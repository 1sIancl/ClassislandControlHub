using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Options;
using ControlHub.Server.Services;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 面向 B 端（ClassIsland 插件）的接口。
/// 全部挂载在 <c>/api/v1/client</c> 下。
/// </summary>
public static class ClientEndpoints
{
    /// <summary>注册客户端相关路由。</summary>
    public static void MapClientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("client");

        group.MapPost("/enroll", EnrollAsync).AllowAnonymous();
        group.MapGet("/config", GetConfigAsync).AllowAnonymous();
        group.MapGet("/time", GetTimeAsync).AllowAnonymous();

        var authed = group.MapGroup(string.Empty)
            .AddEndpointFilter<DeviceAuthFilter>();

        authed.MapPost("/heartbeat", HeartbeatAsync);
        authed.MapGet("/sync", SyncAsync);
        authed.MapGet("/wait", WaitAsync);
        authed.MapPost("/report", ReportAsync);
        authed.MapPost("/logs", UploadLogsAsync);
        authed.MapGet("/commands", GetCommandsAsync);
        authed.MapPost("/commands/report", ReportCommandAsync);
        authed.MapPost("/plugins", ReportPluginsAsync);
        authed.MapPost("/time-report", ReportTimeSyncAsync);
    }

    /// <summary>
    /// 注册设备。管理员在 Web 端生成注册码，客户端填入后即可接入集控。
    /// </summary>
    private static async Task<ApiResult<EnrollResponse>> EnrollAsync(
        EnrollRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        IOptions<ServerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ClientEndpoints");
        var opts = options.Value;

        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            throw HubException.Validation("缺少设备标识。");
        }

        var deviceId = request.DeviceId.Trim();

        // 设备重新注册（例如重装系统后）时若已存在记录，允许直接放行，无需再次消耗注册码。
        var existing = await store.GetDeviceAsync(deviceId, cancellationToken);

        if (opts.RequireEnrollCode && existing is null)
        {
            if (string.IsNullOrWhiteSpace(request.EnrollCode))
            {
                throw new HubException(HubErrorCodes.EnrollCodeInvalid, "请输入注册码。", 400);
            }

            var code = request.EnrollCode.Trim().ToUpperInvariant();
            var codeRow = await store.GetEnrollCodeAsync(code, cancellationToken);
            if (codeRow is null)
            {
                throw new HubException(HubErrorCodes.EnrollCodeInvalid, "注册码不存在。", 400);
            }

            if (!codeRow.Enabled)
            {
                throw new HubException(HubErrorCodes.EnrollCodeExpired, "该注册码已被停用。", 400);
            }

            if (codeRow.ExpiresAt.HasValue && codeRow.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                throw new HubException(HubErrorCodes.EnrollCodeExpired, "该注册码已过期。", 400);
            }

            if (!await store.ConsumeEnrollCodeAsync(code, cancellationToken))
            {
                throw new HubException(HubErrorCodes.EnrollCodeExpired, "该注册码可用次数已用完。", 400);
            }
        }

        var token = HubChecksum.NewToken(48);
        var name = string.IsNullOrWhiteSpace(request.DeviceName)
            ? (string.IsNullOrWhiteSpace(request.MachineName) ? deviceId[..Math.Min(8, deviceId.Length)] : request.MachineName)
            : request.DeviceName.Trim();

        var device = await store.UpsertDeviceAsync(
            deviceId, token, name, request.MachineName, request.OsVersion,
            request.ClassIslandVersion, request.PluginVersion,
            http.GetClientIpAddress(), cancellationToken);

        var groupRow = device.GroupId is null ? null : await store.GetGroupAsync(device.GroupId, cancellationToken);
        var revision = await store.GetRevisionAsync(cancellationToken);

        await store.AddAuditAsync(device.Name, "device.enroll", device.Id,
            $"设备注册成功（{device.MachineName} / {device.IpAddress}）。", http.GetClientIpAddress(), cancellationToken);

        logger.LogInformation("设备 {Name}（{Id}）注册成功。", device.Name, device.Id);

        return ApiResult<EnrollResponse>.Success(new EnrollResponse
        {
            DeviceId = device.Id,
            DeviceToken = token,
            DeviceName = device.Name,
            GroupId = device.GroupId,
            GroupName = groupRow?.Name,
            Revision = revision,
            HeartbeatSeconds = HubProtocol.DefaultHeartbeatSeconds,
            ServerName = opts.ServerName,
        });
    }

    /// <summary>
    /// 时间同步查询。返回经 NTP 授时校正后的服务器 UTC 时间，供 B 端按 NTP 方式（RTT 补偿）校准本机时钟。
    /// 匿名可访问——时间信息无害，且设备在注册前也应能对时。
    /// </summary>
    private static ApiResult<TimeSyncResponse> GetTimeAsync(ServerTimeService serverTime)
    {
        return ApiResult<TimeSyncResponse>.Success(new TimeSyncResponse
        {
            ServerTime = serverTime.GetUtcNow(),
        });
    }

    /// <summary>
    /// 公共配置查询。客户端在注册前即可读取，用于判断服务器是否要求注册码。
    /// </summary>
    private static async Task<ApiResult<ServerInfoDto>> GetConfigAsync(
        HubStore store,
        IOptions<ServerOptions> options,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var revision = await store.GetRevisionAsync(cancellationToken);
        var devices = await store.GetDevicesAsync(cancellationToken);
        var online = devices.Count(d => d.LastSeenAt.HasValue
                                        && (DateTimeOffset.UtcNow - d.LastSeenAt.Value).TotalSeconds
                                        < opts.OnlineTimeoutSeconds
                                        && !d.Revoked);

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
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64),
            UptimeSeconds = Environment.TickCount64 / 1000,
            DataDirectory = opts.ResolveDataDirectory(environment.ContentRootPath),
            HttpPort = opts.HttpPort,
            DiscoveryPort = opts.DiscoveryPort,
            NtpServerEnabled = opts.EnableNtpServer,
            NtpPort = opts.NtpPort,
        });
    }

    /// <summary>心跳上报。服务端据此判断在线状态并回报是否需要同步。</summary>
    private static async Task<ApiResult<HeartbeatResponse>> HeartbeatAsync(
        HeartbeatRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        IOptions<ServerOptions> options,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();
        var revision = await store.GetRevisionAsync(cancellationToken);

        await store.UpdateHeartbeatAsync(
            device.Id,
            string.IsNullOrWhiteSpace(request.State) ? DeviceStates.Idle : request.State,
            request.AppliedRevision,
            request.AppliedPushEpoch,
            request.CurrentClassPlanName,
            request.LastError,
            HubJson.Serialize(request.Metrics),
            cancellationToken);

        var shouldSync = request.AppliedRevision < revision
                         || request.AppliedPushEpoch < device.PushEpoch;

        return ApiResult<HeartbeatResponse>.Success(new HeartbeatResponse
        {
            Revision = revision,
            PushEpoch = device.PushEpoch,
            ShouldSync = shouldSync,
            HeartbeatSeconds = Math.Max(5, options.Value.OnlineTimeoutSeconds / 4),
            ServerTime = DateTimeOffset.UtcNow,
            Message = await sync.TakePushMessageAsync(cancellationToken),
            Revoked = false,
        });
    }

    /// <summary>拉取配置。客户端版本号与服务器一致时返回 <c>data = null</c>，表示无需同步。</summary>
    private static async Task<ApiResult<SyncResponse?>> SyncAsync(
        HttpContext http,
        HubStore store,
        SyncService sync,
        long? revision,
        string? sections,
        bool? force,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();

        var supported = string.IsNullOrWhiteSpace(sections)
            ? null
            : sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => HubProtocol.Sections.All.Contains(s, StringComparer.OrdinalIgnoreCase))
                .ToList();

        var response = await sync.BuildSyncResponseAsync(device, revision, supported, force ?? false,
            cancellationToken);

        if (response is not null)
        {
            await store.AddAuditAsync(device.Name, "device.sync.pull", device.Id,
                $"客户端拉取配置，版本 {response.Revision}。", http.GetClientIpAddress(), cancellationToken);
        }

        return ApiResult<SyncResponse?>.Success(response);
    }

    /// <summary>
    /// 长轮询等待配置变更或定向推送。服务端在版本号/推送世代号变化时立即返回，
    /// 最长挂起 <see cref="HubProtocol.DefaultLongPollSeconds"/> 秒。
    /// </summary>
    private static async Task<ApiResult<WaitResponse>> WaitAsync(
        HttpContext http,
        HubStore store,
        RevisionNotifier notifier,
        long? revision,
        long? pushEpoch,
        int? timeout,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();

        var sinceRevision = revision ?? 0;
        var sincePushEpoch = pushEpoch ?? device.AppliedPushEpoch;
        var seconds = Math.Clamp(timeout ?? HubProtocol.DefaultLongPollSeconds, 1, 60);

        // 条件同时覆盖「全局配置换了」与「本设备被定向推送了」两种情况。
        async Task<bool> Condition(CancellationToken token)
        {
            var currentRevision = await store.GetRevisionAsync(token);
            if (currentRevision != sinceRevision)
            {
                return true;
            }

            var currentPushEpoch = await store.GetPushEpochAsync(device.Id, token);
            return currentPushEpoch != sincePushEpoch;
        }

        var changed = await notifier.WaitAsync(Condition, TimeSpan.FromSeconds(seconds), cancellationToken);

        return ApiResult<WaitResponse>.Success(new WaitResponse
        {
            Changed = changed,
            Revision = await store.GetRevisionAsync(cancellationToken),
            PushEpoch = await store.GetPushEpochAsync(device.Id, cancellationToken),
        });
    }

    /// <summary>上报配置应用结果。</summary>
    private static async Task<ApiResult<bool>> ReportAsync(
        ApplyReportRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();

        if (string.Equals(request.Result, ApplyResults.Success, StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Result, ApplyResults.Partial, StringComparison.OrdinalIgnoreCase))
        {
            await store.UpdateAppliedAsync(device.Id, request.Revision, request.PushEpoch, cancellationToken);
        }
        else
        {
            await store.UpdateHeartbeatAsync(device.Id, DeviceStates.Error, device.AppliedRevision,
                device.AppliedPushEpoch, device.CurrentClassPlanName, request.Message, device.Metrics,
                cancellationToken);
        }

        var sections = string.Join(",", request.AppliedSections);
        await store.AddAuditAsync(device.Name, $"device.sync.{request.Result}", device.Id,
            $"版本 {request.Revision} 应用结果：{request.Result}；分区 {sections}；{request.Message}",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    /// <summary>上传客户端日志。</summary>
    private static async Task<ApiResult<bool>> UploadLogsAsync(
        LogReportRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();

        if (request.Entries.Count == 0)
        {
            return ApiResult<bool>.Success(true);
        }

        // 单次上报做上限保护，避免异常客户端撑爆数据库。
        var entries = request.Entries
            .Take(500)
            .Select(e => (e.Timestamp, string.IsNullOrWhiteSpace(e.Level) ? "info" : e.Level, e.Message ?? string.Empty));

        await store.InsertClientLogsAsync(device.Id, entries, cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    /// <summary>拉取本设备的待执行远程指令。</summary>
    private static async Task<ApiResult<List<RemoteCommandDto>>> GetCommandsAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();
        var rows = await store.GetPendingCommandsAsync(device.Id, cancellationToken);
        return ApiResult<List<RemoteCommandDto>>.Success(rows.Select(r => new RemoteCommandDto
        {
            Id = r.Id,
            Kind = r.Kind,
            Payload = r.Payload,
            IssuedAt = r.IssuedAt,
            IssuedBy = r.IssuedBy,
        }).ToList());
    }

    /// <summary>回报远程指令的执行结果。</summary>
    private static async Task<ApiResult<bool>> ReportCommandAsync(
        CommandReportRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();
        if (string.IsNullOrWhiteSpace(request.CommandId))
        {
            throw HubException.Validation("缺少指令 ID。");
        }

        await store.CompleteCommandAsync(request.CommandId, request.Success,
            request.Output ?? string.Empty, request.ExitCode, request.Error, cancellationToken);

        await store.AddAuditAsync(device.Name, "device.command.result", device.Name,
            $"指令 {request.CommandId} 执行{(request.Success ? "成功" : "失败")}。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    /// <summary>接收 B 端上报的已安装插件列表。</summary>
    private static async Task<ApiResult<bool>> ReportPluginsAsync(
        PluginReportRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();
        await store.SetSettingAsync($"device_plugins.{device.Id}", HubJson.Serialize(request.Plugins), cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    /// <summary>接收 B 端时间同步结果，便于管理端展示。</summary>
    private static async Task<ApiResult<bool>> ReportTimeSyncAsync(
        Dictionary<string, string> request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();
        await store.SetSettingAsync($"time_sync.{device.Id}", HubJson.Serialize(request), cancellationToken);
        return ApiResult<bool>.Success(true);
    }
}
