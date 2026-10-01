using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Options;
using ControlHub.Server.Services;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 管理端认证、仪表盘与审计日志接口，挂载在 <c>/api/v1/admin</c> 下。
/// </summary>
public static class AdminEndpoints
{
    /// <summary>注册管理端路由。</summary>
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // 公开接口：登录页需要在未登录时读取服务器名称等信息。
        app.MapGet($"{HubProtocol.ApiPrefix}/server/info", ServerInfoAsync)
            .WithTags("public")
            .AllowAnonymous();

        var group = app.NewVersionedGroup("admin");

        // ── 公开接口（无需登录）──
        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/login/totp", TotpLoginAsync).AllowAnonymous();
        group.MapGet("/registration", GetRegistrationInfoAsync).AllowAnonymous();
        group.MapPost("/register", RegisterAsync).AllowAnonymous();

        // 权限过滤器必须在 AdminAuthFilter 之后：前者负责校验会话，后者按路由元数据校验权限。
        var authed = group.MapGroup(string.Empty)
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        // ── 与登录状态本身相关：只要求已登录 ──
        authed.MapPost("/logout", LogoutAsync).RequirePermission(PermissionKeys.Authenticated);
        authed.MapGet("/me", MeAsync).RequirePermission(PermissionKeys.Authenticated);
        authed.MapPost("/password", ChangePasswordAsync).RequirePermission(PermissionKeys.Authenticated);
        authed.MapPost("/onboarding", CompleteOnboardingAsync).RequirePermission(PermissionKeys.Authenticated);

        // 两步验证（TOTP）：绑定 / 启用 / 关闭，都是「对自己的账号」操作，只需已登录
        authed.MapPost("/totp/setup", TotpSetupAsync).RequirePermission(PermissionKeys.Authenticated);
        authed.MapPost("/totp/enable", TotpEnableAsync).RequirePermission(PermissionKeys.Authenticated);
        authed.MapPost("/totp/disable", TotpDisableAsync).RequirePermission(PermissionKeys.Authenticated);
        authed.MapGet("/dashboard", DashboardAsync).RequirePermission(PermissionKeys.Authenticated);

        // ── 审计 ──
        authed.MapGet("/audit", AuditAsync).RequirePermission(PermissionKeys.AuditRead);

        // ── 账号与权限 ──
        authed.MapGet("/permissions", PermissionsAsync).RequirePermission(PermissionKeys.AccountsRead);
        authed.MapGet("/permission-presets", PermissionPresetsAsync).RequirePermission(PermissionKeys.AccountsRead);
        authed.MapGet("/accounts", AccountsAsync).RequirePermission(PermissionKeys.AccountsRead);
        authed.MapPost("/accounts", CreateAccountAsync).RequirePermission(PermissionKeys.AccountsWrite);
        authed.MapPut("/accounts/{id}", UpdateAccountAsync).RequirePermission(PermissionKeys.AccountsWrite);
        authed.MapDelete("/accounts/{id}", DeleteAccountAsync).RequirePermission(PermissionKeys.AccountsWrite);
        authed.MapPost("/accounts/{id}/reset-password", ResetPasswordAsync)
            .RequirePermission(PermissionKeys.AccountsWrite);

        // ── 邀请码与自助注册开关 ──
        authed.MapGet("/register-codes", ListRegisterCodesAsync).RequirePermission(PermissionKeys.AccountsRead);
        authed.MapPost("/register-codes", CreateRegisterCodeAsync).RequirePermission(PermissionKeys.AccountsWrite);
        authed.MapDelete("/register-codes/{code}", DeleteRegisterCodeAsync)
            .RequirePermission(PermissionKeys.AccountsWrite);
        authed.MapPut("/registration", SetRegistrationAsync).RequirePermission(PermissionKeys.AccountsWrite);

