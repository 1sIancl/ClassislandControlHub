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
        authed.MapPost("/diagnostics", UploadDiagnosticAsync);
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

        // 设备 ID 会出现在管理端接口路径里（/admin/devices/{id}/…），必须是 URL 安全的短标识。
        // 客户端生成形如 ci-<guid>；这里挡住异常或恶意指纹，避免建出打不开的设备记录。
        if (deviceId.Length > 64
            || !deviceId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
        {
            throw HubException.Validation(
                "设备标识格式不正确：只允许字母、数字、减号、下划线与点，且不超过 64 个字符。");
        }

        // 设备重新注册（例如重装系统后）时若已存在记录，允许直接放行，无需再次消耗注册码。
        var existing = await store.GetDeviceAsync(deviceId, cancellationToken);

        // 预注册匹配：按机器名找「还没有令牌」的占位记录（CSV 批量导入时建的），注册后认领它的绑定关系。
        DeviceRow? preRegistered = null;
        if (existing is null && !string.IsNullOrWhiteSpace(request.MachineName))
        {
            preRegistered = await store.GetPreRegisteredDeviceByMachineNameAsync(
                request.MachineName.Trim(), cancellationToken);
        }

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

        // 预注册认领：管理员可先用 CSV 录好「教室名称 / 分组 / 档案 / 备注」（此时该记录没有令牌），
        // 教室端首次注册时按机器名认领这条记录，从而保留事先配好的绑定关系。
        if (preRegistered is not null)
        {
            await store.AdoptPreRegisteredDeviceAsync(preRegistered, device.Id, cancellationToken);
            device = await store.GetDeviceAsync(device.Id, cancellationToken) ?? device;
            logger.LogInformation("设备 {Name}（{Id}）已认领预注册记录 {Placeholder}。",
                device.Name, device.Id, preRegistered.Id);
        }

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
            StartedAt = ServerRuntime.StartedAt,
            UptimeSeconds = (long)ServerRuntime.Uptime.TotalSeconds,
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
        long? pushEpoch,
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

        var response = await sync.BuildSyncResponseAsync(device, revision, pushEpoch, supported, force ?? false,
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
        SyncService sync,
        WebhookService webhooks,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();

        if (string.Equals(request.Result, ApplyResults.Success, StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Result, ApplyResults.Partial, StringComparison.OrdinalIgnoreCase))
        {
            // 记下「这台设备现在实际用的是哪个档案、哪一版」，它是「下发前差异预览」的对比基线。
            // 必须连版本号一起记：档案之后被编辑时，只有版本号能定位到当时那份内容。
            var appliedProfile = await sync.ResolveProfileAsync(device, cancellationToken);
            await store.UpdateAppliedAsync(device.Id, request.Revision, request.PushEpoch, appliedProfile?.Id,
                appliedProfile?.Revision ?? 0, cancellationToken);
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

        // 只要不是「完全成功」就推 Webhook：部分成功同样意味着教室那边有分区没应用上。
        // 以前 partial 被当成成功静默掉了，表现为「课表换了但时间表没换，管理端却毫无提示」。
        var fullyApplied = string.Equals(request.Result, ApplyResults.Success, StringComparison.OrdinalIgnoreCase);
        var partiallyApplied = string.Equals(request.Result, ApplyResults.Partial, StringComparison.OrdinalIgnoreCase);
        if (!fullyApplied)
        {
            await webhooks.NotifyAsync(WebhookEvents.DeviceError,
                partiallyApplied ? $"配置部分应用：{device.Name}" : $"配置应用失败：{device.Name}",
                $"「{device.Name}」{(partiallyApplied ? "部分配置未能应用" : "应用配置失败")}：{request.Message}",
                new Dictionary<string, object?>
                {
                    ["deviceId"] = device.Id,
                    ["deviceName"] = device.Name,
                    ["revision"] = request.Revision,
                    ["result"] = request.Result,
                }, cancellationToken);
        }

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

    /// <summary>单次上传的诊断内容上限（4 MB，足以容纳 1440p 的 PNG 截图）。</summary>
    private const int MaxDiagnosticBytes = 4 * 1024 * 1024;

    /// <summary>允许上传的诊断工件类型白名单。</summary>
    private static readonly string[] DiagnosticKinds = ["screenshot"];

    /// <summary>
    /// 上传诊断工件（屏幕截图等），内容以 Base64 传输。
    /// <para>落库后由管理端按需读取；每个设备只保留最近若干条，客户端反复抓屏也不会撑爆数据库。</para>
    /// </summary>
    private static async Task<ApiResult<bool>> UploadDiagnosticAsync(
        DiagnosticUploadRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();

        if (string.IsNullOrWhiteSpace(request.Kind) || !DiagnosticKinds.Contains(request.Kind))
        {
            throw HubException.Validation("不支持的诊断类型。");
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(request.ContentBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            throw HubException.Validation("诊断内容不是合法的 Base64。");
        }

        if (content.Length == 0)
        {
            throw HubException.Validation("诊断内容为空。");
        }

        if (content.Length > MaxDiagnosticBytes)
        {
            throw HubException.Validation(
                $"诊断内容过大（{content.Length / 1024} KB，上限 {MaxDiagnosticBytes / 1024 / 1024} MB）。");
        }

        await store.AddDiagnosticAsync(
            device.Id,
            request.CommandId ?? string.Empty,
            request.Kind,
            request.Note ?? string.Empty,
            string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
            content,
            request.CapturedAt,
            cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    /// <summary>
    /// 拉取并「领取」本设备的待执行远程指令。
    /// <para>指令是<b>一次性派发</b>的：本次返回的会被标记为已派发，后续轮询不再返回，
    /// 避免同一条命令随每次心跳被反复执行（重复重启、重复跑命令）。</para>
    /// </summary>
    private static async Task<ApiResult<List<RemoteCommandDto>>> GetCommandsAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();
        var rows = await store.DispatchPendingCommandsAsync(device.Id, cancellationToken);
        return ApiResult<List<RemoteCommandDto>>.Success(rows.Select(r => new RemoteCommandDto
        {
            Id = r.Id,
            Kind = r.Kind,
            Payload = r.Payload,
            IssuedAt = r.IssuedAt,
            IssuedBy = r.IssuedBy,
        }).ToList());
    }

    /// <summary>回报远程指令的执行结果。只能回报属于本设备的指令。</summary>
    private static async Task<ApiResult<bool>> ReportCommandAsync(
        CommandReportRequest request,
        HttpContext http,
        HubStore store,
        WebhookService webhooks,
        CancellationToken cancellationToken)
    {
        var device = http.RequireDevice();
        if (string.IsNullOrWhiteSpace(request.CommandId))
        {
            throw HubException.Validation("缺少指令 ID。");
        }

        var updated = await store.CompleteCommandAsync(device.Id, request.CommandId, request.Success,
            request.Output ?? string.Empty, request.ExitCode, request.Error, cancellationToken);
        if (!updated)
        {
            throw HubException.NotFound("指令不存在，或不属于当前设备。");
        }

        await store.AddAuditAsync(device.Name, "device.command.result", device.Name,
            $"指令 {request.CommandId} 执行{(request.Success ? "成功" : "失败")}。",
            http.GetClientIpAddress(), cancellationToken);

        // 指令失败时推 Webhook：教室那边出问题，管理员不该只能靠翻日志发现。
        if (!request.Success)
        {
            await webhooks.NotifyAsync(WebhookEvents.CommandFailed,
                $"指令执行失败：{device.Name}",
                $"「{device.Name}」执行指令失败：{request.Error ?? "未提供错误信息"}",
                new Dictionary<string, object?>
                {
                    ["deviceId"] = device.Id,
                    ["deviceName"] = device.Name,
                    ["commandId"] = request.CommandId,
                    ["exitCode"] = request.ExitCode,
                }, cancellationToken);
        }

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
