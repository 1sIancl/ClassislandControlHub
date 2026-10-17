using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;
using IslandHub.Server.Http;
using IslandHub.Server.Options;
using IslandHub.Server.Services;
using Microsoft.Extensions.Options;

namespace IslandHub.Server.Endpoints;

/// <summary>
/// 快捷换课（与 ClassIsland 深度集成的那一层）。
///
/// <para>与「档案页的临时换课」的分工：那边记在 A 端、由服务端在下发时**合成**进课表，
/// 适合「提前排好一次调整、全楼统一」；这里直接把指令发到教室，
/// 由插件写进 <b>ClassIsland 自己的临时层课表 / 日期预定</b>，效果与在教室大屏上手动换课一致，
/// 适合「现在就改这一节」。两者最终都会改变教室端实际上的课，界面里必须说清区别。</para>
///
/// <para>权限：读（实时状态 / 课表目录）沿用 <c>devices.read</c>——课表状态就是设备状态的一部分；
/// 写（换课 / 调休）沿用 <c>remote.write</c>——它本质上是向教室下发指令，
/// 与远程重启、下发外观走同一道门，能远程操作教室的人才该能换课。</para>
/// </summary>
public static class LessonEndpoints
{
    /// <summary>注册课表相关路由。</summary>
    public static void MapLessonEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/lessons/live", LiveAsync).RequirePermission(PermissionKeys.DevicesRead);
        group.MapGet("/lessons/catalog", GetCatalogAsync).RequirePermission(PermissionKeys.DevicesRead);
        group.MapPost("/lessons/catalog", RequestCatalogAsync).RequirePermission(PermissionKeys.DevicesRead);
        group.MapPost("/lessons/swap", SwapAsync).RequirePermission(PermissionKeys.RemoteWrite);
        group.MapPost("/lessons/adjust", AdjustAsync).RequirePermission(PermissionKeys.RemoteWrite);
    }

    /// <summary>所有设备的课表实时状态（页面上的「下节课 + 倒计时」）。</summary>
    private static async Task<ApiResult<LessonLiveOverviewDto>> LiveAsync(
        HttpContext http,
        HubStore store,
        IOptionsMonitor<ServerOptions> options,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();

        var devices = await store.GetDevicesAsync(cancellationToken);
        var states = await store.GetLessonStatesAsync(cancellationToken);
        var groups = (await store.GetGroupsAsync(cancellationToken))
            .ToDictionary(g => g.Id, g => g.Name, StringComparer.Ordinal);

        var overview = new LessonLiveOverviewDto { ServerTime = DateTimeOffset.UtcNow };
        foreach (var device in devices)
        {
            if (device.Revoked)
            {
                continue;
            }

            states.TryGetValue(device.Id, out var row);
            overview.Devices.Add(new DeviceLessonStateDto
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                GroupName = device.GroupId is { } gid && groups.TryGetValue(gid, out var gname) ? gname : null,
                IsOnline = IsOnline(device, options.CurrentValue),
                CatalogAt = row?.CatalogAt,
                Lesson = row is null
                    ? null
                    : new LessonStateDto
                    {
                        State = row.State,
                        Date = row.Date,
                        ClassPlanName = row.ClassPlanName,
                        CurrentSubject = row.CurrentSubject,
                        NextSubject = row.NextSubject,
                        NextStartAt = row.NextStartAt,
                        NextEndAt = row.NextEndAt,
                        LeftSeconds = row.LeftSeconds,
                        IsAdjusted = row.IsAdjusted,
                        RotationWeeks = row.RotationWeeks,
                        RotationWeek = row.RotationWeek,
                        ReportedAt = row.ReportedAt,
                    },
            });
        }

        return ApiResult<LessonLiveOverviewDto>.Success(overview);
    }

    /// <summary>读取某台设备上次回报的课表目录（含今天各节、科目、课表）。</summary>
    private static async Task<ApiResult<LessonCatalogDto?>> GetCatalogAsync(
        string deviceId,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw HubException.Validation("请指定 deviceId。");
        }

        var json = await store.GetLessonCatalogAsync(deviceId.Trim(), cancellationToken);
        return ApiResult<LessonCatalogDto?>.Success(
            string.IsNullOrWhiteSpace(json) ? null : HubJson.Deserialize<LessonCatalogDto>(json));
    }

    /// <summary>请求设备重新上报课表目录（页面上的「读取课表」）。</summary>
    private static async Task<ApiResult<bool>> RequestCatalogAsync(
        LessonDispatchRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var targets = await ResolveTargetsAsync(request, store, cancellationToken);
        if (targets.Count == 0)
        {
            throw HubException.Validation("没有匹配的设备。");
        }

        await DispatchAsync(store, sync, targets, RemoteCommandKinds.LessonCatalog, "{}",
            http.RequireAdminSession().Username, cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    /// <summary>临时换课（或恢复原课表）。</summary>
    private static async Task<ApiResult<int>> SwapAsync(
        LessonDispatchRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var swap = request.Swap ?? throw HubException.Validation("缺少换课参数。");

        var targets = await ResolveTargetsAsync(request, store, cancellationToken);
        if (targets.Count == 0)
        {
            throw HubException.Validation("没有匹配的设备。");
        }

        var affected = await DispatchAsync(store, sync, targets, RemoteCommandKinds.LessonSwap,
            HubJson.Serialize(swap), session.Username, cancellationToken);

        var detail = swap.Restore
            ? "恢复原课表（清除教室端临时层）。"
            : swap.Swap
                ? $"第 {swap.ClassIndex + 1} 节与第 {swap.SwapWithIndex + 1} 节对调"
                  + $"（日期 {swap.Date ?? "教室端今天"}）。"
                : $"第 {swap.ClassIndex + 1} 节换为「{swap.SubjectName ?? swap.SubjectId}」"
                  + $"（日期 {swap.Date ?? "教室端今天"}）。";
        await store.AddAuditAsync(session.Username, "lesson.swap", string.Join(",", targets.Select(d => d.Name)),
            $"{detail} 目标 {affected} 台。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<int>.Success(affected);
    }

    /// <summary>调休：给某个日期指定一张课表，或撤销该日期的调休。</summary>
    private static async Task<ApiResult<int>> AdjustAsync(
        LessonDispatchRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var adjust = request.Adjust ?? throw HubException.Validation("缺少调休参数。");

        var targets = await ResolveTargetsAsync(request, store, cancellationToken);
        if (targets.Count == 0)
        {
            throw HubException.Validation("没有匹配的设备。");
        }

        var affected = await DispatchAsync(store, sync, targets, RemoteCommandKinds.LessonAdjust,
            HubJson.Serialize(adjust), session.Username, cancellationToken);

        var detail = adjust.Restore
            ? $"撤销 {adjust.Date} 的调休。"
            : $"{adjust.Date} 改用课表「{adjust.ClassPlanName ?? adjust.ClassPlanId}」。";
        await store.AddAuditAsync(session.Username, "lesson.adjust", string.Join(",", targets.Select(d => d.Name)),
            $"{detail} 目标 {affected} 台。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<int>.Success(affected);
    }

    // ────────────────────────────── 内部工具 ──────────────────────────────

    /// <summary>解析目标设备：给定了设备 ID 就用它，否则按分组取（不含已停用设备）。</summary>
    /// <remarks>
    /// <para>**两边都不给就报错，而不是默默取全部设备。** 早先这里把「没指定」当成「全部」，
    /// 前端又恰好一个目标都不传，于是「把某个班第 3 节换成数学」实际发给了全校每一台教室机；
    /// 而课表类指令的载荷（节次、科目）是从**某一台**设备读来的，发给别的班多半是错的。
    /// 真要整栋楼统一，前端把设备 ID 显式列出来（界面上对应「全部设备」那一项）。</para>
    /// </remarks>
    private static async Task<List<DeviceRow>> ResolveTargetsAsync(
        LessonDispatchRequest request, HubStore store, CancellationToken cancellationToken)
    {
        if (request.DeviceIds.Count == 0 && string.IsNullOrWhiteSpace(request.GroupId))
        {
            throw HubException.Validation("请指定目标设备或分组。");
        }

        List<DeviceRow> devices = request.DeviceIds.Count > 0
            ? (await store.GetDevicesAsync(cancellationToken))
                .Where(d => request.DeviceIds.Contains(d.Id, StringComparer.Ordinal)).ToList()
            : await store.GetDevicesByGroupAsync(request.GroupId!.Trim(), cancellationToken);

        return devices.Where(d => !d.Revoked).ToList();
    }

    /// <summary>把一条指令发给一批设备，并唤醒它们立刻来取。</summary>
    private static async Task<int> DispatchAsync(
        HubStore store, SyncService sync, List<DeviceRow> targets, string kind, string payload,
        string actor, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var device in targets)
        {
            await store.CreateCommandAsync(new RemoteCommandRow
            {
                Id = HubChecksum.NewId(),
                DeviceId = device.Id,
                Kind = kind,
                Payload = payload,
                Status = "pending",
                IssuedAt = now,
                IssuedBy = actor,
            }, cancellationToken);
        }

        sync.PublishWakeUp();
        return targets.Count;
    }

    /// <summary>在线判定沿用统一口径（与仪表盘、在线率报表一致）。</summary>
    private static bool IsOnline(DeviceRow device, ServerOptions options) =>
        device.LastSeenAt is { } seen
        && DateTimeOffset.UtcNow - seen < TimeSpan.FromSeconds(Math.Max(10, options.OnlineTimeoutSeconds));
}
