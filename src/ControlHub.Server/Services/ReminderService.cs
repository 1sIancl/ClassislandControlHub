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

    /// <summary>把所有到点的提醒推送到教室大屏，返回本轮触发的条数。</summary>
    public async Task<int> FireDueRemindersAsync(CancellationToken cancellationToken = default)
    {
        var now = Now;
        var due = await store.GetDueRemindersAsync(now, 100, cancellationToken);
        var fired = 0;

        foreach (var row in due)
        {
            try
            {
                await FireAsync(row, now, cancellationToken);
                fired++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "触发提醒失败：{Id}（{Title}）", row.Id, row.Title);
            }
        }

        return fired;
    }

    /// <summary>
    /// 触发一条提醒：下发通知指令 → 记录触发历史 → 推进到下次触发时间。
    /// <para>只推给<b>在线</b>设备：定时提醒是时效性的，离线教室等上线后再收到「开晨会」反而是打扰。</para>
    /// </summary>
    public async Task<ReminderFireRow> FireAsync(ReminderRow row, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var (targets, skipped, detail) = await ResolveTargetsAsync(row, cancellationToken);

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

        var next = ComputeNextFire(row, now);
        await store.RecordReminderFiredAsync(row.Id, now, next, cancellationToken);

        var fire = new ReminderFireRow
        {
            Id = HubChecksum.NewId(),
            ReminderId = row.Id,
            UserId = row.UserId,
            FiredAt = now,
            Affected = targets.Count,
            Skipped = skipped,
            Detail = detail,
        };
        await store.AddReminderFireAsync(fire, cancellationToken);

        logger.LogInformation("提醒「{Title}」已触发：推送 {Affected} 台，跳过离线 {Skipped} 台（{Detail}）。",
            row.Title, targets.Count, skipped, detail);

        if (next is null)
        {
            logger.LogInformation("提醒「{Title}」已走完重复计划，自动停用。", row.Title);
        }

        return fire;
    }

    /// <summary>解析提醒的投递目标，返回「在线目标设备 / 跳过数量 / 目标描述」。</summary>
    private async Task<(List<DeviceRow> Targets, int Skipped, string Detail)> ResolveTargetsAsync(
        ReminderRow row, CancellationToken cancellationToken)
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
