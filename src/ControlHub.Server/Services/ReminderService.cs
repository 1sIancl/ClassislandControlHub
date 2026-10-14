using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using Microsoft.Extensions.Logging;

namespace ControlHub.Server.Services;

/// <summary>
/// 定时提醒服务：负责「算下一次触发时间」与「到点推送到教室大屏」。
/// <para>
/// 提醒本身按用户隔离（<c>user_id</c>），但投递目标（教室 / 楼栋楼层）是全校共享的设备，
/// 因此谁能建提醒由 <see cref="PermissionKeys.RemindersWrite"/> 控制。
/// </para>
/// <para>
/// 触发判定统一使用 <see cref="ServerTimeService.GetUtcNow"/>（含 NTP 与管理员手动偏移校正），
/// 这样「8:00 提醒」与教室大屏上显示的时钟是一致的。
/// </para>
/// </summary>
public sealed class ReminderService(
    HubStore store,
    ServerTimeService serverTime,
    SyncService sync,
    WebhookService webhooks,
    ILogger<ReminderService> logger)
{
    /// <summary>提醒指令的有效期。提醒讲时效，过期就不该再打扰教室。</summary>
    private static readonly TimeSpan CommandTtl = TimeSpan.FromMinutes(30);

    /// <summary>重复规则的最大推演范围（天）。超过这个范围仍未命中就认为不再触发。</summary>
    private const int MaxHorizonDays = 366 * 5;

    /// <summary>当前时间（含校正），所有触发判定都用它。</summary>
    public DateTimeOffset Now => serverTime.GetUtcNow();

    // ────────────────────────────── 触发时间推算 ──────────────────────────────

    /// <summary>
    /// 推算下一次触发时间（UTC）。返回 <c>null</c> 表示这条提醒不会再有下次。
    /// <para>按「服务器本地时间」解释 <c>FireTime</c> 与 <c>StartDate</c>，再换算成 UTC 便于比较与排序。</para>
    /// </summary>
    public static DateTimeOffset? ComputeNextFire(ReminderRow row, DateTimeOffset nowUtc)
    {
        var zone = TimeZoneInfo.Local;
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, zone);

        var start = DateOnly.TryParse(row.StartDate, out var parsedStart)
            ? parsedStart
            : DateOnly.FromDateTime(nowLocal.DateTime);
        var fireTime = TryParseTime(row.FireTime) ?? new TimeOnly(8, 0);
        var end = DateOnly.TryParse(row.EndDate, out var parsedEnd) ? parsedEnd : (DateOnly?)null;

        // 从「今天」和「起始日期」里较晚的那个开始扫，避免为很久以前的提醒做无谓推算。
        var cursor = DateOnly.FromDateTime(nowLocal.DateTime);
        if (start > cursor)
        {
            cursor = start;
        }

        var interval = Math.Max(1, row.RepeatInterval);
        var weekdays = row.WeekdayList();
        var horizon = string.Equals(row.RepeatKind, ReminderRepeats.Once, StringComparison.Ordinal)
            ? 1
            : MaxHorizonDays;

        for (var offset = 0; offset < horizon; offset++)
        {
            var date = cursor.AddDays(offset);
            if (end.HasValue && date > end.Value)
            {
                return null;
            }

            if (!MatchesDate(row, start, date, interval, weekdays))
            {
                continue;
            }

            var candidateUtc = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(fireTime), zone);
            if (candidateUtc > nowUtc)
            {
                return candidateUtc;
            }
        }

        return null;
    }

    private static bool MatchesDate(ReminderRow row, DateOnly start, DateOnly date, int interval,
        List<int> weekdays) => row.RepeatKind switch
        {
            ReminderRepeats.Daily =>
                (date.DayNumber - start.DayNumber) >= 0 && (date.DayNumber - start.DayNumber) % interval == 0,

            ReminderRepeats.Weekly =>
                weekdays.Contains(IsoWeekday(date)) && WeekIndex(start, date) % interval == 0,

            ReminderRepeats.Monthly =>
                date.Day == EffectiveDayOfMonth(row, start) && MonthIndex(start, date) % interval == 0,

            // once：只在起始日期当天触发。
            _ => date == start,
        };

    /// <summary>ISO 星期：1=周一 … 7=周日。</summary>
    private static int IsoWeekday(DateOnly date) => ((int)date.DayOfWeek + 6) % 7 + 1;

    /// <summary>从起始日期所在周算起，<paramref name="date"/> 是第几周（0 起）。</summary>
    private static int WeekIndex(DateOnly start, DateOnly date)
    {
        var startMonday = start.AddDays(-(IsoWeekday(start) - 1));
        var dateMonday = date.AddDays(-(IsoWeekday(date) - 1));
        return Math.Max(0, (dateMonday.DayNumber - startMonday.DayNumber) / 7);
    }

    /// <summary>从起始日期的月份算起，<paramref name="date"/> 是第几个月（0 起）。</summary>
    private static int MonthIndex(DateOnly start, DateOnly date) =>
        Math.Max(0, (date.Year - start.Year) * 12 + (date.Month - start.Month));

    /// <summary>每月几号：优先用管理员指定的，未指定则沿用起始日期的号数。</summary>
    private static int EffectiveDayOfMonth(ReminderRow row, DateOnly start) =>
        row.RepeatDayOfMonth is >= 1 and <= 31 ? row.RepeatDayOfMonth : start.Day;

    /// <summary>解析 <c>HH:mm</c> 或 <c>HH:mm:ss</c>。</summary>
    public static TimeOnly? TryParseTime(string? value) =>
        TimeOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var time) ? time : null;

    // ────────────────────────────── 触发与投递 ──────────────────────────────

    /// <summary>关机指令的有效期：比通用指令短得多（30 分钟）。</summary>
    /// <remarks>
    /// 定时关机讲的是「到点就关」。通用指令的 2 小时 TTL 意味着设备离线一小时后上线仍会执行——
    /// 那台教室机可能已经被老师重新开起来上课了，再被关掉就是事故。
    /// </remarks>
    private static readonly TimeSpan PowerCommandTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 物理动作（关机 / 唤醒）允许迟到多久。超过就只推进计划、不执行。
    /// <para>排程器对错过的触发是**补发**（见 <see cref="FireDueRemindersAsync"/>）：服务停机期间
    /// 过去的时点，重启后仍会被挑出来。通知补发一次无害，但「上周排的关机」在周一早上服务一启动
    /// 就把整栋楼关掉是事故；「昨晚 22:00 的开机」在早上 8:00 补发也没意义（机器早有人开了）。
    /// 跳过会在触发历史里留一条记录，不会静默消失。</para>
    /// </summary>
    private static readonly TimeSpan PowerActionGrace = TimeSpan.FromMinutes(10);

    /// <summary>这次迟到的触发是否要跳过（只有物理动作会被跳过）。</summary>
    public static bool ShouldSkipLateFire(string? action, DateTimeOffset? plannedAt, DateTimeOffset now) =>
        ReminderActions.IsPower(action)
        && plannedAt is { } planned
        && now - planned > PowerActionGrace;

    /// <summary>
    /// 执行所有到点的提醒，返回本轮真正执行的条数（被别人领取走的不算）。
    /// <para><b>错过的触发是补发</b>：服务停机期间过去的时点，重启后仍满足 <c>next_fire_at &lt;= now</c>，
    /// 会在下一个 tick（15 秒内）执行一次，然后把计划推到未来。</para>
    /// </summary>
    public async Task<int> FireDueRemindersAsync(CancellationToken cancellationToken = default)
    {
        var now = Now;
        var due = await store.GetDueRemindersAsync(now, 100, cancellationToken);
        var fired = 0;

        foreach (var row in due)
        {
            try
            {
                if (await FireAsync(row, now, cancellationToken) is not null)
                {
                    fired++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "触发提醒失败：{Id}（{Title}）", row.Id, row.Title);
            }
        }

        return fired;
    }

    /// <summary>
    /// 执行一条提醒：**先原子领取**这次触发（把下次时间推进掉），再按 <c>action</c> 下发通知 / 关机 / 唤醒。
    /// <para>返回 <c>null</c> 表示这次触发已被别处领取（另一个服务实例、或同一分钟的另一次调用）——
    /// 调用方不要当成失败。领取式的好处是：推进失败 / 并发重复调用都不会导致同一次触发被执行两遍，
    /// 而这在「定时关机」上就是「教室机被反复关掉」。</para>
    /// <para>领取之后就不再重试，因此**执行失败也一定会留一条触发记录**（详情里带原因），
    /// 让管理员在界面上看得到，而不是只沉在日志里。</para>
    /// </summary>
    public async Task<ReminderFireRow?> FireAsync(ReminderRow row, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var action = ReminderActions.All.Contains(row.Action) ? row.Action : ReminderActions.Notify;

        // 迟到判定必须用「计划时刻」而不是「收到时刻」：停机补发时两者会差几小时。
        var late = ShouldSkipLateFire(action, row.NextFireAt, now);
        var next = ComputeNextFire(row, now);

        if (!await store.TryClaimReminderFireAsync(row.Id, row.NextFireAt, next, now, cancellationToken))
        {
            logger.LogInformation("提醒「{Title}」的这次触发已被其它实例领取，跳过。", row.Title);
            return null;
        }

        var fire = await ExecuteOnceAsync(row, action, now, late, "按计划", cancellationToken);

        if (next is null)
        {
            logger.LogInformation("提醒「{Title}」已走完重复计划，自动停用。", row.Title);
        }

        return fire;
    }

    /// <summary>事件触发（#12）的冷却时间：设备反复上下线、或全校同时来电时，同一条提醒最多这么频繁。</summary>
    private static readonly TimeSpan EventCooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 某台设备发生了某个**事件**时，执行所有以该事件为触发的提醒（#12）。
    /// <para>只执行**目标覆盖这台设备**的提醒：事件触发的语义是「我关心的那台 / 那组发生了这件事」，
    /// 而不是「随便哪台设备发生都跑一遍」。目标为「全部教室」时跳过目标解析（这正是最常见的情形，
    /// 不值得为它每次都查一遍设备与分组）。</para>
    /// <para>每条提醒单独 try：一条失败不该影响其它提醒，更不该把心跳 / 回报拖出问题。</para>
    /// </summary>
    /// <param name="device">发生事件的设备。</param>
    /// <param name="triggerKind">事件类型，见 <see cref="ReminderTriggers"/>（只取这一类提醒）。</param>
    /// <param name="source">写进触发记录与群消息的来源文案（「设备上线」/「配置同步完成」）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<int> FireDeviceEventAsync(DeviceRow device, string triggerKind, string source,
        CancellationToken cancellationToken = default)
    {
        var now = Now;
        var fired = 0;

        foreach (var row in await store.GetEventRemindersAsync(triggerKind, cancellationToken))
        {
            try
            {
                if (row.TargetType != ReminderTargets.All)
                {
                    var (targets, _, _) = await ResolveTargetsAsync(row, onlineOnly: false, cancellationToken);
                    if (!targets.Any(t => string.Equals(t.Id, device.Id, StringComparison.Ordinal)))
                    {
                        continue;
                    }
                }

                if (await FireEventAsync(row, now, source, cancellationToken) is not null)
                {
                    fired++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Source}触发的提醒执行失败：{Id}（{Title}）", source, row.Id, row.Title);
            }
        }

        return fired;
    }

    /// <summary>
    /// 执行一条**事件触发**的提醒（#12）。与 <see cref="FireAsync"/> 的两点不同：
    /// 不做迟到判定（事件没有「准时」的概念），也不推进排程——改用带冷却的领取，
    /// 冷却期内重复上线不会再执行（见 <see cref="EventCooldown"/>）。
    /// </summary>
    private async Task<ReminderFireRow?> FireEventAsync(ReminderRow row, DateTimeOffset now, string source,
        CancellationToken cancellationToken)
    {
        var action = ReminderActions.All.Contains(row.Action) ? row.Action : ReminderActions.Notify;

        if (!await store.TryClaimEventFireAsync(row.Id, now, now - EventCooldown, cancellationToken))
        {
            logger.LogInformation("提醒「{Title}」在 {Minutes} 分钟冷却期内，这次{Source}跳过。",
                row.Title, EventCooldown.TotalMinutes, source);
            return null;
        }

        return await ExecuteOnceAsync(row, action, now, late: false, source, cancellationToken);
    }

    /// <summary>
    /// 「执行一次」的共同部分：按动作下发 → 写触发记录 → 记日志 → 发 Webhook。
    /// <para>时间触发与事件触发只差在**怎么领到这次执行权**（CAS 推进排程 / 带冷却领取），
    /// 领到之后就完全一样，所以拆出来共用一份——否则两条路径的触发记录与外部通知很容易走偏。</para>
    /// </summary>
    /// <param name="row">要执行的提醒。</param>
    /// <param name="action">执行的动作（已归一化）。</param>
    /// <param name="now">本次执行的时刻（用于触发记录与指令签发时间）。</param>
    /// <param name="late">是否属于「迟到补发」：迟到只推进计划、不执行物理动作。</param>
    /// <param name="source">触发来源的文案（「按计划」/「设备上线」），用于日志、记录与群消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ReminderFireRow> ExecuteOnceAsync(ReminderRow row, string action, DateTimeOffset now,
        bool late, string source, CancellationToken cancellationToken)
    {
        var affected = 0;
        var skipped = 0;
        var detail = string.Empty;
        var failed = false;

        try
        {
            if (late)
            {
                detail = $"迟到约 {FormatLateness(row.NextFireAt, now)}（计划 {LocalText(row.NextFireAt)}），"
                         + "只推进计划、未执行物理动作";
            }
            else
            {
                (affected, skipped, detail) = action switch
                {
                    ReminderActions.PowerShutdown => await FireShutdownAsync(row, now, cancellationToken),
                    ReminderActions.PowerWake => await FireWakeAsync(row, cancellationToken),
                    _ => await FireNotifyAsync(row, now, cancellationToken),
                };
            }
        }
        catch (Exception ex)
        {
            failed = true;
            detail = "执行失败：" + ex.Message;
            logger.LogError(ex, "提醒「{Title}」执行失败（{Action}）。", row.Title, action);
        }

        var fire = new ReminderFireRow
        {
            Id = HubChecksum.NewId(),
            ReminderId = row.Id,
            UserId = row.UserId,
            Action = action,
            FiredAt = now,
            Affected = affected,
            Skipped = skipped,
            Detail = $"{detail}（触发方式：{source}）",
        };
        await store.AddReminderFireAsync(fire, cancellationToken);

        if (!failed && !late)
        {
            logger.LogInformation("提醒「{Title}」执行完成（{Action}，{Source}）：{Detail}。",
                row.Title, action, source, detail);
        }

        // 外部通知（#75）：定时提醒是「没人盯着也要按点发生」的事，出没出、发到几台、
        // 有没有迟到或失败，都该能流到群里，而不是只在管理端里翻历史。
        // 失败用不同的标题，避免被 WebhookService 的「同标题抑制窗口」把成功那条吞掉。
        await webhooks.NotifyAsync(WebhookEvents.ReminderFired,
            failed ? $"提醒执行失败：{row.Title}" : $"提醒已触发：{row.Title}",
            failed
                ? $"「{row.Title}」执行失败（{source}）：{detail}"
                : $"「{row.Title}」已执行（{action}，{source}），影响 {affected} 台、跳过 {skipped} 台"
                  + (late ? "（迟到补发）" : string.Empty) + "。",
            new Dictionary<string, object?>
            {
                ["reminderId"] = row.Id,
                ["reminderTitle"] = row.Title,
                ["action"] = action,
                ["affected"] = affected,
                ["skipped"] = skipped,
                ["late"] = late,
                ["failed"] = failed,
                ["source"] = source,
            }, cancellationToken);

        return fire;
    }

    /// <summary>
    /// 发通知（<see cref="ReminderActions.Notify"/>）：只推给<b>在线</b>设备——定时提醒讲时效，
    /// 离线教室等上线后再收到「开晨会」反而是打扰。
    /// </summary>
    private async Task<(int Affected, int Skipped, string Detail)> FireNotifyAsync(ReminderRow row,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (targets, skipped, detail) = await ResolveTargetsAsync(row, onlineOnly: true, cancellationToken);

        foreach (var device in targets)
        {
            await store.CreateCommandAsync(new RemoteCommandRow
            {
                Id = HubChecksum.NewId(),
                DeviceId = device.Id,
                Kind = RemoteCommandKinds.Notify,
                Payload = HubJson.Serialize(new NotifyRequestDto
                {
                    Title = string.IsNullOrWhiteSpace(row.Title) ? "提醒" : row.Title,
                    Message = row.Content,
                    Speak = row.Speak,
                }),
                Status = "pending",
                IssuedAt = now,
                IssuedBy = "reminder",
                ExpiresAt = now.Add(CommandTtl),
            }, cancellationToken);
        }

        if (targets.Count > 0)
        {
            // 让挂在长轮询上的教室端立刻取走：否则要等下一次心跳（默认 30 秒）才收到通知。
            sync.PublishWakeUp();
        }

        return (targets.Count, skipped, $"{detail}；推送 {targets.Count} 台，跳过离线 {skipped} 台");
    }

    /// <summary>
    /// 定时关机（<see cref="ReminderActions.PowerShutdown"/>）：下发 <c>power.shutdown</c>，**只发给在线设备**。
    /// <para>离线设备不排队：指令的 30 分钟 TTL 一过就作废，而排到队列里反而可能在第二天早上
    /// 那台机器刚被打开时把它关掉——这正是「定时关机」最容易被骂的形态。</para>
    /// </summary>
    private async Task<(int Affected, int Skipped, string Detail)> FireShutdownAsync(ReminderRow row,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (targets, skipped, detail) = await ResolveTargetsAsync(row, onlineOnly: true, cancellationToken);

        foreach (var device in targets)
        {
            await store.CreateCommandAsync(new RemoteCommandRow
            {
                Id = HubChecksum.NewId(),
                DeviceId = device.Id,
                Kind = RemoteCommandKinds.PowerShutdown,
                Payload = string.Empty,
                Status = "pending",
                IssuedAt = now,
                IssuedBy = "reminder",
                ExpiresAt = now.Add(PowerCommandTtl),
            }, cancellationToken);
        }

        if (targets.Count > 0)
        {
            sync.PublishWakeUp();
        }

        var note = skipped > 0 ? $"，离线 {skipped} 台未排队（避免它们下次开机时被关掉）" : string.Empty;
        return (targets.Count, skipped, $"{detail}；已下发关机指令 {targets.Count} 台{note}");
    }

    /// <summary>
    /// 定时开机（<see cref="ReminderActions.PowerWake"/>）：直接发 Wake-on-LAN 魔术包。
    /// <para><b>不按在线过滤</b>——要唤醒的机器按定义就是关着的；<b>不走指令队列</b>——机器关着时
    /// 插件根本没在运行。没上报过网卡地址的设备跳过并写明原因。</para>
    /// </summary>
    private async Task<(int Affected, int Skipped, string Detail)> FireWakeAsync(ReminderRow row,
        CancellationToken cancellationToken)
    {
        var (candidates, notAlive, detail) = await ResolveTargetsAsync(row, onlineOnly: false, cancellationToken);

        var sent = 0;
        var skipped = notAlive;
        var reasons = new List<string>();

        foreach (var device in candidates)
        {
            try
            {
                var error = await WakeOnLan.SendAsync(device.MacAddress, device.IpAddress, cancellationToken);
                if (error is null)
                {
                    sent++;
                    continue;
                }

                skipped++;
                reasons.Add($"{device.Name}：{error}");
            }
            catch (Exception ex)
            {
                skipped++;
                reasons.Add($"{device.Name}：{ex.Message}");
            }
        }

        var note = reasons.Count > 0
            ? "，跳过：" + string.Join("；", reasons.Take(5)) + (reasons.Count > 5 ? $" 等 {reasons.Count} 台" : string.Empty)
            : string.Empty;
        return (sent, skipped, $"{detail}；已发魔术包 {sent} 台{note}");
    }

    /// <summary>目标描述里的本地时间（管理员看的是墙上时钟，不是 UTC）。</summary>
    private static string LocalText(DateTimeOffset? utc) => utc is null
        ? "（无计划时间）"
        : TimeZoneInfo.ConvertTime(utc.Value, TimeZoneInfo.Local).ToString("MM-dd HH:mm");

    /// <summary>迟到时长的可读描述。</summary>
    private static string FormatLateness(DateTimeOffset? planned, DateTimeOffset now) => planned is null
        ? "未知"
        : $"{(int)Math.Max(0, (now - planned.Value).TotalMinutes)} 分钟";

    /// <summary>解析提醒的投递目标，返回「目标设备 / 跳过数量 / 目标描述」。</summary>
    /// <param name="row">提醒行（决定目标类型与目标 ID）。</param>
    /// <param name="onlineOnly">
    /// 是否只取在线设备。通知与关机要在线（时效 / 不可逆），唤醒**不能**按在线过滤。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<(List<DeviceRow> Targets, int Skipped, string Detail)> ResolveTargetsAsync(
        ReminderRow row, bool onlineOnly, CancellationToken cancellationToken)
    {
        var devices = await store.GetDevicesAsync(cancellationToken);
        var groups = await store.GetGroupsAsync(cancellationToken);

        List<DeviceRow> candidates;
        string detail;

        switch (row.TargetType)
        {
            case ReminderTargets.Device:
            {
                var device = devices.FirstOrDefault(d => string.Equals(d.Id, row.TargetId, StringComparison.Ordinal));
                candidates = device is null ? [] : [device];
                detail = device is null ? "目标教室已不存在" : $"教室「{device.Name}」";
                break;
            }

            case ReminderTargets.Group:
            {
                var family = CollectGroupFamily(groups, row.TargetId);
                candidates = devices
                    .Where(d => d.GroupId is not null && family.Contains(d.GroupId))
                    .ToList();
                var name = groups.FirstOrDefault(g => string.Equals(g.Id, row.TargetId, StringComparison.Ordinal))?.Name;
                detail = name is null ? "目标分组已删除" : $"分组「{name}」及其下级";
                break;
            }

            default:
                candidates = [.. devices];
                detail = "全部教室";
                break;
        }

        var alive = candidates.Where(d => !d.Revoked).ToList();
        if (!onlineOnly)
        {
            // 唤醒用：离线也要发魔术包——要叫醒的机器本来就是关着的。
            return (alive, 0, detail);
        }

        var online = alive.Where(sync.IsOnline).ToList();
        return (online, alive.Count - online.Count, detail);
    }

    /// <summary>分组及其全部下级分组的 ID（楼栋 → 楼层）。</summary>
    private static HashSet<string> CollectGroupFamily(List<GroupRow> groups, string rootId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var group in groups)
            {
                if (group.ParentId is not null && ids.Contains(group.ParentId) && ids.Add(group.Id))
                {
                    changed = true;
                }
            }
        }

        return ids;
    }

    /// <summary>把提醒行转成对外的 DTO（回填目标名称与星期集合）。</summary>
    public async Task<ReminderDto> ToDtoAsync(ReminderRow row, CancellationToken cancellationToken = default)
    {
        var targetName = row.TargetType switch
        {
            ReminderTargets.Group => (await store.GetGroupAsync(row.TargetId, cancellationToken))?.Name
                                     ?? "（分组已删除）",
            ReminderTargets.Device => (await store.GetDeviceAsync(row.TargetId, cancellationToken))?.Name
                                      ?? "（教室已删除）",
            _ => "全部在线教室",
        };

        var owner = await store.GetUserAsync(row.UserId, cancellationToken);

        return new ReminderDto
        {
            Id = row.Id,
            Title = row.Title,
            Content = row.Content,
            Channel = row.Channel,
            Action = row.Action,
            TriggerKind = row.TriggerKind,
            TargetType = row.TargetType,
            TargetId = row.TargetId,
            TargetName = targetName,
            Speak = row.Speak,
            Enabled = row.Enabled,
            RepeatKind = row.RepeatKind,
            RepeatInterval = row.RepeatInterval,
            RepeatWeekdays = row.WeekdayList(),
            RepeatDayOfMonth = row.RepeatDayOfMonth,
            FireTime = row.FireTime,
            StartDate = row.StartDate,
            EndDate = row.EndDate,
            NextFireAt = row.NextFireAt,
            LastFiredAt = row.LastFiredAt,
            FireCount = row.FireCount,
            OwnerId = row.UserId,
            OwnerName = owner?.DisplayName is { Length: > 0 } display ? display : owner?.Username ?? string.Empty,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };
    }
}
