using System.Globalization;
using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;
using IslandHub.Server.Http;
using IslandHub.Server.Services;

namespace IslandHub.Server.Endpoints;

/// <summary>
/// 定时提醒接口，挂载在 <c>/api/v1/admin</c> 下。
/// <para>
/// <b>数据隔离</b>：每条提醒都归属创建它的账号，所有读写都带 <c>user_id</c> 过滤；
/// 访问别人的提醒一律按「不存在」返回，不会泄漏其它账号的提醒内容。
/// </para>
/// </summary>
public static class ReminderEndpoints
{
    /// <summary>注册定时提醒路由。</summary>
    public static void MapReminderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/reminders", ListAsync).RequirePermission(PermissionKeys.RemindersRead);
        group.MapGet("/reminders/summary", SummaryAsync).RequirePermission(PermissionKeys.RemindersRead);
        group.MapGet("/reminders/fires", ListFiresAsync).RequirePermission(PermissionKeys.RemindersRead);
        group.MapPost("/reminders", CreateAsync).RequirePermission(PermissionKeys.RemindersWrite);
        group.MapPut("/reminders/{id}", UpdateAsync).RequirePermission(PermissionKeys.RemindersWrite);
        group.MapDelete("/reminders/{id}", DeleteAsync).RequirePermission(PermissionKeys.RemindersWrite);
        group.MapPost("/reminders/{id}/run", RunNowAsync).RequirePermission(PermissionKeys.RemindersWrite);
    }

    /// <summary>列出当前账号的全部提醒。</summary>
    private static async Task<ApiResult<List<ReminderDto>>> ListAsync(
        HttpContext http,
        HubStore store,
        ReminderService reminders,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var rows = await store.GetRemindersAsync(session.UserId, cancellationToken);

        var list = new List<ReminderDto>();
        foreach (var row in rows)
        {
            list.Add(await reminders.ToDtoAsync(row, cancellationToken));
        }

        return ApiResult<List<ReminderDto>>.Success(list);
    }

    /// <summary>提醒总览：总数 / 启用数 / 近 24 小时触发次数 / 下次触发时间。</summary>
    private static async Task<ApiResult<ReminderSummaryDto>> SummaryAsync(
        HttpContext http,
        HubStore store,
        ReminderService reminders,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var rows = await store.GetRemindersAsync(session.UserId, cancellationToken);
        var fires = await store.GetReminderFiresAsync(session.UserId, null, 200, cancellationToken);
        var since = reminders.Now.AddHours(-24);

        return ApiResult<ReminderSummaryDto>.Success(new ReminderSummaryDto
        {
            Total = rows.Count,
            Enabled = rows.Count(r => r.Enabled),
            FiredLast24Hours = fires.Count(f => f.FiredAt >= since),
            NextFireAt = rows.Where(r => r.Enabled && r.NextFireAt.HasValue)
                .Select(r => r.NextFireAt)
                .OrderBy(t => t)
                .FirstOrDefault(),
        });
    }

    /// <summary>查看触发历史（只含当前账号的提醒）。</summary>
    private static async Task<ApiResult<List<ReminderFireDto>>> ListFiresAsync(
        string? reminderId,
        int? limit,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var rows = await store.GetReminderFiresAsync(session.UserId, reminderId,
            Math.Clamp(limit ?? 100, 1, 500), cancellationToken);

        // 回填标题，便于历史列表直接展示。
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in await store.GetRemindersAsync(session.UserId, cancellationToken))
        {
            titles[row.Id] = row.Title;
        }

        return ApiResult<List<ReminderFireDto>>.Success(rows.Select(f => new ReminderFireDto
        {
            Id = f.Id,
            ReminderId = f.ReminderId,
            ReminderTitle = titles.GetValueOrDefault(f.ReminderId, "（提醒已删除）"),
            Action = f.Action,
            FiredAt = f.FiredAt,
            Affected = f.Affected,
            Skipped = f.Skipped,
            Detail = f.Detail,
        }).ToList());
    }

    /// <summary>
    /// 把请求里的触发方式落到行上（#12）。非法取值**直接拒绝**，不静默改成按时间——
    /// 管理员以为建了一条「设备上线就发」的提醒，结果它按 08:00 触发，这种错最难查。
    /// </summary>
    private static void ApplyTriggerKind(ReminderRow row, ReminderUpsertRequest request)
    {
        if (!ReminderTriggers.IsValid(request.TriggerKind))
        {
            throw HubException.Validation("触发方式只能是 time（按时间）或 device_online（设备上线）。");
        }

        row.TriggerKind = string.IsNullOrWhiteSpace(request.TriggerKind)
            ? ReminderTriggers.Time
            : request.TriggerKind.Trim();
    }

    /// <summary>审计文案里的「什么时候触发」：事件触发不该显示成排程（会让人以为它按时触发）。</summary>
    private static string DescribeTrigger(ReminderRow row) => row.TriggerKind switch
    {
        ReminderTriggers.DeviceOnline => "设备上线时触发",
        ReminderTriggers.SyncCompleted => "配置同步完成时触发",
        _ => DescribeRepeat(row),
    };

    /// <summary>新建提醒。</summary>
    private static async Task<ApiResult<ReminderDto>> CreateAsync(
        ReminderUpsertRequest request,
        HttpContext http,
        HubStore store,
        ReminderService reminders,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var now = reminders.Now;

        var row = new ReminderRow
        {
            Id = HubChecksum.NewId(),
            UserId = session.UserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        ApplyTriggerKind(row, request);
        ApplyRequest(row, request);
        await EnsureTargetExistsAsync(store, row, cancellationToken);

        // 事件触发（#12）不参与排程：next_fire_at 保持为空，调度器因此天然不会选中它
        // （GetDueRemindersAsync 只取 next_fire_at 非空的），「必须有下次触发」这条校验也就不适用。
        row.NextFireAt = ReminderTriggers.IsEvent(row.TriggerKind) || !row.Enabled
            ? null
            : ReminderService.ComputeNextFire(row, now);
        if (!ReminderTriggers.IsEvent(row.TriggerKind))
        {
            EnsureHaveNextFire(row);
        }

        await store.CreateReminderAsync(row, cancellationToken);
        await store.AddAuditAsync(session.Username, "reminder.create", row.Title,
            $"新建提醒（{DescribeAction(row.Action)} · {DescribeTrigger(row)}，目标：{row.TargetType}）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<ReminderDto>.Success(await reminders.ToDtoAsync(row, cancellationToken));
    }

    /// <summary>修改提醒。</summary>
    private static async Task<ApiResult<ReminderDto>> UpdateAsync(
        string id,
        ReminderUpsertRequest request,
        HttpContext http,
        HubStore store,
        ReminderService reminders,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await RequireOwnAsync(store, session, id, cancellationToken);

        ApplyTriggerKind(row, request);
        ApplyRequest(row, request);
        await EnsureTargetExistsAsync(store, row, cancellationToken);
        row.UpdatedAt = reminders.Now;

        // 同 CreateAsync：事件触发不写排程，也不走「必须有下次触发」的校验。
        row.NextFireAt = ReminderTriggers.IsEvent(row.TriggerKind) || !row.Enabled
            ? null
            : ReminderService.ComputeNextFire(row, reminders.Now);
        if (!ReminderTriggers.IsEvent(row.TriggerKind))
        {
            EnsureHaveNextFire(row);
        }

        await store.UpdateReminderAsync(row, cancellationToken);
        await store.AddAuditAsync(session.Username, "reminder.update", row.Title,
            $"修改提醒（{DescribeAction(row.Action)} · {DescribeTrigger(row)}）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<ReminderDto>.Success(await reminders.ToDtoAsync(row, cancellationToken));
    }

    /// <summary>删除提醒及其历史。</summary>
    private static async Task<ApiResult<bool>> DeleteAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await RequireOwnAsync(store, session, id, cancellationToken);

        await store.DeleteReminderAsync(id, cancellationToken);
        await store.AddAuditAsync(session.Username, "reminder.delete", row.Title,
            "删除了定时提醒。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    /// <summary>立即触发一次（用于验证配置是否正确），不影响原有重复计划。</summary>
    private static async Task<ApiResult<ReminderFireDto>> RunNowAsync(
        string id,
        HttpContext http,
        HubStore store,
        ReminderService reminders,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await RequireOwnAsync(store, session, id, cancellationToken);

        // 触发是「领取式」的：抢不到说明这一轮刚被调度器（或另一个实例）执行过，如实告知而不是假装成功。
        var fire = await reminders.FireAsync(row, reminders.Now, cancellationToken)
                   ?? throw HubException.Conflict("这条提醒刚刚已被触发（可能由调度器或另一个实例执行），请刷新后重试。");

        await store.AddAuditAsync(session.Username, "reminder.run", row.Title,
            $"手动触发提醒（{DescribeAction(fire.Action)}，影响 {fire.Affected} 台，跳过 {fire.Skipped} 台）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<ReminderFireDto>.Success(new ReminderFireDto
        {
            Id = fire.Id,
            ReminderId = fire.ReminderId,
            ReminderTitle = row.Title,
            Action = fire.Action,
            FiredAt = fire.FiredAt,
            Affected = fire.Affected,
            Skipped = fire.Skipped,
            Detail = fire.Detail,
        });
    }

    // ────────────────────────────── 校验与转换 ──────────────────────────────

    /// <summary>动作的中文名（审计 / 列表 / 日志共用一份，别在多处各写一遍）。</summary>
    private static string DescribeAction(string action) => action switch
    {
        ReminderActions.PowerShutdown => "定时关机",
        ReminderActions.PowerWake => "定时开机",
        _ => "发通知",
    };

    /// <summary>取当前账号自己的提醒；别人的一律按「不存在」处理，实现数据隔离。</summary>
    private static async Task<ReminderRow> RequireOwnAsync(HubStore store, SessionRow session, string id,
        CancellationToken cancellationToken)
    {
        var row = await store.GetReminderAsync(id, cancellationToken);
        if (row is null || !string.Equals(row.UserId, session.UserId, StringComparison.Ordinal))
        {
            throw HubException.NotFound("提醒不存在。");
        }

        return row;
    }

    /// <summary>把请求体归一化写入提醒行；不合法时抛出业务异常。</summary>
    private static void ApplyRequest(ReminderRow row, ReminderUpsertRequest request)
    {
        // 动作留空 = 发通知（老客户端不带这个字段，语义必须保持不变）。
        var action = (request.Action ?? string.Empty).Trim();
        if (!ReminderActions.IsValid(action))
        {
            throw HubException.Validation(
                $"动作不正确：只能是 {string.Join(" / ", ReminderActions.All)}。");
        }

        var normalized = action.Length == 0 ? ReminderActions.Notify : action;
        var title = (request.Title ?? string.Empty).Trim();
        var content = (request.Content ?? string.Empty).Trim();

        // 「标题与内容不能同时为空」只对**通知**成立：定时开关机不是消息，内容本来就没意义。
        if (normalized == ReminderActions.Notify && title.Length == 0 && content.Length == 0)
        {
            throw HubException.Validation("提醒标题与内容不能同时为空。");
        }

        // 关机 / 开机的标题留空时给个默认名，列表里一眼能看出这条是什么。
        if (normalized != ReminderActions.Notify && title.Length == 0)
        {
            title = DescribeAction(normalized);
        }

        row.Title = title;
        row.Content = content;
        row.Action = normalized;
        // 目前只实现「推送到教室大屏」一种投递方式；邮件/短信需要外部服务凭证。
        row.Channel = ReminderChannels.Screen;
        row.Speak = request.Speak;
        row.Enabled = request.Enabled;

        var targetType = (request.TargetType ?? ReminderTargets.All).Trim().ToLowerInvariant();
        if (!ReminderTargets.AllValues.Contains(targetType))
        {
            throw HubException.Validation("投递目标类型不正确。");
        }

        row.TargetType = targetType;
        row.TargetId = targetType == ReminderTargets.All ? string.Empty : (request.TargetId ?? string.Empty).Trim();
        if (targetType != ReminderTargets.All && row.TargetId.Length == 0)
        {
            throw HubException.Validation("请选择要提醒的分组或教室。");
        }

        var kind = (request.RepeatKind ?? ReminderRepeats.Once).Trim().ToLowerInvariant();
        if (!ReminderRepeats.All.Contains(kind))
        {
            throw HubException.Validation("重复规则不正确。");
        }

        row.RepeatKind = kind;
        row.RepeatInterval = Math.Clamp(request.RepeatInterval <= 0 ? 1 : request.RepeatInterval, 1, 365);

        var weekdays = (request.RepeatWeekdays ?? [])
            .Where(n => n is >= 1 and <= 7)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        if (kind == ReminderRepeats.Weekly && weekdays.Count == 0)
        {
            throw HubException.Validation("按周重复时至少要选择一天。");
        }

        row.RepeatWeekdays = string.Join(',', weekdays);
        row.RepeatDayOfMonth = request.RepeatDayOfMonth is >= 1 and <= 31 ? request.RepeatDayOfMonth : 0;

        var time = ReminderService.TryParseTime(request.FireTime);
        if (time is null)
        {
            throw HubException.Validation("触发时间格式应为 HH:mm。");
        }

        row.FireTime = time.Value.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (!DateOnly.TryParse(request.StartDate, out var start))
        {
            throw HubException.Validation("起始日期格式应为 yyyy-MM-dd。");
        }

        row.StartDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(request.EndDate))
        {
            row.EndDate = null;
            return;
        }

        if (!DateOnly.TryParse(request.EndDate, out var end))
        {
            throw HubException.Validation("结束日期格式应为 yyyy-MM-dd。");
        }

        if (end < start)
        {
            throw HubException.Validation("结束日期不能早于起始日期。");
        }

        row.EndDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>启用中的提醒必须算得出下次触发时间，否则就是配错了（例如日期已过）。</summary>
    private static void EnsureHaveNextFire(ReminderRow row)
    {
        if (row.Enabled && row.NextFireAt is null)
        {
            throw HubException.Validation("按当前规则算不出下次触发时间：请检查起始日期、结束日期与触发时间（「仅一次」的日期不能早于现在）。");
        }
    }

    private static async Task EnsureTargetExistsAsync(HubStore store, ReminderRow row,
        CancellationToken cancellationToken)
    {
        switch (row.TargetType)
        {
            case ReminderTargets.Group when await store.GetGroupAsync(row.TargetId, cancellationToken) is null:
                throw HubException.Validation("指定的分组（楼栋 / 楼层）不存在。");
            case ReminderTargets.Device when await store.GetDeviceAsync(row.TargetId, cancellationToken) is null:
                throw HubException.Validation("指定的教室设备不存在。");
        }
    }

    private static string DescribeRepeat(ReminderRow row) => row.RepeatKind switch
    {
        ReminderRepeats.Daily => row.RepeatInterval > 1 ? $"每 {row.RepeatInterval} 天 {row.FireTime}" : $"每天 {row.FireTime}",
        ReminderRepeats.Weekly => $"每 {row.RepeatInterval} 周（周 {row.RepeatWeekdays}）{row.FireTime}",
        ReminderRepeats.Monthly => row.RepeatInterval > 1
            ? $"每 {row.RepeatInterval} 月 {row.FireTime}"
            : $"每月 {row.FireTime}",
        _ => $"{row.StartDate} {row.FireTime} 仅一次",
    };
}
