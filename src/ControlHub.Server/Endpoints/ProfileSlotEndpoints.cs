using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Services;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 走班制教室按时段切档案（`#25`）：第 N 节时，这台教室用哪个档案。
///
/// <para>规则存的是**节次序号**而不是时间（作息表会变，节次不会跟着变），所以这里要拿
/// 「这台教室当前生效档案」的作息来告诉界面「一共几节」「现在是第几节」——
/// 界面据此提示「第 6 节越界了」，而不是等后台默默什么都不做。</para>
/// </summary>
public static class ProfileSlotEndpoints
{
    /// <summary>注册走班规则路由。</summary>
    public static void MapProfileSlotEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/profile-slots", ListAsync).RequirePermission(PermissionKeys.DevicesRead);
        group.MapGet("/devices/{id}/profile-slots", GetAsync).RequirePermission(PermissionKeys.DevicesRead);
        group.MapPut("/devices/{id}/profile-slots", UpdateAsync).RequirePermission(PermissionKeys.DevicesWrite);
        group.MapPost("/profile-slots/apply-now", ApplyNowAsync).RequirePermission(PermissionKeys.DevicesWrite);
    }

    /// <summary>所有带规则的教室（界面上的列表）。</summary>
    private static async Task<ApiResult<List<DeviceProfileSlotsDto>>> ListAsync(
        HttpContext http, HubStore store, SyncService sync, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var devices = (await store.GetDevicesAsync(cancellationToken)).ToDictionary(d => d.Id, StringComparer.Ordinal);
        var slots = await store.GetAllProfileSlotsAsync(cancellationToken);
        var profiles = (await store.GetProfilesAsync(cancellationToken)).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var now = TimeOnly.FromDateTime(DateTime.Now);

        var result = new List<DeviceProfileSlotsDto>();
        foreach (var group in slots.GroupBy(s => s.DeviceId))
        {
            if (!devices.TryGetValue(group.Key, out var device))
            {
                continue;   // 设备已删除：规则留着但不再显示（也不会被执行）
            }

            result.Add(await BuildAsync(device, group.ToList(), profiles, sync, store, now, cancellationToken));
        }

        return ApiResult<List<DeviceProfileSlotsDto>>.Success(result.OrderBy(d => d.DeviceName, StringComparer.CurrentCulture).ToList());
    }

    /// <summary>一台教室的规则（编辑器打开时用）。</summary>
    private static async Task<ApiResult<DeviceProfileSlotsDto>> GetAsync(
        string id, HttpContext http, HubStore store, SyncService sync, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var device = (await store.GetDevicesAsync(cancellationToken))
            .FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal))
            ?? throw HubException.NotFound("教室不存在，可能已被删除。");

        var slots = await store.GetDeviceProfileSlotsAsync(id, cancellationToken);
        var profiles = (await store.GetProfilesAsync(cancellationToken)).ToDictionary(p => p.Id, StringComparer.Ordinal);
        return ApiResult<DeviceProfileSlotsDto>.Success(await BuildAsync(device, slots, profiles, sync, store,
            TimeOnly.FromDateTime(DateTime.Now), cancellationToken));
    }

    /// <summary>整体替换一台教室的规则（顺带校验节次范围 —— 别让「第 9 节」这种规则静静躺着）。</summary>
    private static async Task<ApiResult<DeviceProfileSlotsDto>> UpdateAsync(
        string id, DeviceProfileSlotsUpdateRequest request, HttpContext http, HubStore store, SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var device = (await store.GetDevicesAsync(cancellationToken))
            .FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal))
            ?? throw HubException.NotFound("教室不存在，可能已被删除。");

        var incoming = request.Slots ?? [];
        if (incoming.Select(s => s.PeriodIndex).Distinct().Count() != incoming.Count)
        {
            throw HubException.Validation("同一节次只能配置一条规则。");
        }

        var profiles = (await store.GetProfilesAsync(cancellationToken)).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var layout = ProfileSlotResolver.ClassPeriods(await ResolveLayoutAsync(device, store, sync, cancellationToken));
        var periodCount = layout.Count;

        foreach (var slot in incoming)
        {
            if (slot.PeriodIndex < 0 || (periodCount > 0 && slot.PeriodIndex >= periodCount))
            {
                throw HubException.Validation(periodCount > 0
                    ? $"第 {slot.PeriodIndex + 1} 节超出范围：这台教室的作息只有 {periodCount} 节。"
                    : "节次不能为负数。");
            }

            if (!profiles.ContainsKey(slot.ProfileId))
            {
                throw HubException.Validation($"第 {slot.PeriodIndex + 1} 节指定的档案不存在。");
            }
        }

        var rows = incoming
            .OrderBy(s => s.PeriodIndex)
            .Select(s => new HubStore.ProfileSlotRow
            {
                DeviceId = id,
                PeriodIndex = s.PeriodIndex,
                ProfileId = s.ProfileId,
            })
            .ToList();

        await store.ReplaceDeviceProfileSlotsAsync(id, rows, cancellationToken);

        await store.AddAuditAsync(session.Username, "profile-slots.update", device.Name,
            rows.Count == 0
                ? $"清空「{device.Name}」的走班规则。"
                : $"更新「{device.Name}」的走班规则：{Describe(rows, profiles)}。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<DeviceProfileSlotsDto>.Success(await BuildAsync(device, rows, profiles, sync, store,
            TimeOnly.FromDateTime(DateTime.Now), cancellationToken));
    }

    /// <summary>立即按当前时段对齐一次（作息临时调整后不必等下一轮）。</summary>
    private static async Task<ApiResult<ProfileSlotsApplyReportDto>> ApplyNowAsync(
        HttpContext http, HubStore store, ProfileSlotService service, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var report = await service.ReconcileAsync(null, cancellationToken);

        await store.AddAuditAsync(session.Username, "profile-slots.apply", "走班制",
            $"手动按当前时段对齐：检查 {report.DeviceCount} 台，实际切换 {report.Changed} 台。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<ProfileSlotsApplyReportDto>.Success(report);
    }

    /// <summary>组装一台教室的规则视图（含节数、当前节次、当前档案）。</summary>
    private static async Task<DeviceProfileSlotsDto> BuildAsync(DeviceRow device,
        List<HubStore.ProfileSlotRow> slots, Dictionary<string, ProfileRow> profiles, SyncService sync,
        HubStore store, TimeOnly now, CancellationToken cancellationToken)
    {
        var periods = ProfileSlotResolver.ClassPeriods(
            await ResolveLayoutAsync(device, store, sync, cancellationToken));

        return new DeviceProfileSlotsDto
        {
            DeviceId = device.Id,
            DeviceName = device.Name,
            CurrentProfileId = device.ProfileId ?? string.Empty,
            PeriodCount = periods.Count,
            CurrentPeriodIndex = ProfileSlotResolver.ResolvePeriodIndex(
                new TimeLayoutDto { Items = periods }, now),
            Slots = slots.OrderBy(s => s.PeriodIndex).Select(s => new DeviceProfileSlotDto
            {
                PeriodIndex = s.PeriodIndex,
                ProfileId = s.ProfileId,
                ProfileName = profiles.TryGetValue(s.ProfileId, out var p) ? p.Name : "（档案已删除）",
            }).ToList(),
        };
    }

    /// <summary>取这台教室当前生效档案里的作息（节次判定与界面提示都用它）。</summary>
    private static async Task<TimeLayoutDto?> ResolveLayoutAsync(DeviceRow device, HubStore store,
        SyncService sync, CancellationToken cancellationToken)
    {
        var profile = await sync.ResolveProfileAsync(device, cancellationToken);
        var content = HubJson.Deserialize<ContentBundleDto>(profile?.Content ?? string.Empty);
        if (content is null || content.TimeLayouts.Count == 0)
        {
            return null;
        }

        var plan = content.ClassPlans.FirstOrDefault(p => !p.IsOverlay && p.IsEnabled);
        return plan is null
            ? content.TimeLayouts[0]
            : content.TimeLayouts.FirstOrDefault(l => string.Equals(l.Id, plan.TimeLayoutId, StringComparison.Ordinal))
              ?? content.TimeLayouts[0];
    }

    /// <summary>审计里的规则摘要。</summary>
    private static string Describe(List<HubStore.ProfileSlotRow> rows, Dictionary<string, ProfileRow> profiles) =>
        string.Join("、", rows.Select(r =>
            $"第 {r.PeriodIndex + 1} 节 → {(profiles.TryGetValue(r.ProfileId, out var p) ? p.Name : r.ProfileId)}"));
}
