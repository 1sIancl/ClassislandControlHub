using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Services;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// A 端远程管理接口：远程命令行、插件管理、外观下发、提醒，挂载在 <c>/api/v1/admin</c> 下。
/// </summary>
public static class RemoteEndpoints
{
    /// <summary>注册远程管理路由。</summary>
    public static void MapRemoteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapPost("/devices/{id}/command", SendCommandAsync).RequirePermission(PermissionKeys.RemoteWrite);
        group.MapPost("/devices/command", SendBroadcastCommandAsync).RequirePermission(PermissionKeys.RemoteWrite);
        group.MapGet("/devices/{id}/commands", ListCommandsAsync).RequirePermission(PermissionKeys.RemoteRead);
        group.MapPost("/devices/{id}/notify", NotifyAsync).RequirePermission(PermissionKeys.RemoteWrite);
        group.MapPost("/devices/appearance", ApplyAppearanceAsync).RequirePermission(PermissionKeys.RemoteWrite);
        group.MapGet("/devices/{id}/plugins", GetPluginsAsync).RequirePermission(PermissionKeys.RemoteRead);
        group.MapPost("/devices/{id}/plugins/refresh", RefreshPluginsAsync)
            .RequirePermission(PermissionKeys.RemoteWrite);


    }

    /// <summary>读取设备最近一次上报的插件列表。</summary>
    private static async Task<ApiResult<List<PluginInfoDto>>> GetPluginsAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var raw = await store.GetSettingAsync($"device_plugins.{id}", "[]", cancellationToken);
        return ApiResult<List<PluginInfoDto>>.Success(
            HubJson.DeserializeOrDefault(raw ?? "[]", new List<PluginInfoDto>()));
    }

    /// <summary>命令 B 端刷新并重新上报插件列表。</summary>
    private static async Task<ApiResult<DeviceCommandDto>> RefreshPluginsAsync(
        string id,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var device = await store.GetDeviceAsync(id, cancellationToken)
                     ?? throw HubException.NotFound("设备不存在。");

        var row = new RemoteCommandRow
        {
            Id = HubChecksum.NewId(),
            DeviceId = device.Id,
            Kind = RemoteCommandKinds.PluginRefresh,
            Payload = string.Empty,
            Status = "pending",
            IssuedAt = DateTimeOffset.UtcNow,
            IssuedBy = session.Username,
        };
        await store.CreateCommandAsync(row, cancellationToken);
        sync.PublishWakeUp();
        return ApiResult<DeviceCommandDto>.Success(ToDto(row, device.Name));
    }

    /// <summary>向单台设备下发指令。</summary>
    private static async Task<ApiResult<DeviceCommandDto>> SendCommandAsync(
        string id,
        SendCommandRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var device = await store.GetDeviceAsync(id, cancellationToken)
                     ?? throw HubException.NotFound("设备不存在。");

        if (string.IsNullOrWhiteSpace(request.Kind))
        {
            throw HubException.Validation("指令类型不能为空。");
        }

        if (device.Revoked)
        {
            throw HubException.Validation("设备已停用，无法下发指令。");
        }

        var row = NewCommand(device, request.Kind.Trim(), request.Payload ?? string.Empty, session.Username);
        await store.CreateCommandAsync(row, cancellationToken);
        sync.PublishWakeUp();

        await store.AddAuditAsync(session.Username, "device.command", device.Name,
            $"下发指令 {row.Kind}。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<DeviceCommandDto>.Success(ToDto(row, device.Name));
    }

    /// <summary>向多台设备统一下发指令（广播）。</summary>
    private static async Task<ApiResult<object>> SendBroadcastCommandAsync(
        SendCommandRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        if (string.IsNullOrWhiteSpace(request.Kind))
        {
            throw HubException.Validation("指令类型不能为空。");
        }

        var devices = await store.GetDevicesAsync(cancellationToken);
        var (targets, skipped) = SelectTargets(devices, request.DeviceIds, request.IncludeOffline, sync);

        // 同一批广播共用同一个签发时间，方便在历史里识别为一次操作。
        var now = DateTimeOffset.UtcNow;
        foreach (var device in targets)
        {
            await store.CreateCommandAsync(
                NewCommand(device, request.Kind.Trim(), request.Payload ?? string.Empty, session.Username, now),
                cancellationToken);
        }

        sync.PublishWakeUp();
        await store.AddAuditAsync(session.Username, "device.command.broadcast", "多设备",
            $"统一下发指令 {request.Kind} 至 {targets.Count} 台设备（跳过离线 {skipped} 台）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<object>.Success(new { affected = targets.Count, skipped });
    }

    // ────────────────────────────── 指令构造与目标筛选 ──────────────────────────────

    /// <summary>远程指令的默认有效期：超过该时间还没执行就作废，避免设备离线很久后突然执行陈旧命令。</summary>
    private static readonly TimeSpan CommandTtl = TimeSpan.FromHours(2);

    /// <summary>构造一条待执行指令。</summary>
    private static RemoteCommandRow NewCommand(DeviceRow device, string kind, string payload, string issuedBy,
        DateTimeOffset? issuedAt = null)
    {
        var now = issuedAt ?? DateTimeOffset.UtcNow;
        return new RemoteCommandRow
        {
            Id = HubChecksum.NewId(),
            DeviceId = device.Id,
            Kind = kind,
            Payload = payload,
            Status = "pending",
            IssuedAt = now,
            IssuedBy = issuedBy,
            ExpiresAt = now.Add(CommandTtl),
        };
    }

    /// <summary>
    /// 解析广播目标。默认只发给**在线**设备（与界面文案一致），
    /// 离线设备需要显式打开 <c>IncludeOffline</c>：此时命令会排队，等设备上线后执行。
    /// </summary>
    private static (List<DeviceRow> Targets, int Skipped) SelectTargets(
        List<DeviceRow> devices, List<string>? deviceIds, bool includeOffline, SyncService sync)
    {
        IEnumerable<DeviceRow> pool = devices.Where(d => !d.Revoked);
        if (deviceIds is { Count: > 0 })
        {
            var wanted = new HashSet<string>(deviceIds, StringComparer.OrdinalIgnoreCase);
            pool = pool.Where(d => wanted.Contains(d.Id));
        }

        var candidates = pool.ToList();
        if (includeOffline)
        {
            return (candidates, 0);
        }

        var online = candidates.Where(sync.IsOnline).ToList();
        return (online, candidates.Count - online.Count);
    }

    /// <summary>查询某设备的指令历史。</summary>
    private static async Task<ApiResult<List<DeviceCommandDto>>> ListCommandsAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var device = await store.GetDeviceAsync(id, cancellationToken);
        var rows = await store.GetCommandsAsync(id, 80, cancellationToken);
        return ApiResult<List<DeviceCommandDto>>.Success(
            rows.Select(r => ToDto(r, device?.Name)).ToList());
    }

    /// <summary>向单台设备发送提醒。</summary>
    private static async Task<ApiResult<DeviceCommandDto>> NotifyAsync(
        string id,
        NotifyRequestDto request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var device = await store.GetDeviceAsync(id, cancellationToken)
                     ?? throw HubException.NotFound("设备不存在。");

        if (string.IsNullOrWhiteSpace(request.Title) && string.IsNullOrWhiteSpace(request.Message))
        {
            throw HubException.Validation("提醒标题与内容不能同时为空。");
        }

        if (device.Revoked)
        {
            throw HubException.Validation("设备已停用，无法发送提醒。");
        }

        var row = NewCommand(device, RemoteCommandKinds.Notify, HubJson.Serialize(request), session.Username);
        await store.CreateCommandAsync(row, cancellationToken);
        sync.PublishWakeUp();

        await store.AddAuditAsync(session.Username, "device.notify", device.Name,
            $"发送提醒：{request.Title}", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<DeviceCommandDto>.Success(ToDto(row, device.Name));
    }

    /// <summary>向一台或多台设备统一下发外观配置。</summary>
    private static async Task<ApiResult<object>> ApplyAppearanceAsync(
        ApplyAppearanceRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var devices = await store.GetDevicesAsync(cancellationToken);
        var (targets, skipped) = SelectTargets(devices, request.DeviceIds, request.IncludeOffline, sync);

        var payload = HubJson.Serialize(request.Appearance ?? new AppearanceConfigDto());
        var now = DateTimeOffset.UtcNow;
        foreach (var device in targets)
        {
            await store.CreateCommandAsync(
                NewCommand(device, RemoteCommandKinds.AppearanceApply, payload, session.Username, now),
                cancellationToken);
        }

        sync.PublishWakeUp();
        await store.AddAuditAsync(session.Username, "device.appearance", "多设备",
            $"统一下发外观配置至 {targets.Count} 台设备（跳过离线 {skipped} 台）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<object>.Success(new { affected = targets.Count, skipped });
    }

    private static DeviceCommandDto ToDto(RemoteCommandRow row, string? deviceName) => new()
    {
        Id = row.Id,
        DeviceId = row.DeviceId,
        DeviceName = deviceName,
        Kind = row.Kind,
        Payload = row.Payload,
        Status = row.Status,
        Output = row.Output,
        ExitCode = row.ExitCode,
        IssuedAt = row.IssuedAt,
        FinishedAt = row.FinishedAt,
        IssuedBy = row.IssuedBy,
    };
}

/// <summary>下发指令的请求体。</summary>
public sealed class SendCommandRequest
{
    /// <summary>指令类型，取值见 <see cref="RemoteCommandKinds"/>。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>载荷（JSON 字符串或纯文本）。</summary>
    public string? Payload { get; set; }

    /// <summary>目标设备 ID（统一下发时可选，留空表示全部在线设备）。</summary>
    public List<string>? DeviceIds { get; set; }

    /// <summary>是否把离线设备也纳入广播（命令会排队，等设备上线后执行）。默认 false。</summary>
    public bool IncludeOffline { get; set; }
}

/// <summary>
/// 通知模板接口：把「广播站通知」「放学提醒」这类常用内容存成模板，发通知时一键套用。
/// </summary>
public static class NoticeTemplateEndpoints
{
    /// <summary>注册通知模板路由。</summary>
    public static void MapNoticeTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/notice-templates", ListNoticeTemplatesAsync).RequirePermission(PermissionKeys.RemoteRead);
        group.MapPost("/notice-templates", CreateNoticeTemplateAsync).RequirePermission(PermissionKeys.RemoteWrite);
        group.MapPut("/notice-templates/{id}", UpdateNoticeTemplateAsync)
            .RequirePermission(PermissionKeys.RemoteWrite);
        group.MapDelete("/notice-templates/{id}", DeleteNoticeTemplateAsync)
            .RequirePermission(PermissionKeys.RemoteWrite);
    }

    private static NoticeTemplateDto ToNoticeTemplateDto(NoticeTemplateRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Title = row.Title,
        Content = row.Content,
        Speak = row.Speak,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    /// <summary>通知模板列表。</summary>
    private static async Task<ApiResult<List<NoticeTemplateDto>>> ListNoticeTemplatesAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var rows = await store.GetNoticeTemplatesAsync(cancellationToken);
        return ApiResult<List<NoticeTemplateDto>>.Success(rows.Select(ToNoticeTemplateDto).ToList());
    }

    /// <summary>新建通知模板。</summary>
    private static async Task<ApiResult<NoticeTemplateDto>> CreateNoticeTemplateAsync(
        NoticeTemplateUpsertRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw HubException.Validation("请填写模板名称。");
        }

        if (string.IsNullOrWhiteSpace(request.Title) && string.IsNullOrWhiteSpace(request.Content))
        {
            throw HubException.Validation("标题和内容不能都为空。");
        }

        var row = new NoticeTemplateRow
        {
            Id = HubChecksum.NewId(),
            Name = name.Length > 40 ? name[..40] : name,
            Title = (request.Title ?? string.Empty).Trim(),
            Content = (request.Content ?? string.Empty).Trim(),
            Speak = request.Speak,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateNoticeTemplateAsync(row, cancellationToken);
        await store.AddAuditAsync(session.Username, "notice-template.create", row.Name,
            "新建通知模板。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<NoticeTemplateDto>.Success(ToNoticeTemplateDto(row));
    }

    /// <summary>更新通知模板。</summary>
    private static async Task<ApiResult<NoticeTemplateDto>> UpdateNoticeTemplateAsync(
        string id,
        NoticeTemplateUpsertRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await store.GetNoticeTemplateAsync(id, cancellationToken)
                  ?? throw HubException.NotFound("模板不存在。");

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw HubException.Validation("请填写模板名称。");
        }

        row.Name = name.Length > 40 ? name[..40] : name;
        row.Title = (request.Title ?? string.Empty).Trim();
        row.Content = (request.Content ?? string.Empty).Trim();
        row.Speak = request.Speak;

        if (!await store.UpdateNoticeTemplateAsync(row, cancellationToken))
        {
            throw HubException.NotFound("模板不存在。");
        }

        await store.AddAuditAsync(session.Username, "notice-template.update", row.Name,
            "更新通知模板。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<NoticeTemplateDto>.Success(ToNoticeTemplateDto(row));
    }

    /// <summary>删除通知模板。</summary>
    private static async Task<ApiResult<bool>> DeleteNoticeTemplateAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await store.GetNoticeTemplateAsync(id, cancellationToken)
                  ?? throw HubException.NotFound("模板不存在。");

        await store.DeleteNoticeTemplateAsync(id, cancellationToken);
        await store.AddAuditAsync(session.Username, "notice-template.delete", row.Name,
            "删除通知模板。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(true);
    }
}

/// <summary>下发外观的请求体。</summary>
public sealed class ApplyAppearanceRequest
{
    /// <summary>外观配置。</summary>
    public AppearanceConfigDto? Appearance { get; set; }

    /// <summary>目标设备 ID（留空表示全部在线设备）。</summary>
    public List<string>? DeviceIds { get; set; }

    /// <summary>是否把离线设备也纳入下发（命令会排队，等设备上线后执行）。默认 false。</summary>
    public bool IncludeOffline { get; set; }
}