        // ── 系统设置 ──
        authed.MapGet("/branding", GetBrandingAsync).RequirePermission(PermissionKeys.SettingsRead);
        authed.MapPut("/branding", SetBrandingAsync).RequirePermission(PermissionKeys.SettingsWrite);
        authed.MapGet("/time-offset", GetTimeOffsetAsync).RequirePermission(PermissionKeys.SettingsRead);
        authed.MapPut("/time-offset", SetTimeOffsetAsync).RequirePermission(PermissionKeys.SettingsWrite);
        authed.MapGet("/update/state", GetUpdateStateAsync).RequirePermission(PermissionKeys.SettingsRead);
        authed.MapGet("/update/check", CheckUpdateAsync).RequirePermission(PermissionKeys.SettingsRead);
        authed.MapPost("/update/apply", ApplyUpdateAsync).RequirePermission(PermissionKeys.SettingsWrite);
    }

    /// <summary>服务器基础信息。无需登录即可访问。</summary>
    private static async Task<ApiResult<ServerInfoDto>> ServerInfoAsync(
        HubStore store,
        SyncService sync,
        IOptions<ServerOptions> options,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var revision = await store.GetRevisionAsync(cancellationToken);
        var devices = await store.GetDevicesAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var online = devices.Count(d => !d.Revoked && d.LastSeenAt.HasValue
            && (now - d.LastSeenAt.Value).TotalSeconds < opts.OnlineTimeoutSeconds);

        return ApiResult<ServerInfoDto>.Success(new ServerInfoDto
        {
            ServerName = opts.ServerName,
            Version = HubProtocol.ProductVersion,
            ProtocolVersion = HubProtocol.Version,
            Revision = revision,
            RequiresEnrollCode = opts.RequireEnrollCode,
            PublicBaseUrl = opts.PublicBaseUrl,
            DiscoveryEnabled = opts.EnableDiscovery,
            DeviceCount = devices.Count,
            OnlineDeviceCount = online,
            PendingDeviceCount = devices.Count(d => !d.Revoked && d.AppliedRevision < revision),
            StartedAt = ServerRuntime.StartedAt,
            UptimeSeconds = (long)ServerRuntime.Uptime.TotalSeconds,
            DataDirectory = opts.ResolveDataDirectory(environment.ContentRootPath),
            HttpPort = opts.HttpPort,
            DiscoveryPort = opts.DiscoveryPort,
            NtpServerEnabled = opts.EnableNtpServer,
            NtpPort = opts.NtpPort,
            Branding = await LoadBrandingAsync(store, cancellationToken),
        });
    }

    /// <summary>读取品牌个性化配置，缺省返回默认值。</summary>
    private static async Task<BrandingDto> LoadBrandingAsync(HubStore store, CancellationToken cancellationToken)
    {
        var json = await store.GetSettingAsync("branding", null, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BrandingDto();
        }

        try
        {
            return JsonSerializer.Deserialize<BrandingDto>(json) ?? new BrandingDto();
        }
        catch
        {
            return new BrandingDto();
        }
    }

    /// <summary>管理员登录。</summary>
    private static async Task<ApiResult<LoginResponse>> LoginAsync(
        LoginRequest request,
        HttpContext http,
        AdminAuthService auth,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw HubException.Validation("请输入用户名和密码。");
        }

        var response = await auth.LoginAsync(request.Username, request.Password,
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<LoginResponse>.Success(response);
    }

    /// <summary>完成两步验证：用半程票据 + 6 位验证码换取正式令牌。</summary>
    private static async Task<ApiResult<LoginResponse>> TotpLoginAsync(
        TotpLoginRequest request,
        HttpContext http,
        AdminAuthService auth,
        CancellationToken cancellationToken)
    {
        var response = await auth.CompleteTotpLoginAsync(request.Ticket ?? string.Empty, request.Code ?? string.Empty,
            http.GetClientIpAddress(), cancellationToken);
        return ApiResult<LoginResponse>.Success(response);
    }

    /// <summary>生成待绑定的 TOTP 密钥（此时还没启用），返回密钥与 otpauth 链接。</summary>
    private static async Task<ApiResult<TotpSetupDto>> TotpSetupAsync(
        HttpContext http,
        HubStore store,
        AdminAuthService auth,
        IOptions<ServerOptions> options,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var user = await store.GetUserAsync(session.UserId, cancellationToken)
                   ?? throw HubException.NotFound("账号不存在。");

        var issuer = string.IsNullOrWhiteSpace(options.Value.ServerName)
            ? "ClassislandControlHub"
            : options.Value.ServerName;

        var (secret, url) = await auth.PrepareTotpAsync(user, issuer, cancellationToken);
        return ApiResult<TotpSetupDto>.Success(new TotpSetupDto { Secret = secret, OtpAuthUrl = url });
    }

    /// <summary>校验一次验证码并启用两步验证。</summary>
    private static async Task<ApiResult<bool>> TotpEnableAsync(
        TotpEnableRequest request,
        HttpContext http,
        HubStore store,
        AdminAuthService auth,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var user = await store.GetUserAsync(session.UserId, cancellationToken)
                   ?? throw HubException.NotFound("账号不存在。");

        await auth.EnableTotpAsync(user, request.Code ?? string.Empty, cancellationToken);
        await store.AddAuditAsync(session.Username, "totp.enable", session.Username,
            "启用了两步验证（TOTP）。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    /// <summary>关闭两步验证（需要当前密码确认，避免会话被劫持后直接关掉）。</summary>
    private static async Task<ApiResult<bool>> TotpDisableAsync(
        TotpDisableRequest request,
        HttpContext http,
        HubStore store,
        AdminAuthService auth,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var user = await store.GetUserAsync(session.UserId, cancellationToken)
                   ?? throw HubException.NotFound("账号不存在。");

        if (!PasswordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
        {
            throw HubException.Validation("当前密码不正确。");
        }

        await auth.DisableTotpAsync(user, cancellationToken);
        await store.AddAuditAsync(session.Username, "totp.disable", session.Username,
            "关闭了两步验证（TOTP）。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    /// <summary>登出。</summary>
    private static async Task<ApiResult<bool>> LogoutAsync(
        HttpContext http,
        AdminAuthService auth,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        await auth.LogoutAsync(session.Token, cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    /// <summary>获取当前登录账号信息（含权限集合与新手引导状态，前端据此过滤导航与决定是否放引导）。</summary>
    private static async Task<ApiResult<object>> MeAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var user = await store.GetUserAsync(session.UserId, cancellationToken);

        return ApiResult<object>.Success(new
        {
            id = session.UserId,
            username = session.Username,
            displayName = string.IsNullOrWhiteSpace(session.DisplayName) ? session.Username : session.DisplayName,
            role = session.Role,
            // 超级管理员直接展开为全部权限，前端导航无需再特殊处理。
            permissions = IsAdministratorRole(session.Role) ? PermissionKeys.Grantable : session.Permissions,
            expiresAt = session.ExpiresAt,
            mustChangePassword = user?.MustChangePassword ?? false,
            totpEnabled = user?.TotpEnabled ?? false,
            onboardingDone = await store.GetSettingAsync(OnboardingKey(session.UserId), "0", cancellationToken) == "1",
        });
    }

    /// <summary>修改当前账号密码。</summary>
    private static async Task<ApiResult<bool>> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext http,
        HubStore store,
        AdminAuthService auth,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        // 本地外壳不是数据库里的账号，没有密码可改。
        if (LocalShellTrust.IsLocalShell(session))
        {
            throw HubException.Validation("本地控制台不需要密码；账号密码请在「系统设置 → 账号与权限」里维护。");
        }

        var user = await store.GetUserAsync(session.UserId, cancellationToken)
                   ?? throw HubException.NotFound("账号不存在。");

        await auth.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword,
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    /// <summary>仪表盘统计。</summary>
    private static async Task<ApiResult<DashboardStatsDto>> DashboardAsync(
        HttpContext http,
        HubStore store,
        IOptions<ServerOptions> options,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();

        var opts = options.Value;
        var revision = await store.GetRevisionAsync(cancellationToken);
        var devices = await store.GetDevicesAsync(cancellationToken);
        var summaries = await store.GetGroupsAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var online = devices.Count(d => !d.Revoked && d.LastSeenAt.HasValue
            && (now - d.LastSeenAt.Value).TotalSeconds < opts.OnlineTimeoutSeconds);

        var pending = devices.Count(d => !d.Revoked && d.AppliedRevision < revision);
        var errors = devices.Count(d => !d.Revoked
            && string.Equals(d.State, DeviceStates.Error, StringComparison.OrdinalIgnoreCase));

        var recentEvents = (await store.GetRecentAuditAsync(12, cancellationToken))
            .Select(ToAuditDto)
            .ToList();

        return ApiResult<DashboardStatsDto>.Success(new DashboardStatsDto
        {
            DeviceCount = devices.Count,
            OnlineDeviceCount = online,
            OfflineDeviceCount = devices.Count - online,
            PendingDeviceCount = pending,
            ErrorDeviceCount = errors,
            GroupCount = summaries.Count,
            ProfileCount = await store.CountProfilesAsync(cancellationToken),
            Revision = revision,
            RecentEnrollCount = devices.Count(d => (now - d.CreatedAt).TotalHours <= 24),
            RecentEvents = recentEvents,
        });
    }

    /// <summary>分页查询审计日志。</summary>
    private static async Task<ApiResult<PagedResult<AuditLogDto>>> AuditAsync(
        HttpContext http,
        HubStore store,
        int? page,
        int? pageSize,
        string? search,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();

        var (items, total) = await store.GetAuditLogsAsync(page ?? 1, pageSize ?? 50, search, cancellationToken);

        return ApiResult<PagedResult<AuditLogDto>>.Success(new PagedResult<AuditLogDto>
        {
            Items = items.Select(ToAuditDto).ToList(),
            Total = total,
            Page = page ?? 1,
            PageSize = pageSize ?? 50,
        });
    }

    /// <summary>列出管理员账号（含各自的权限集合）。</summary>
    private static async Task<ApiResult<List<AccountDto>>> AccountsAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var users = await store.GetUsersAsync(cancellationToken);
        return ApiResult<List<AccountDto>>.Success(users.Select(ToAccountDto).ToList());
    }

    /// <summary>重置指定账号密码。</summary>
    private static async Task<ApiResult<bool>> ResetPasswordAsync(
        string id,
        ChangePasswordRequest request,
        HttpContext http,
        HubStore store,
        AdminAuthService auth,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var target = await store.GetUserAsync(id, cancellationToken)
                     ?? throw HubException.NotFound("账号不存在。");
        EnsureCanManage(session, target);

        await auth.ResetPasswordAsync(id, request.NewPassword, session.Username,
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    /// <summary>读取品牌个性化配置。</summary>
    private static async Task<ApiResult<BrandingDto>> GetBrandingAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        return ApiResult<BrandingDto>.Success(await LoadBrandingAsync(store, cancellationToken));
    }

    /// <summary>保存品牌个性化配置。</summary>
    private static async Task<ApiResult<BrandingDto>> SetBrandingAsync(
        BrandingDto request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        request.SiteName = (request.SiteName ?? string.Empty).Trim();
        request.LogoText = (request.LogoText ?? string.Empty).Trim();
        await store.SetSettingAsync("branding", JsonSerializer.Serialize(request), cancellationToken);
        await store.AddAuditAsync(session.Username, "branding.updated", "branding", "更新站点品牌配置",
            http.GetClientIpAddress(), cancellationToken);
        return ApiResult<BrandingDto>.Success(request);
    }

    /// <summary>读取手动时间偏移及当前授时状态。</summary>
    private static ApiResult<object> GetTimeOffsetAsync(
        HttpContext http,
        ServerTimeService serverTime)
    {
        http.RequireAdminSession();
        return ApiResult<object>.Success(new
        {
            offsetSeconds = serverTime.ManualOffsetSeconds,
            ntpOffsetSeconds = serverTime.OffsetSeconds,
            serverTime = serverTime.GetUtcNow(),
            lastSyncStatus = serverTime.LastSyncStatus,
            lastSyncAt = serverTime.LastSyncAt,
        });
    }

    /// <summary>设置手动时间偏移（秒），立即叠加到对外授时的时间上。</summary>
    private static async Task<ApiResult<object>> SetTimeOffsetAsync(
        TimeOffsetRequest request,
        HttpContext http,
        HubStore store,
        ServerTimeService serverTime,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        var seconds = Math.Clamp(request.OffsetSeconds, -86400, 86400); // 限制在 ±24 小时内。
        await store.SetSettingAsync("timeOffsetSeconds",
            seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        serverTime.SetManualOffsetSeconds(seconds);

        await store.AddAuditAsync(session.Username, "timeoffset.updated", "timeOffsetSeconds",
            $"设置时间偏移 {seconds:F3} 秒。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<object>.Success(new
        {
            offsetSeconds = seconds,
            serverTime = serverTime.GetUtcNow(),
        });
    }

    /// <summary>获取当前自动更新状态（不触发检查）。</summary>
    private static ApiResult<UpdateState> GetUpdateStateAsync(
        HttpContext http,
        UpdateService update)
    {
        http.RequireAdminSession();
        return ApiResult<UpdateState>.Success(update.GetState());
    }

    /// <summary>立即检查一次更新并返回最新状态。</summary>
    private static async Task<ApiResult<UpdateState>> CheckUpdateAsync(
        HttpContext http,
        UpdateService update,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var state = await update.CheckForUpdateAsync(cancellationToken);
        await store.AddAuditAsync(session.Username, "update.check", "更新",
            $"检查更新：{state.Status}", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<UpdateState>.Success(state);
    }

    /// <summary>立即下载并应用更新（保留数据，完成后自动重启）。</summary>
    private static async Task<ApiResult<UpdateState>> ApplyUpdateAsync(
        HttpContext http,
        UpdateService update,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        await store.AddAuditAsync(session.Username, "update.apply", "更新",
            "发起自动更新。", http.GetClientIpAddress(), cancellationToken);
        var state = await update.ApplyUpdateAsync(cancellationToken);
        return ApiResult<UpdateState>.Success(state);
    }

    // ────────────────────────────── 账号、权限与自助注册 ──────────────────────────────

    /// <summary>自助注册开关存放在设置表里，便于在界面上一键开关（默认关闭）。</summary>
    private const string SelfRegistrationSettingKey = "selfRegistrationEnabled";

    /// <summary>新手引导是否已完成的设置项键（按用户隔离）。</summary>
    private static string OnboardingKey(string userId) => $"onboarding_done.{userId}";

    /// <summary>是否超级管理员角色。</summary>
    private static bool IsAdministratorRole(string? role) =>
        string.Equals(role, PermissionKeys.AdministratorRole, StringComparison.OrdinalIgnoreCase);

    private static string DescribeRole(string role) =>
        IsAdministratorRole(role) ? "超级管理员" : "自定义权限";

    private static AccountDto ToAccountDto(UserRow user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        DisplayName = user.DisplayName,
        Role = IsAdministratorRole(user.Role) ? PermissionKeys.AdministratorRole : PermissionKeys.CustomRole,
        // 超级管理员始终展开为全部权限，界面上直接呈现「全选」。
        Permissions = IsAdministratorRole(user.Role)
            ? PermissionKeys.Grantable
            : PermissionKeys.Normalize(user.Permissions),
        MustChangePassword = user.MustChangePassword,
        CreatedAt = user.CreatedAt,
    };

    private static RegisterCodeDto ToRegisterCodeDto(RegisterCodeRow row) => new()
    {
        Code = row.Code,
        Note = row.Note,
        Permissions = PermissionKeys.Normalize(row.Permissions),
        MaxUses = row.MaxUses,
        UsedCount = row.UsedCount,
        RemainingUses = row.MaxUses <= 0 ? -1 : Math.Max(0, row.MaxUses - row.UsedCount),
        ExpiresAt = row.ExpiresAt,
        CreatedAt = row.CreatedAt,
    };

    /// <summary>只有超级管理员能管理超级管理员账号，避免自定义权限账号把超管踢掉。</summary>
    private static void EnsureCanManage(SessionRow session, UserRow target)
    {
        if (IsAdministratorRole(target.Role) && !IsAdministratorRole(session.Role))
        {
            throw HubException.Forbidden("只有超级管理员可以管理超级管理员账号。");
        }
    }

    /// <summary>
    /// 提权防护：非超级管理员既不能把别人设为超级管理员，也不能授予自己没有的权限。
    /// </summary>
    internal static void EnsureCanGrant(SessionRow session, string role, IEnumerable<string> permissions)
    {
        if (IsAdministratorRole(session.Role))
        {
            return;
        }

        if (IsAdministratorRole(role))
        {
            throw HubException.Forbidden("只有超级管理员可以把账号设为超级管理员。");
        }

        var missing = PermissionKeys.Normalize(permissions)
            .Where(p => !PermissionKeys.Satisfies(session.Role, session.Permissions, p))
            .ToList();

        if (missing.Count > 0)
        {
            throw HubException.Forbidden(
                "不能授予自己没有的权限：" + string.Join("、", missing.Select(PermissionCatalog.Describe)) + "。");
        }
    }

    /// <summary>降级或删除超级管理员前的保护：系统至少要留一个。</summary>
    private static async Task EnsureNotLastAdministratorAsync(HubStore store, CancellationToken cancellationToken)
    {
        var users = await store.GetUsersAsync(cancellationToken);
        if (users.Count(u => IsAdministratorRole(u.Role)) <= 1)
        {
            throw HubException.Validation("系统至少需要保留一个超级管理员账号。");
        }
    }

    /// <summary>校验用户名格式（新建账号、邀请码注册与自助申请共用）。</summary>
    internal static string NormalizeUsername(string? username)
    {
        var value = (username ?? string.Empty).Trim();
        if (value.Length < 3 || value.Length > 32)
        {
            throw HubException.Validation("用户名长度需在 3~32 个字符之间。");
        }

        if (!value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.'))
        {
            throw HubException.Validation("用户名只能包含字母、数字、下划线、减号与点。");
        }

        return value;
    }

    /// <summary>校验密码强度，不通过时抛出可读的业务异常。</summary>
    private static void EnsurePasswordStrength(string password)
    {
        var error = PasswordHasher.ValidateStrength(password);
        if (!string.IsNullOrEmpty(error))
        {
            throw HubException.Validation(error);
        }
    }

    /// <summary>权限目录：账号界面据此渲染「查看 / 修改」勾选表。</summary>
    private static ApiResult<List<PermissionModuleDto>> PermissionsAsync(HttpContext http)
    {
        http.RequireAdminSession();
        return ApiResult<List<PermissionModuleDto>>.Success(PermissionCatalog.ToDto());
    }

    /// <summary>角色模板：账号界面「快速套用」岗位权限组合。</summary>
    private static ApiResult<List<PermissionPresetDto>> PermissionPresetsAsync(HttpContext http)
    {
        http.RequireAdminSession();
        return ApiResult<List<PermissionPresetDto>>.Success(PermissionCatalog.ToPresetDto());
    }

    /// <summary>新建账号。未指定密码时由系统生成，并在响应里回传一次。</summary>
    private static async Task<ApiResult<AccountCreatedDto>> CreateAccountAsync(
        AccountUpsertRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var username = NormalizeUsername(request.Username);

        if (await store.GetUserByUsernameAsync(username, cancellationToken) is not null)
        {
            throw HubException.Conflict("该用户名已被占用。");
        }

        var role = IsAdministratorRole(request.Role) ? PermissionKeys.AdministratorRole : PermissionKeys.CustomRole;
        var permissions = role == PermissionKeys.AdministratorRole
            ? []
            : PermissionKeys.Normalize(request.Permissions ?? PermissionKeys.DefaultForNewUser);
        EnsureCanGrant(session, role, permissions);

        var generated = string.IsNullOrWhiteSpace(request.Password);
        var password = generated ? HubChecksum.NewEnrollCode(12).ToLowerInvariant() + "A1" : request.Password!;
        EnsurePasswordStrength(password);

        var user = new UserRow
        {
            Id = HubChecksum.NewId(),
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim(),
            Role = role,
            Permissions = permissions,
            // 系统生成的初始密码强制首次登录修改。
            MustChangePassword = generated,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateUserAsync(user, cancellationToken);
        await store.AddAuditAsync(session.Username, "account.create", user.Username,
            $"新建账号（{DescribeRole(role)}，权限 {permissions.Count} 项）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<AccountCreatedDto>.Success(new AccountCreatedDto
        {
            Account = ToAccountDto(user),
            GeneratedPassword = generated ? password : null,
        });
    }

    /// <summary>修改账号的显示名、角色与权限。权限变化会让该账号的会话立即失效（需重新登录）。</summary>
    private static async Task<ApiResult<AccountDto>> UpdateAccountAsync(
        string id,
        AccountUpsertRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var target = await store.GetUserAsync(id, cancellationToken)
                     ?? throw HubException.NotFound("账号不存在。");
        EnsureCanManage(session, target);

        var role = IsAdministratorRole(request.Role) ? PermissionKeys.AdministratorRole : PermissionKeys.CustomRole;
        var permissions = role == PermissionKeys.AdministratorRole
            ? []
            : PermissionKeys.Normalize(request.Permissions ?? target.Permissions);
        EnsureCanGrant(session, role, permissions);

        var isSelf = string.Equals(session.UserId, id, StringComparison.Ordinal);
        if (isSelf && !IsAdministratorRole(role))
        {
            throw HubException.Validation("不能取消自己的超级管理员角色。");
        }

        if (IsAdministratorRole(target.Role) && !IsAdministratorRole(role))
        {
            await EnsureNotLastAdministratorAsync(store, cancellationToken);
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? target.DisplayName : request.DisplayName.Trim();
        await store.UpdateUserAsync(id, displayName, role, permissions, cancellationToken);

        // 会话里缓存了权限，改动后必须让该账号重新登录才会生效。
        var changed = !IsAdministratorRole(role) && !target.Permissions.OrderBy(p => p).SequenceEqual(permissions.OrderBy(p => p))
                      || !string.Equals(target.Role, role, StringComparison.OrdinalIgnoreCase);
        if (changed)
        {
            await store.DeleteUserSessionsAsync(id, cancellationToken);
        }

        await store.AddAuditAsync(session.Username, "account.update", target.Username,
            $"更新账号（{DescribeRole(role)}，权限 {permissions.Count} 项）。",
            http.GetClientIpAddress(), cancellationToken);

        var updated = await store.GetUserAsync(id, cancellationToken) ?? target;
        return ApiResult<AccountDto>.Success(ToAccountDto(updated));
    }

    /// <summary>删除账号（连同它的会话与个人提醒）。</summary>
    private static async Task<ApiResult<bool>> DeleteAccountAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var target = await store.GetUserAsync(id, cancellationToken)
                     ?? throw HubException.NotFound("账号不存在。");
        EnsureCanManage(session, target);

        if (string.Equals(session.UserId, id, StringComparison.Ordinal))
        {
            throw HubException.Validation("不能删除当前登录的账号。");
        }

        if (IsAdministratorRole(target.Role))
        {
            await EnsureNotLastAdministratorAsync(store, cancellationToken);
        }

        await store.DeleteUserAsync(id, cancellationToken);
        await store.AddAuditAsync(session.Username, "account.delete", target.Username,
            "删除账号（含其个人提醒与会话）。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    // ────────────────────────────── 邀请码与自助注册 ──────────────────────────────

    private static async Task<ApiResult<List<RegisterCodeDto>>> ListRegisterCodesAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var rows = await store.GetRegisterCodesAsync(cancellationToken);
        return ApiResult<List<RegisterCodeDto>>.Success(rows.Select(ToRegisterCodeDto).ToList());
    }

    /// <summary>生成邀请码。凭码注册的账号会拿到这里预设的权限。</summary>
    private static async Task<ApiResult<RegisterCodeDto>> CreateRegisterCodeAsync(
        RegisterCodeUpsertRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        var permissions = PermissionKeys.Normalize(request.Permissions ?? PermissionKeys.DefaultForNewUser);
        if (permissions.Count == 0)
        {
            throw HubException.Validation("请至少勾选一项权限，否则注册出来的账号什么都做不了。");
        }

        EnsureCanGrant(session, PermissionKeys.CustomRole, permissions);

        var row = new RegisterCodeRow
        {
            Code = HubChecksum.NewEnrollCode(),
            Note = (request.Note ?? string.Empty).Trim(),
            Permissions = permissions,
            MaxUses = Math.Max(0, request.MaxUses),
            UsedCount = 0,
            ExpiresAt = request.ValidHours > 0 ? DateTimeOffset.UtcNow.AddHours(request.ValidHours) : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateRegisterCodeAsync(row, cancellationToken);
        await store.AddAuditAsync(session.Username, "register-code.create", row.Code,
            $"生成邀请码（权限 {permissions.Count} 项，可用 {row.MaxUses} 次）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<RegisterCodeDto>.Success(ToRegisterCodeDto(row));
    }

    private static async Task<ApiResult<bool>> DeleteRegisterCodeAsync(
        string code,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        await store.DeleteRegisterCodeAsync(code.ToUpperInvariant(), cancellationToken);
        await store.AddAuditAsync(session.Username, "register-code.delete", code,
            "删除了邀请码。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    /// <summary>注册开关的公开状态（登录页据此决定显示哪些注册入口）。</summary>
    private static async Task<ApiResult<RegistrationInfoDto>> GetRegistrationInfoAsync(
        HubStore store,
        CancellationToken cancellationToken)
    {
        var enabled = await store.GetSettingAsync(SelfRegistrationSettingKey, "0", cancellationToken) == "1";
        var approvalEnabled = await RegistrationEndpoints.IsApprovalEnabledAsync(store, cancellationToken);

        return ApiResult<RegistrationInfoDto>.Success(new RegistrationInfoDto
        {
            Enabled = enabled,
            ApprovalEnabled = approvalEnabled,
            Hint = (enabled, approvalEnabled) switch
            {
                (true, true) => "可凭邀请码自助注册，也可提交申请等待管理员审批",
                (true, false) => "凭管理员发放的邀请码自助注册",
                (false, true) => "可提交注册申请，管理员审批通过后即可登录",
                (false, false) => "当前未开放自助注册，请联系管理员开通账号。",
            },
        });
    }

    /// <summary>开关自助注册。默认关闭：A 端是校内控制台，开放注册意味着任何人都能拿到账号。</summary>
    private static async Task<ApiResult<bool>> SetRegistrationAsync(
        bool? enabled,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var value = enabled ?? false;
        await store.SetSettingAsync(SelfRegistrationSettingKey, value ? "1" : "0", cancellationToken);
        await store.AddAuditAsync(session.Username, "registration.switch", "selfRegistration",
            value ? "开启了自助注册。" : "关闭了自助注册。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(value);
    }

    /// <summary>只读校验邀请码（不消耗使用次数），返回失败原因；可用时返回 null。</summary>
    private static string? ValidateRegisterCode(RegisterCodeRow? row)
    {
        if (row is null)
        {
            return "邀请码不存在。";
        }

        if (row.ExpiresAt.HasValue && row.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            return "邀请码已过期。";
        }

        if (row.MaxUses > 0 && row.UsedCount >= row.MaxUses)
        {
            return "邀请码使用次数已用尽。";
        }

        return null;
    }

    /// <summary>凭邀请码自助注册，成功后直接登录（无需再输一次密码）。</summary>
    private static async Task<ApiResult<LoginResponse>> RegisterAsync(
        RegisterRequest request,
        HttpContext http,
        HubStore store,
        AdminAuthService auth,
        CancellationToken cancellationToken)
    {
        if (await store.GetSettingAsync(SelfRegistrationSettingKey, "0", cancellationToken) != "1")
        {
            throw HubException.Forbidden("当前未开放自助注册。");
        }

        var username = NormalizeUsername(request.Username);
        EnsurePasswordStrength(request.Password ?? string.Empty);
        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();

        // 先只读校验邀请码，再查用户名重名：否则没有有效邀请码的人也能靠 400/409 的差异探测用户名是否存在。
        var previewError = ValidateRegisterCode(await store.GetRegisterCodeAsync(code, cancellationToken));
        if (previewError is not null)
        {
            await store.AddAuditAsync(username, "account.register.failed", username,
                $"自助注册失败：{previewError}", http.GetClientIpAddress(), cancellationToken);
            throw new HubException(HubErrorCodes.EnrollCodeInvalid, previewError, 400);
        }

        if (await store.GetUserByUsernameAsync(username, cancellationToken) is not null)
        {
            throw HubException.Conflict("该用户名已被占用。");
        }

        // 真正消耗一次使用次数（内部会在同一事务里再校验一遍，避免并发把次数用超）。
        var (codeRow, error) = await store.ConsumeRegisterCodeAsync(code, cancellationToken);
        if (codeRow is null)
        {
            throw new HubException(HubErrorCodes.EnrollCodeInvalid, error ?? "邀请码无效。", 400);
        }

        var permissions = codeRow.Permissions.Count > 0 ? codeRow.Permissions : PermissionKeys.DefaultForNewUser.ToList();
        var user = new UserRow
        {
            Id = HubChecksum.NewId(),
            Username = username,
            PasswordHash = PasswordHasher.Hash(request.Password!),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim(),
            Role = PermissionKeys.CustomRole,
            Permissions = PermissionKeys.Normalize(permissions),
            MustChangePassword = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateUserAsync(user, cancellationToken);
        await store.AddAuditAsync(username, "account.register", username,
            $"凭邀请码自助注册（权限 {user.Permissions.Count} 项）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<LoginResponse>.Success(
            await auth.LoginAsync(username, request.Password!, http.GetClientIpAddress(), cancellationToken));
    }

    /// <summary>标记新手引导已完成；再次调用可重置（用于「重新观看引导」）。</summary>
    private static async Task<ApiResult<bool>> CompleteOnboardingAsync(
        bool? done,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var value = done ?? true;
        await store.SetSettingAsync(OnboardingKey(session.UserId), value ? "1" : "0", cancellationToken);
        return ApiResult<bool>.Success(value);
    }

    private static AuditLogDto ToAuditDto(AuditLogRow row) => new()
    {
        Id = row.Id,
        Timestamp = row.Timestamp,
        Actor = row.Actor,
        Action = row.Action,
        Target = row.Target,
        Detail = row.Detail,
        IpAddress = row.IpAddress,
    };
}

/// <summary>设置手动时间偏移的请求体。</summary>
public sealed class TimeOffsetRequest
{
    /// <summary>时间偏移（秒），正值表示整体提前。</summary>
    public double OffsetSeconds { get; set; }
}

/// <summary>完成两步验证。</summary>
public sealed class TotpLoginRequest
{
    /// <summary>登录第一步返回的临时票据。</summary>
    public string? Ticket { get; set; }

    /// <summary>验证器 App 显示的 6 位验证码。</summary>
    public string? Code { get; set; }
}

/// <summary>启用两步验证。</summary>
public sealed class TotpEnableRequest
{
    /// <summary>验证器当前显示的 6 位验证码。</summary>
    public string? Code { get; set; }
}

/// <summary>关闭两步验证。</summary>
public sealed class TotpDisableRequest
{
    /// <summary>当前登录密码，用于二次确认。</summary>
    public string? Password { get; set; }
}

/// <summary>两步验证的绑定信息。</summary>
public sealed class TotpSetupDto
{
    /// <summary>Base32 密钥：可手动录入验证器，也可用于生成二维码。</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>otpauth:// 链接（验证器 App 可直接识别）。</summary>
    public string OtpAuthUrl { get; set; } = string.Empty;
}
