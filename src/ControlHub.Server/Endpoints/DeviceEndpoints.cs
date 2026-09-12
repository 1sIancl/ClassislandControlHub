using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Services;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 设备、分组、注册码与下发推送接口，挂载在 <c>/api/v1/admin</c> 下。
/// </summary>
public static class DeviceEndpoints
{
    /// <summary>注册设备与分组相关路由。</summary>
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>();

        // ── 设备 ──
        group.MapGet("/devices", ListDevicesAsync);
        group.MapPut("/devices/{id}", UpdateDeviceAsync);
        group.MapPost("/devices/{id}/revoke", RevokeDeviceAsync);
        group.MapDelete("/devices/{id}", DeleteDeviceAsync);
        group.MapGet("/devices/{id}/logs", GetDeviceLogsAsync);
        group.MapDelete("/devices/{id}/logs", ClearDeviceLogsAsync);

        // ── 分组 ──
        group.MapGet("/groups", ListGroupsAsync);
        group.MapPost("/groups", CreateGroupAsync);
        group.MapPut("/groups/{id}", UpdateGroupAsync);
        group.MapDelete("/groups/{id}", DeleteGroupAsync);

        // ── 注册码 ──
        group.MapGet("/enroll-codes", ListEnrollCodesAsync);
        group.MapPost("/enroll-codes", CreateEnrollCodeAsync);
        group.MapPut("/enroll-codes/{code}", UpdateEnrollCodeAsync);
        group.MapDelete("/enroll-codes/{code}", DeleteEnrollCodeAsync);

