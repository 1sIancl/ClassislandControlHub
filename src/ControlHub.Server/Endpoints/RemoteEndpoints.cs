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
            .AddEndpointFilter<AdminAuthFilter>();

        group.MapPost("/devices/{id}/command", SendCommandAsync);
        group.MapPost("/devices/command", SendBroadcastCommandAsync);
        group.MapGet("/devices/{id}/commands", ListCommandsAsync);
        group.MapPost("/devices/{id}/notify", NotifyAsync);
        group.MapPost("/devices/appearance", ApplyAppearanceAsync);
        group.MapGet("/devices/{id}/plugins", GetPluginsAsync);
        group.MapPost("/devices/{id}/plugins/refresh", RefreshPluginsAsync);
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

        var row = new RemoteCommandRow
        {
            Id = HubChecksum.NewId(),
            DeviceId = device.Id,
            Kind = request.Kind.Trim(),
            Payload = request.Payload ?? string.Empty,
            Status = "pending",
            IssuedAt = DateTimeOffset.UtcNow,
            IssuedBy = session.Username,
        };
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
        var targets = new List<DeviceRow>();
        if (request.DeviceIds is { Count: > 0 })
        {
            var wanted = new HashSet<string>(request.DeviceIds, StringComparer.OrdinalIgnoreCase);
            targets.AddRange(devices.Where(d => wanted.Contains(d.Id) && !d.Revoked));
        }
        else
        {
            targets.AddRange(devices.Where(d => !d.Revoked));
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var device in targets)
        {
            await store.CreateCommandAsync(new RemoteCommandRow
            {
                Id = HubChecksum.NewId(),
                DeviceId = device.Id,
                Kind = request.Kind.Trim(),
                Payload = request.Payload ?? string.Empty,
                Status = "pending",
                IssuedAt = now,
                IssuedBy = session.Username,
            }, cancellationToken);
        }

        sync.PublishWakeUp();
        await store.AddAuditAsync(session.Username, "device.command.broadcast", "多设备",
            $"统一下发指令 {request.Kind} 至 {targets.Count} 台设备。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<object>.Success(new { affected = targets.Count });
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

        var row = new RemoteCommandRow
        {
            Id = HubChecksum.NewId(),
            DeviceId = device.Id,
            Kind = RemoteCommandKinds.Notify,
            Payload = HubJson.Serialize(request),
            Status = "pending",
            IssuedAt = DateTimeOffset.UtcNow,
            IssuedBy = session.Username,
        };
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
        var targets = new List<DeviceRow>();
        if (request.DeviceIds is { Count: > 0 })
        {
            var wanted = new HashSet<string>(request.DeviceIds, StringComparer.OrdinalIgnoreCase);
            targets.AddRange(devices.Where(d => wanted.Contains(d.Id) && !d.Revoked));
        }
        else
        {
            targets.AddRange(devices.Where(d => !d.Revoked));
        }

        var payload = HubJson.Serialize(request.Appearance ?? new AppearanceConfigDto());
        var now = DateTimeOffset.UtcNow;
        foreach (var device in targets)
        {
            await store.CreateCommandAsync(new RemoteCommandRow
            {
                Id = HubChecksum.NewId(),
                DeviceId = device.Id,
                Kind = RemoteCommandKinds.AppearanceApply,
                Payload = payload,
                Status = "pending",
                IssuedAt = now,
                IssuedBy = session.Username,
            }, cancellationToken);
        }

        sync.PublishWakeUp();
        await store.AddAuditAsync(session.Username, "device.appearance", "多设备",
            $"统一下发外观配置至 {targets.Count} 台设备。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<object>.Success(new { affected = targets.Count });
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
}

/// <summary>下发外观的请求体。</summary>
public sealed class ApplyAppearanceRequest
{
    /// <summary>外观配置。</summary>
    public AppearanceConfigDto? Appearance { get; set; }

    /// <summary>目标设备 ID（留空表示全部设备）。</summary>
    public List<string>? DeviceIds { get; set; }
}
