using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Services;

/// <summary>
/// 管理员认证服务：首次启动播种默认账号、登录、会话校验与改密。
/// </summary>
public sealed class AdminAuthService(
    HubStore store,
    IOptions<ServerOptions> options,
    ILogger<AdminAuthService> logger)
{
    private readonly ServerOptions _options = options.Value;

    /// <summary>
    /// 两步验证的「半程票据」：密码校验通过、验证码还没校验时的中间状态。
    /// <para>只存在内存里、5 分钟过期，进程重启即失效——它不是一个能当令牌用的东西。</para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TotpTicket> _totpTickets = new();

    private sealed record TotpTicket(string UserId, DateTimeOffset ExpiresAt);

    /// <summary>
    /// 确保至少存在一个管理员账号。仅在数据库为空时执行。
    /// </summary>
    public async Task EnsureSeedAdminAsync(CancellationToken cancellationToken = default)
    {
        if (await store.CountUsersAsync(cancellationToken) > 0)
        {
            return;
        }

        var user = new UserRow
        {
            Id = HubChecksum.NewId(),
            Username = _options.DefaultAdminUser,
            PasswordHash = PasswordHasher.Hash(_options.DefaultAdminPassword),
            DisplayName = "系统管理员",
            Role = "admin",
            // 首次登录必须改密，避免默认口令长期留存。
            MustChangePassword = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateUserAsync(user, cancellationToken);
        await store.AddAuditAsync("system", "admin.seed", user.Username,
            "首次启动，已创建默认管理员账号。", null, cancellationToken);

        logger.LogWarning(
            "已创建默认管理员账号 {User}，初始密码为 {Password}，请登录后立即修改。",
            user.Username, _options.DefaultAdminPassword);
    }

    /// <summary>登录并签发会话令牌。</summary>
    public async Task<LoginResponse> LoginAsync(string username, string password,
        string? ipAddress, CancellationToken cancellationToken = default)
    {
        var trimmed = username?.Trim() ?? string.Empty;

        // 账号临时锁定：连续失败达到阈值后先拒绝，避免在线暴力尝试。
        // 只按用户名计数（不按来源 IP）：学校出口 IP 常常只有一个，按 IP 会因一台机器被扫而误伤所有人。
        if (_options.LoginMaxFailures > 0
            && await store.CountRecentLoginFailuresAsync(trimmed, _options.LoginLockoutMinutes, cancellationToken)
                >= _options.LoginMaxFailures)
        {
            await store.AddAuditAsync(trimmed, "admin.login.locked", trimmed,
                $"连续 {_options.LoginMaxFailures} 次登录失败，已临时锁定 {_options.LoginLockoutMinutes} 分钟。",
                ipAddress, cancellationToken);

            throw new HubException(HubErrorCodes.AccountLocked,
                $"连续登录失败次数过多，账号已临时锁定，请 {_options.LoginLockoutMinutes} 分钟后再试。", 429);
        }

        var user = await store.GetUserByUsernameAsync(trimmed, cancellationToken);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            await store.AddLoginAttemptAsync(trimmed, ipAddress, success: false, cancellationToken);
            await store.AddAuditAsync(username ?? "(空)", "admin.login.failed", trimmed,
                "用户名或密码错误。", ipAddress, cancellationToken);
            throw new HubException(HubErrorCodes.AuthInvalid, "用户名或密码错误。", 401);
        }

        // 密码正确即清空失败计数：成功登录后不应再被之前的失败拖累。
        await store.AddLoginAttemptAsync(trimmed, ipAddress, success: true, cancellationToken);
        await store.ClearLoginFailuresAsync(trimmed, cancellationToken);

        // 启用了两步验证：先发一张 5 分钟有效的「半程票据」，验证码通过后才签发正式会话。
        if (user.TotpEnabled)
        {
            var ticket = HubChecksum.NewToken(32);
            _totpTickets[ticket] = new TotpTicket(user.Id, DateTimeOffset.UtcNow.AddMinutes(5));

            await store.AddAuditAsync(user.Username, "admin.login.totp", user.Username,
                "密码校验通过，等待两步验证码。", ipAddress, cancellationToken);

            return new LoginResponse
            {
                NeedTotp = true,
                TotpTicket = ticket,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
                Role = user.Role,
            };
        }

        return await IssueSessionAsync(user, ipAddress, cancellationToken);
    }

    /// <summary>完成两步验证并签发正式会话。</summary>
    public async Task<LoginResponse> CompleteTotpLoginAsync(string ticket, string code, string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket) || !_totpTickets.TryRemove(ticket.Trim(), out var pending))
        {
            throw new HubException(HubErrorCodes.AuthInvalid, "验证已超时，请重新登录。", 401);
        }

        if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new HubException(HubErrorCodes.AuthInvalid, "验证已超时，请重新登录。", 401);
        }

        var user = await store.GetUserAsync(pending.UserId, cancellationToken)
                   ?? throw new HubException(HubErrorCodes.AuthInvalid, "账号不存在。", 401);

        if (!TotpService.Verify(user.TotpSecret, code))
        {
            await store.AddAuditAsync(user.Username, "admin.login.totp.failed", user.Username,
                "两步验证码错误。", ipAddress, cancellationToken);
            throw new HubException(HubErrorCodes.AuthInvalid, "验证码不正确，请重新输入。", 401);
        }

        return await IssueSessionAsync(user, ipAddress, cancellationToken);
    }

    /// <summary>
    /// 生成（或重置）待绑定的 TOTP 密钥：此时尚未启用，
    /// 需要用验证器算出一次验证码再调用 <see cref="EnableTotpAsync"/> 才算绑定成功。
    /// </summary>
    public async Task<(string Secret, string OtpAuthUrl)> PrepareTotpAsync(UserRow user, string issuer,
        CancellationToken cancellationToken = default)
    {
        var secret = TotpService.GenerateSecret();
        await store.SetUserTotpAsync(user.Id, secret, false, cancellationToken);
        return (secret, TotpService.BuildOtpAuthUrl(issuer, user.Username, secret));
    }

    /// <summary>校验验证码并启用两步验证。</summary>
    public async Task EnableTotpAsync(UserRow user, string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user.TotpSecret))
        {
            throw HubException.Validation("请先生成密钥并把它录入验证器。");
        }

        if (!TotpService.Verify(user.TotpSecret, code))
        {
            throw HubException.Validation("验证码不正确，请确认验证器时间准确后重试。");
        }

        await store.SetUserTotpAsync(user.Id, user.TotpSecret, true, cancellationToken);
    }

    /// <summary>关闭两步验证并清除密钥。</summary>
    public Task DisableTotpAsync(UserRow user, CancellationToken cancellationToken = default)
        => store.SetUserTotpAsync(user.Id, string.Empty, false, cancellationToken);

    /// <summary>签发会话令牌（密码直登与两步验证通过后的公共收尾）。</summary>
    private async Task<LoginResponse> IssueSessionAsync(UserRow user, string? ipAddress,
        CancellationToken cancellationToken)
    {
        var token = HubChecksum.NewToken(48);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(Math.Max(1, _options.SessionLifetimeHours));
        await store.CreateSessionAsync(token, user.Id, expiresAt, cancellationToken);

        await store.AddAuditAsync(user.Username, "admin.login", user.Username,
            "登录成功。", ipAddress, cancellationToken);

        return new LoginResponse
        {
            Token = token,
            ExpiresAt = expiresAt,
            DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
            Role = user.Role,
        };
    }

    /// <summary>校验会话令牌。无效时返回 <c>null</c>。</summary>
    public Task<SessionRow?> ValidateAsync(string? token, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(token)
            ? Task.FromResult<SessionRow?>(null)
            : store.GetSessionAsync(token, cancellationToken);

    /// <summary>登出。</summary>
    public async Task LogoutAsync(string token, CancellationToken cancellationToken = default)
    {
        await store.DeleteSessionAsync(token, cancellationToken);
    }

    /// <summary>修改密码。成功后会使该账号的全部会话失效。</summary>
    public async Task ChangePasswordAsync(UserRow user, string currentPassword, string newPassword,
        string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
        {
            throw HubException.Validation("原密码不正确。");
        }

        var strengthIssue = PasswordHasher.ValidateStrength(newPassword);
        if (strengthIssue is not null)
        {
            throw HubException.Validation(strengthIssue);
        }

        if (PasswordHasher.Verify(newPassword, user.PasswordHash))
        {
            throw HubException.Validation("新密码不能与原密码相同。");
        }

        await store.UpdatePasswordAsync(user.Id, PasswordHasher.Hash(newPassword), false, cancellationToken);
        await store.DeleteUserSessionsAsync(user.Id, cancellationToken);
        await store.AddAuditAsync(user.Username, "admin.password.changed", user.Username,
            "管理员修改了登录密码。", ipAddress, cancellationToken);
    }

    /// <summary>重置指定账号的密码（管理员操作）。</summary>
    public async Task ResetPasswordAsync(string targetUserId, string newPassword,
        string actor, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var strengthIssue = PasswordHasher.ValidateStrength(newPassword);
        if (strengthIssue is not null)
        {
            throw HubException.Validation(strengthIssue);
        }

        var target = await store.GetUserAsync(targetUserId, cancellationToken)
                     ?? throw HubException.NotFound("账号不存在。");

        await store.UpdatePasswordAsync(target.Id, PasswordHasher.Hash(newPassword), true, cancellationToken);
        await store.DeleteUserSessionsAsync(target.Id, cancellationToken);
        await store.AddAuditAsync(actor, "admin.password.reset", target.Username,
            "管理员重置了该账号的密码。", ipAddress, cancellationToken);
    }
}