        // ── 下发绑定与推送 ──
        group.MapGet("/assignments", ListAssignmentsAsync);
        group.MapPost("/push", PushAsync);
    }

    // ────────────────────────────── 设备 ──────────────────────────────

    /// <summary>列出全部设备及其实时状态。</summary>
    private static async Task<ApiResult<List<DeviceSummaryDto>>> ListDevicesAsync(
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var devices = await store.GetDevicesAsync(cancellationToken);
        return ApiResult<List<DeviceSummaryDto>>.Success(
            await sync.GetDeviceSummariesAsync(devices, cancellationToken));
    }

    /// <summary>修改设备名称、所属分组与绑定档案。</summary>
    private static async Task<ApiResult<DeviceSummaryDto>> UpdateDeviceAsync(
        string id,
        DeviceUpdateRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var device = await store.GetDeviceAsync(id, cancellationToken)
                     ?? throw HubException.NotFound("设备不存在。");

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0)
            {
                throw HubException.Validation("设备名称不能为空。");
            }

            await store.RenameDeviceAsync(id, name, cancellationToken);
        }

        if (request.GroupId is not null)
        {
            var groupId = string.IsNullOrWhiteSpace(request.GroupId) ? null : request.GroupId.Trim();
            if (groupId is not null && await store.GetGroupAsync(groupId, cancellationToken) is null)
            {
                throw HubException.Validation("指定的分组不存在。");
            }

            await store.SetDeviceGroupAsync(id, groupId, cancellationToken);
        }

        if (request.ProfileId is not null)
        {
            var profileId = string.IsNullOrWhiteSpace(request.ProfileId) ? null : request.ProfileId.Trim();
            if (profileId is not null && await store.GetProfileAsync(profileId, cancellationToken) is null)
            {
                throw HubException.Validation("指定的配置档案不存在。");
            }

            await store.SetDeviceProfileAsync(id, profileId, cancellationToken);
        }

        await store.AddAuditAsync(session.Username, "device.update", device.Name,
            "更新了设备的分组/档案绑定。", http.GetClientIpAddress(), cancellationToken);

        var updated = await store.GetDeviceAsync(id, cancellationToken)
                      ?? throw HubException.NotFound("设备不存在。");

        // 设备配置来源可能发生变化，唤醒它立即重新拉取。
        await store.BumpPushEpochAsync([id], cancellationToken);

        var summaries = await sync.GetDeviceSummariesAsync([updated], cancellationToken);
        return ApiResult<DeviceSummaryDto>.Success(summaries[0]);
    }

    /// <summary>停用/启用设备。</summary>
    private static async Task<ApiResult<bool>> RevokeDeviceAsync(
        string id,
        RevokeRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var device = await store.GetDeviceAsync(id, cancellationToken)
                     ?? throw HubException.NotFound("设备不存在。");

        await store.SetDeviceRevokedAsync(id, request.Revoked, cancellationToken);
        await store.AddAuditAsync(session.Username, request.Revoked ? "device.revoke" : "device.restore",
            device.Name, request.Revoked ? "停用了该设备。" : "恢复了该设备。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    /// <summary>删除设备记录。</summary>
    private static async Task<ApiResult<bool>> DeleteDeviceAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var device = await store.GetDeviceAsync(id, cancellationToken)
                     ?? throw HubException.NotFound("设备不存在。");

        await store.ClearDeviceLogsAsync(id, cancellationToken);
        await store.DeleteDeviceAsync(id, cancellationToken);
        await store.AddAuditAsync(session.Username, "device.delete", device.Name,
            "删除了设备记录。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    /// <summary>查看设备上报的日志。</summary>
    private static async Task<ApiResult<List<LogEntryDto>>> GetDeviceLogsAsync(
        string id,
        HttpContext http,
        HubStore store,
        int? limit,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var logs = await store.GetClientLogsAsync(id, Math.Clamp(limit ?? 200, 1, 1000), cancellationToken);

        return ApiResult<List<LogEntryDto>>.Success(logs.Select(l => new LogEntryDto
        {
            Timestamp = l.Timestamp,
            Level = l.Level,
            Message = l.Message,
        }).ToList());
    }

    /// <summary>清空设备日志。</summary>
    private static async Task<ApiResult<bool>> ClearDeviceLogsAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        await store.ClearDeviceLogsAsync(id, cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    // ────────────────────────────── 分组 ──────────────────────────────

    /// <summary>列出全部分组。</summary>
    private static async Task<ApiResult<List<GroupDto>>> ListGroupsAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var groups = await store.GetGroupsAsync(cancellationToken);
        var counts = await store.GetGroupDeviceCountsAsync(cancellationToken);

        return ApiResult<List<GroupDto>>.Success(groups.Select(g => new GroupDto
        {
            Id = g.Id,
            Name = g.Name,
            Description = g.Description,
            DefaultProfileId = g.DefaultProfileId,
            DeviceCount = counts.GetValueOrDefault(g.Id),
            CreatedAt = g.CreatedAt,
        }).ToList());
    }

    /// <summary>新建分组。</summary>
    private static async Task<ApiResult<GroupDto>> CreateGroupAsync(
        GroupUpsertRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw HubException.Validation("分组名称不能为空。");
        }

        var row = new GroupRow
        {
            Id = HubChecksum.NewId(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            DefaultProfileId = string.IsNullOrWhiteSpace(request.DefaultProfileId) ? null : request.DefaultProfileId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateGroupAsync(row, cancellationToken);
        await store.AddAuditAsync(session.Username, "group.create", row.Name,
            "创建了分组。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<GroupDto>.Success(new GroupDto
        {
            Id = row.Id,
            Name = row.Name,
            Description = row.Description,
            DefaultProfileId = row.DefaultProfileId,
            DeviceCount = 0,
            CreatedAt = row.CreatedAt,
        });
    }

    /// <summary>修改分组。</summary>
    private static async Task<ApiResult<bool>> UpdateGroupAsync(
        string id,
        GroupUpsertRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var group = await store.GetGroupAsync(id, cancellationToken)
                    ?? throw HubException.NotFound("分组不存在。");

        var profileId = string.IsNullOrWhiteSpace(request.DefaultProfileId) ? null : request.DefaultProfileId;
        if (profileId is not null && await store.GetProfileAsync(profileId, cancellationToken) is null)
        {
            throw HubException.Validation("指定的配置档案不存在。");
        }

        await store.UpdateGroupAsync(id,
            string.IsNullOrWhiteSpace(request.Name) ? group.Name : request.Name.Trim(),
            request.Description?.Trim() ?? group.Description,
            profileId,
            cancellationToken);

        await store.AddAuditAsync(session.Username, "group.update", group.Name,
            "更新了分组设置。", http.GetClientIpAddress(), cancellationToken);

        // 分组默认档案变化会影响到组内全部设备，逐个唤醒它们。
        var devices = await store.GetDevicesByGroupAsync(id, cancellationToken);
        await store.BumpPushEpochAsync(devices.Select(d => d.Id), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    /// <summary>删除分组。</summary>
    private static async Task<ApiResult<bool>> DeleteGroupAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var group = await store.GetGroupAsync(id, cancellationToken)
                    ?? throw HubException.NotFound("分组不存在。");

        await store.DeleteGroupAsync(id, cancellationToken);
        await store.AddAuditAsync(session.Username, "group.delete", group.Name,
            "删除了分组，组内设备已变为未分组。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    // ────────────────────────────── 注册码 ──────────────────────────────

    /// <summary>列出全部注册码。</summary>
    private static async Task<ApiResult<List<EnrollCodeDto>>> ListEnrollCodesAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var codes = await store.GetEnrollCodesAsync(cancellationToken);
        return ApiResult<List<EnrollCodeDto>>.Success(codes.Select(ToEnrollCodeDto).ToList());
    }

    /// <summary>生成新注册码。</summary>
    private static async Task<ApiResult<EnrollCodeDto>> CreateEnrollCodeAsync(
        EnrollCodeUpsertRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        var row = new EnrollCodeRow
        {
            Code = HubChecksum.NewEnrollCode(),
            Note = request.Note?.Trim() ?? string.Empty,
            MaxUses = Math.Max(0, request.MaxUses),
            UsedCount = 0,
            ExpiresAt = request.ValidHours > 0
                ? DateTimeOffset.UtcNow.AddHours(request.ValidHours)
                : null,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateEnrollCodeAsync(row, cancellationToken);
        await store.AddAuditAsync(session.Username, "enrollcode.create", row.Code,
            $"生成注册码，可用次数 {row.MaxUses}。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<EnrollCodeDto>.Success(ToEnrollCodeDto(row));
    }

    /// <summary>修改注册码。</summary>
    private static async Task<ApiResult<bool>> UpdateEnrollCodeAsync(
        string code,
        EnrollCodeUpsertRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await store.GetEnrollCodeAsync(code, cancellationToken)
                  ?? throw HubException.NotFound("注册码不存在。");

        var expiresAt = request.ValidHours > 0
            ? DateTimeOffset.UtcNow.AddHours(request.ValidHours)
            : (DateTimeOffset?)null;

        await store.UpdateEnrollCodeAsync(code, request.Note?.Trim() ?? row.Note,
            Math.Max(0, request.MaxUses), expiresAt, true, cancellationToken);

        await store.AddAuditAsync(session.Username, "enrollcode.update", code,
            "更新了注册码。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    /// <summary>删除注册码。</summary>
    private static async Task<ApiResult<bool>> DeleteEnrollCodeAsync(
        string code,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        await store.DeleteEnrollCodeAsync(code, cancellationToken);
        await store.AddAuditAsync(session.Username, "enrollcode.delete", code,
            "删除了注册码。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    // ────────────────────────────── 下发 ──────────────────────────────

    /// <summary>
    /// 列出当前的下发绑定关系（设备 → 档案、分组 → 档案）。
    /// 绑定关系由设备/分组的配置字段承载，这里汇总为统一视图便于管理端展示。
    /// </summary>
    private static async Task<ApiResult<List<AssignmentDto>>> ListAssignmentsAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();

        var profiles = (await store.GetProfilesAsync(cancellationToken))
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var groups = (await store.GetGroupsAsync(cancellationToken))
            .ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        var devices = await store.GetDevicesAsync(cancellationToken);

        var result = new List<AssignmentDto>();

        foreach (var group in groups.Values.Where(g => !string.IsNullOrEmpty(g.DefaultProfileId)))
        {
            result.Add(new AssignmentDto
            {
                Id = $"group:{group.Id}",
                TargetType = "group",
                TargetId = group.Id,
                TargetName = group.Name,
                ProfileId = group.DefaultProfileId!,
                ProfileName = profiles.GetValueOrDefault(group.DefaultProfileId!)?.Name,
                CreatedAt = group.CreatedAt,
            });
        }

        foreach (var device in devices.Where(d => !string.IsNullOrEmpty(d.ProfileId)))
        {
            result.Add(new AssignmentDto
            {
                Id = $"device:{device.Id}",
                TargetType = "device",
                TargetId = device.Id,
                TargetName = device.Name,
                ProfileId = device.ProfileId!,
                ProfileName = profiles.GetValueOrDefault(device.ProfileId!)?.Name,
                CreatedAt = device.CreatedAt,
            });
        }

        return ApiResult<List<AssignmentDto>>.Success(result);
    }

    /// <summary>立即推送：让目标设备马上重新拉取配置。</summary>
    private static async Task<ApiResult<object>> PushAsync(
        PushRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        var (affected, revision) = await sync.PushAsync(request, cancellationToken);

        await store.AddAuditAsync(session.Username, "config.push", request.Scope,
            $"推送范围 {request.Scope}，影响 {affected} 台设备，当前版本 {revision}。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<object>.Success(new
        {
            affected,
            revision,
            scope = request.Scope,
        });
    }

    private static EnrollCodeDto ToEnrollCodeDto(EnrollCodeRow row) => new()
    {
        Code = row.Code,
        Note = row.Note,
        MaxUses = row.MaxUses,
        UsedCount = row.UsedCount,
        ExpiresAt = row.ExpiresAt,
        Enabled = row.Enabled,
        CreatedAt = row.CreatedAt,
    };
}

/// <summary>修改设备信息的请求体。字段为 <c>null</c> 表示不修改该项。</summary>
public sealed class DeviceUpdateRequest
{
    /// <summary>新的设备名称。</summary>
    public string? Name { get; set; }

    /// <summary>新的分组 ID，空字符串表示移出分组。</summary>
    public string? GroupId { get; set; }

    /// <summary>新的配置档案 ID，空字符串表示改为继承分组/默认档案。</summary>
    public string? ProfileId { get; set; }
}

/// <summary>停用/恢复设备的请求体。</summary>
public sealed class RevokeRequest
{
    /// <summary>是否停用。</summary>
    public bool Revoked { get; set; } = true;
}
