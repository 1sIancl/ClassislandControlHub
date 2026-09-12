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
        var user = await store.GetUserByUsernameAsync(username?.Trim() ?? string.Empty, cancellationToken);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            await store.AddAuditAsync(username ?? "(空)", "admin.login.failed", username ?? string.Empty,
                "用户名或密码错误。", ipAddress, cancellationToken);
            throw new HubException(HubErrorCodes.AuthInvalid, "用户名或密码错误。", 401);
        }

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
