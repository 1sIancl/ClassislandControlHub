using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Services;

namespace ControlHub.Tests;

/// <summary>
/// 定时提醒的排程推算（#20 定时开关机复用的那一套）。
/// <para>这里只测**纯函数**：时间推算与「迟到要不要执行」的判定。真正的触发要用端到端
/// （<c>_shot/verify-scheduled-power.js</c>）验，涉及调度器、指令下发与网卡广播。</para>
/// </summary>
public sealed class ReminderScheduleTests
{
    /// <summary>把「本地墙上时间」换算成 UTC 时刻——排程按服务器本地时区解释 fire_time。</summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, 0), TimeZoneInfo.Local));

    private static ReminderRow Daily(string fireTime, string startDate) => new()
    {
        RepeatKind = ReminderRepeats.Daily,
        FireTime = fireTime,
        StartDate = startDate,
        RepeatInterval = 1,
    };

    [Fact]
    public void 每天的提醒_今天已过就落在明天同一时刻()
    {
        var next = ReminderService.ComputeNextFire(Daily("08:00", "2026-01-01"), Local(2026, 10, 13, 9, 30));

        Assert.Equal(Local(2026, 10, 14, 8, 0), next);
    }

    [Fact]
    public void 每天的提醒_今天还没到就落在今天()
    {
        var next = ReminderService.ComputeNextFire(Daily("08:00", "2026-01-01"), Local(2026, 10, 13, 7, 0));

        Assert.Equal(Local(2026, 10, 13, 8, 0), next);
    }

    [Fact]
    public void 仅一次的提醒_日期已过就没有下一次()
    {
        var row = new ReminderRow
        {
            RepeatKind = ReminderRepeats.Once,
            FireTime = "08:00",
            StartDate = "2026-10-12",
        };

        Assert.Null(ReminderService.ComputeNextFire(row, Local(2026, 10, 13, 9, 0)));
    }

    [Fact]
    public void 每周的提醒_挑下一个命中的星期几()
    {
        // 2026-10-13 是周二；只勾了周三 → 下一次应落在 10-14 08:00。
        var row = new ReminderRow
        {
            RepeatKind = ReminderRepeats.Weekly,
            FireTime = "08:00",
            StartDate = "2026-10-05",
            RepeatWeekdays = "3",
            RepeatInterval = 1,
        };

        Assert.Equal(Local(2026, 10, 14, 8, 0), ReminderService.ComputeNextFire(row, Local(2026, 10, 13, 9, 0)));
    }

    [Fact]
    public void 过了结束日期就不再触发()
    {
        var row = Daily("08:00", "2026-01-01");
        row.EndDate = "2026-10-13";

        Assert.Null(ReminderService.ComputeNextFire(row, Local(2026, 10, 13, 9, 0)));
    }

    [Fact]
    public void 物理动作迟到十分钟以上就只推进计划不执行()
    {
        var now = Local(2026, 10, 13, 9, 0);

        // 停机补发的场景：上周排的关机，重启后不该真的把教学楼关掉。
        Assert.True(ReminderService.ShouldSkipLateFire(ReminderActions.PowerShutdown, now.AddMinutes(-11), now));
        Assert.True(ReminderService.ShouldSkipLateFire(ReminderActions.PowerWake, now.AddHours(-5), now));

        // 刚好在宽限窗口内（调度误差、短暂抖动）仍要执行。
        Assert.False(ReminderService.ShouldSkipLateFire(ReminderActions.PowerShutdown, now.AddMinutes(-9), now));

        // 通知不跳：迟到的通知只是晚了，静默丢掉才是问题（而且这就是原来的行为）。
        Assert.False(ReminderService.ShouldSkipLateFire(ReminderActions.Notify, now.AddHours(-5), now));

        // 没有计划时间（手动「立即执行」）不按迟到处理。
        Assert.False(ReminderService.ShouldSkipLateFire(ReminderActions.PowerShutdown, null, now));
    }

    [Fact]
    public void 动作取值校验与默认值()
    {
        // 空 = 老客户端不带这个字段，语义必须还是「发通知」。
        Assert.True(ReminderActions.IsValid(null));
        Assert.True(ReminderActions.IsValid(string.Empty));
        Assert.True(ReminderActions.IsValid(ReminderActions.Notify));
        Assert.True(ReminderActions.IsValid(ReminderActions.PowerShutdown));
        Assert.True(ReminderActions.IsValid(ReminderActions.PowerWake));
        Assert.False(ReminderActions.IsValid("reboot"));

        Assert.True(ReminderActions.IsPower(ReminderActions.PowerWake));
        Assert.True(ReminderActions.IsPower(ReminderActions.PowerShutdown));
        Assert.False(ReminderActions.IsPower(ReminderActions.Notify));
    }
}
