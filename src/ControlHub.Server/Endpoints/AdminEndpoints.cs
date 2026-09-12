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

        group.MapPost("/login", LoginAsync).AllowAnonymous();

        var authed = group.MapGroup(string.Empty).AddEndpointFilter<AdminAuthFilter>();

        authed.MapPost("/logout", LogoutAsync);
        authed.MapGet("/me", MeAsync);
        authed.MapPost("/password", ChangePasswordAsync);
        authed.MapGet("/dashboard", DashboardAsync);
        authed.MapGet("/audit", AuditAsync);
        authed.MapGet("/accounts", AccountsAsync);
        authed.MapPost("/accounts/{id}/reset-password", ResetPasswordAsync);
        authed.MapGet("/branding", GetBrandingAsync);
        authed.MapPut("/branding", SetBrandingAsync);
        authed.MapGet("/time-offset", GetTimeOffsetAsync);
        authed.MapPut("/time-offset", SetTimeOffsetAsync);
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
            StartedAt = now - TimeSpan.FromMilliseconds(Environment.TickCount64),
            UptimeSeconds = Environment.TickCount64 / 1000,
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

    /// <summary>获取当前登录账号信息。</summary>
    private static async Task<ApiResult<object>> MeAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var user = await store.GetUserAsync(session.UserId, cancellationToken);

        return ApiResult<object>.Success(new
        {
            username = session.Username,
            displayName = string.IsNullOrWhiteSpace(session.DisplayName) ? session.Username : session.DisplayName,
            role = session.Role,
            expiresAt = session.ExpiresAt,
            mustChangePassword = user?.MustChangePassword ?? false,
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

    /// <summary>列出管理员账号。</summary>
    private static async Task<ApiResult<object>> AccountsAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var users = await store.GetUsersAsync(cancellationToken);

        return ApiResult<object>.Success(users.Select(u => new
        {
            id = u.Id,
            username = u.Username,
            displayName = u.DisplayName,
            role = u.Role,
            mustChangePassword = u.MustChangePassword,
            createdAt = u.CreatedAt,
        }));
    }

    /// <summary>重置指定账号密码。</summary>
    private static async Task<ApiResult<bool>> ResetPasswordAsync(
        string id,
        ChangePasswordRequest request,
        HttpContext http,
        AdminAuthService auth,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
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
