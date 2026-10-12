using System.ComponentModel;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.IPC.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using ControlHub.Plugin.Services;
using ControlHub.Protocol.Dtos;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlHub.Verify;

/// <summary>
/// 断言结果的去处。
/// <para>自定义委托而不是 <c>Action&lt;string, bool, string?&gt;</c>：第三参（现场值）
/// 绝大多数断言不需要，写成可省略比在每个调用点补一个 <c>null</c> 清爽。</para>
/// </summary>
internal delegate void CheckSink(string name, bool condition, string? detail = null);

/// <summary>
/// 课表桥接（<see cref="LessonBridge"/>）的语义验证。
///
/// <para>为什么必须单独测这一层：它直接改宿主的课表档案，而那几个模型的真实语义相当绕——
/// 「临时层」与「调休」都写在同一个 <c>Profile.OrderedSchedules</c> 里，
/// <c>OverlayClassPlanId</c> 只代表**今天**那张临时层（宿主的主计时器每 ~50ms 按当天重算一次），
/// 宿主的 <c>ClearTempClassPlan()</c> 又**固定只清今天那一条**预定。
/// 这一层没有 HTTP 接口可验（A 端只能看到「指令已下发」），所以把语义固化成可重复执行的断言。</para>
///
/// <para>替身按 ClassIsland 源码语义实现，不是「随便返回个值」——否则测的只是自己的假设。
/// 关键语义的出处写在对应的替身成员上。</para>
/// </summary>
internal static class LessonBridgeChecks
{
    public static void Run(CheckSink check)
    {
        Console.WriteLine();
        Console.WriteLine("课表桥接（LessonBridge：教室端档案语义）");

        FallbackSubject(check);
        LeftSeconds(check);
        AdjustedFlag(check);
        RestoreByDate(check);
        SwapPlan(check);
        AdjustDate(check);
        StateMapping(check);
    }

    // ────────────────────────────── 上报：科目与剩余时间 ──────────────────────────────

    private static void FallbackSubject(CheckSink check)
    {
        var host = new Host();
        host.Lessons.NextClassTimeLayoutItem = Slot(10, 0, 10, 45);
        host.Lessons.CurrentSubject = Subject.Fallback;
        host.Lessons.NextClassSubject = Subject.Fallback;

        var state = host.Bridge.ReadState();
        check("宿主的兜底科目（Name = \"???\"）不会当成科目上报",
            state.CurrentSubject is null && state.NextSubject is null,
            $"current={state.CurrentSubject ?? "null"} next={state.NextSubject ?? "null"}");

        // 课间时宿主给的是 Subject.Breaking（Name 被改成时间点里的课间名），这是**真实信息**。
        host.Lessons.CurrentSubject = Subject.Breaking;
        check("课间科目（课间休息）照常上报",
            host.Bridge.ReadState().CurrentSubject == Subject.Breaking.Name,
            host.Bridge.ReadState().CurrentSubject ?? "null");
    }

    private static void LeftSeconds(CheckSink check)
    {
        var host = new Host();

        host.Lessons.CurrentState = TimeState.OnClass;
        host.Lessons.OnBreakingTimeLeftTime = TimeSpan.FromSeconds(185);
        check("正在上课时剩余秒数取「距下课」（OnBreakingTimeLeftTime）",
            host.Bridge.ReadState().LeftSeconds == 185, host.Bridge.ReadState().LeftSeconds.ToString());

        host.Lessons.CurrentState = TimeState.Breaking;
        host.Lessons.OnClassLeftTime = TimeSpan.FromSeconds(42);
        check("课间时剩余秒数取「距上课」（OnClassLeftTime）",
            host.Bridge.ReadState().LeftSeconds == 42, host.Bridge.ReadState().LeftSeconds.ToString());

        host.Lessons.CurrentState = TimeState.AfterSchool;
        check("放学后剩余秒数为 0", host.Bridge.ReadState().LeftSeconds == 0);

        // 当天课已上完时宿主给的是 TimeLayoutItem.Empty（StartTime/EndTime 都是 Zero），
        // 此时不能报出一个 00:00 的下节课。
        host.Lessons.NextClassTimeLayoutItem = TimeLayoutItem.Empty;
        var state = host.Bridge.ReadState();
        check("没有下节课时不填下节时间（Empty 的两个时间点都是 Zero）",
            state.NextStartAt is null && state.NextEndAt is null,
            $"{state.NextStartAt ?? "null"}~{state.NextEndAt ?? "null"}");
    }

    // ────────────────────────────── 「已调整」判定 ──────────────────────────────

    private static void AdjustedFlag(CheckSink check)
    {
        var todayOverlay = new Host();
        todayOverlay.AddOverlayFor(AddPlan(todayOverlay.Profile, "周一课表", [Guid.NewGuid()]), todayOverlay.Now);
        check("今天有临时层 → 标「已调整」", todayOverlay.Bridge.ReadState().IsAdjusted);

        // 这一条是回归点：旧实现看 OverlayClassPlanId（它是「今天」的指针，宿主每 tick 重算），
        // 只要今天有临时层，**任何**日期都会被判成已调整。
        var tomorrowOnly = new Host();
        tomorrowOnly.AddOverlayFor(
            AddPlan(tomorrowOnly.Profile, "周一课表", [Guid.NewGuid()]), tomorrowOnly.Tomorrow);
        check("只有「明天」被换课 → 今天不算已调整",
            !tomorrowOnly.Bridge.ReadState().IsAdjusted,
            $"IsAdjusted={tomorrowOnly.Bridge.ReadState().IsAdjusted}");

        var adjusted = new Host();
        var planId = AddPlan(adjusted.Profile, "周三课表", [Guid.NewGuid()]);
        adjusted.Profile.OrderedSchedules[adjusted.Now.Date] = new OrderedSchedule { ClassPlanId = planId };
        check("今天是调休（绑普通课表）→ 标「已调整」", adjusted.Bridge.ReadState().IsAdjusted);

        // 总开关关掉时宿主的 GetClassPlanByDate 会跳过临时层，大屏上仍是普通课表。
        var disabled = new Host();
        disabled.AddOverlayFor(AddPlan(disabled.Profile, "周一课表", [Guid.NewGuid()]), disabled.Now);
        disabled.Profile.IsOverlayClassPlanEnabled = false;
        check("临时层总开关关闭时不标「已调整」", !disabled.Bridge.ReadState().IsAdjusted);
    }

    // ────────────────────────────── 恢复原课表 ──────────────────────────────

    private static void RestoreByDate(CheckSink check)
    {
        // 今天与明天各有一张临时层。宿主的 ClearTempClassPlan() 固定只清「今天」，
        // 所以「把日期选成明天再点恢复」绝不能走它。
        var host = new Host();
        var sourceId = AddPlan(host.Profile, "周一课表", [Guid.NewGuid(), Guid.NewGuid()]);
        var todayOverlay = host.AddOverlayFor(sourceId, host.Now);
        var tomorrowOverlay = host.AddOverlayFor(sourceId, host.Tomorrow);
        var todaySubject = host.Profile.ClassPlans[todayOverlay].Classes[0].SubjectId;

        var message = host.Bridge.Swap(new LessonSwapRequestDto { Restore = true, Date = Text(host.Tomorrow) });

        check("恢复「明天」不会动「今天」的临时层（旧实现清掉的是今天）",
            host.Profile.OrderedSchedules.ContainsKey(host.Now.Date)
            && host.Profile.ClassPlans.ContainsKey(todayOverlay)
            && host.Profile.ClassPlans[todayOverlay].Classes[0].SubjectId == todaySubject,
            $"今天预定还在={host.Profile.OrderedSchedules.ContainsKey(host.Now.Date)}");
        check("恢复「明天」会删掉明天的预定",
            !host.Profile.OrderedSchedules.ContainsKey(host.Tomorrow));
        check("恢复后不再被引用的临时层课表被清理（对齐宿主 CleanExpiredTempClassPlan）",
            !host.Profile.ClassPlans.ContainsKey(tomorrowOverlay));
        check("恢复的提示语写清了是哪一天", message.Contains(Text(host.Tomorrow)), message);

        // 恢复今天：走宿主的 ClearTempClassPlan（它本来就是干这个的）。
        var today = new Host();
        today.AddOverlayFor(AddPlan(today.Profile, "周一课表", [Guid.NewGuid()]), today.Now);
        today.Bridge.Swap(new LessonSwapRequestDto { Restore = true });
        check("恢复「今天」（不填日期）清掉今天的临时层",
            !today.Profile.OrderedSchedules.ContainsKey(today.Now.Date)
            && today.Profile.OverlayClassPlanId is null,
            $"OverlayClassPlanId={today.Profile.OverlayClassPlanId?.ToString() ?? "null"}");

        // 一个从没调整过的日期：既不该改档案，也不该谎报成功。
        var untouched = new Host();
        AddPlan(untouched.Profile, "周一课表", [Guid.NewGuid()]);
        var saves = untouched.SaveCount;
        var message2 = untouched.Bridge.Swap(new LessonSwapRequestDto { Restore = true, Date = "2026-10-20" });
        check("恢复一个没调整过的日期：如实说明无需恢复，且不写档案",
            message2.Contains("无需恢复") && untouched.SaveCount == saves, message2);
    }

    // ────────────────────────────── 临时换课 ──────────────────────────────

    private static void SwapPlan(CheckSink check)
    {
        // 当天已有临时层：换课要复用它，不能每改一次就多出一张课表。
        var host = new Host();
        var chinese = Guid.NewGuid();
        var math = Guid.NewGuid();
        host.Profile.Subjects[math] = new Subject { Name = "数学" };
        var sourceId = AddPlan(host.Profile, "周一课表", [chinese, chinese]);
        host.SetWeeklyPlan(sourceId);
        var overlay = host.AddOverlayFor(sourceId, host.Now);
        var plansBefore = host.Profile.ClassPlans.Count;

        var message = host.Bridge.Swap(new LessonSwapRequestDto
        {
            Date = Text(host.Now), ClassIndex = 1, SubjectName = "数学",
        });

        check("换课写进当天的临时层并复用同一张（不新建第二张）",
            host.Profile.ClassPlans.Count == plansBefore
            && host.Profile.ClassPlans[overlay].Classes[1].SubjectId == math,
            $"课表数 {plansBefore}→{host.Profile.ClassPlans.Count}");
        check("换课结果提示写明第几节", message.Contains("第 2 节"), message);

        // 给「明天」换课：必须落到明天的临时层。旧实现会用 OverlayClassPlanId（今天的指针）兜底，
        // 于是静默写进了今天的临时层。
        var next = new Host();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        next.Profile.Subjects[b] = new Subject { Name = "数学" };
        var source2 = AddPlan(next.Profile, "周一课表", [a, b]);
        next.SetWeeklyPlan(source2);
        var todayOverlay = next.AddOverlayFor(source2, next.Now);
        next.Bridge.Swap(new LessonSwapRequestDto
        {
            Date = Text(next.Tomorrow), ClassIndex = 0, SubjectId = b.ToString(),
        });

        check("给明天换课不会写进今天的临时层",
            next.Profile.ClassPlans[todayOverlay].Classes[0].SubjectId == a
            && next.Profile.ClassPlans[todayOverlay].Classes[1].SubjectId == b,
            $"今天的临时层第1节={next.Profile.ClassPlans[todayOverlay].Classes[0].SubjectId}");
        check("给明天换课会新建明天的临时层并登记预定",
            next.Profile.OrderedSchedules.TryGetValue(next.Tomorrow, out var scheduled)
            && next.Profile.ClassPlans[scheduled.ClassPlanId].IsOverlay
            && next.Profile.ClassPlans[scheduled.ClassPlanId].Classes[0].SubjectId == b,
            $"明天预定={(next.Profile.OrderedSchedules.ContainsKey(next.Tomorrow) ? "有" : "无")}");

        // 对调两节。
        var swapHost = new Host();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var source3 = AddPlan(swapHost.Profile, "周一课表", [first, second]);
        swapHost.SetWeeklyPlan(source3);
        var overlay3 = swapHost.AddOverlayFor(source3, swapHost.Now);
        swapHost.Bridge.Swap(new LessonSwapRequestDto
        {
            Date = Text(swapHost.Now), ClassIndex = 0, Swap = true, SwapWithIndex = 1,
        });
        check("对调：两节科目互换",
            swapHost.Profile.ClassPlans[overlay3].Classes[0].SubjectId == second
            && swapHost.Profile.ClassPlans[overlay3].Classes[1].SubjectId == first);

        // 找不到科目时如实失败，绝不能回报成功（否则管理端显示「已生效」而教室毫无变化）。
        var missing = new Host();
        missing.AddOverlayFor(AddPlan(missing.Profile, "周一课表", [first]), missing.Now);
        var failure = missing.Bridge.Swap(new LessonSwapRequestDto
        {
            Date = Text(missing.Now), ClassIndex = 0, SubjectName = "不存在的科目",
        });
        check("换成不存在的科目时明确失败", failure.Contains("失败"), failure);
    }

    // ────────────────────────────── 调休 ──────────────────────────────

    private static void AdjustDate(CheckSink check)
    {
        var host = new Host();
        var planId = AddPlan(host.Profile, "周三课表", [Guid.NewGuid()]);
        var date = new DateTime(2026, 10, 17);

        var message = host.Bridge.Adjust(new LessonAdjustRequestDto { Date = Text(date), ClassPlanName = "周三课表" });
        check("调休：把那天绑到指定课表",
            host.Profile.OrderedSchedules.TryGetValue(date, out var scheduled) && scheduled.ClassPlanId == planId,
            message);
        check("调休提示写清日期与课表名",
            message.Contains("2026-10-17") && message.Contains("周三课表"), message);

        host.Bridge.Adjust(new LessonAdjustRequestDto { Date = Text(date), Restore = true });
        check("撤销调休：移掉那天的预定", !host.Profile.OrderedSchedules.ContainsKey(date));

        var bad = host.Bridge.Adjust(new LessonAdjustRequestDto { Date = "不是日期", ClassPlanName = "周三课表" });
        check("调休日期不合法时明确失败", bad.Contains("失败"), bad);
    }

    // ────────────────────────────── 状态映射 ──────────────────────────────

    private static void StateMapping(CheckSink check)
    {
        var host = new Host();
        (TimeState State, string Expected)[] cases =
        [
            (TimeState.OnClass, LessonLiveStates.OnClass),
            (TimeState.PrepareOnClass, LessonLiveStates.Prepare),
            (TimeState.Breaking, LessonLiveStates.Breaking),
            (TimeState.AfterSchool, LessonLiveStates.AfterSchool),
            (TimeState.None, LessonLiveStates.None),
        ];

        foreach (var (state, expected) in cases)
        {
            host.Lessons.CurrentState = state;
            var actual = host.Bridge.ReadState().State;
            check($"状态映射 {state} → {expected}", actual == expected, actual);
        }
    }

    // ────────────────────────────── 替身 ──────────────────────────────

    /// <summary>一个「教室端」：档案服务 + 课表服务 + 时钟，全部接在真实的 LessonBridge 上。</summary>
    private sealed class Host
    {
        public Host()
        {
            Clock = new FakeClock(new DateTime(2026, 10, 12, 9, 10, 0)); // 2026-10-12 是周一
            ProfileService = new FakeProfileService(Clock);
            Lessons = new FakeLessonsService(ProfileService);
            Bridge = new LessonBridge(ProfileService, Lessons, Clock, NullLogger<LessonBridge>.Instance);
        }

        public FakeProfileService ProfileService { get; }

        public FakeLessonsService Lessons { get; }

        public FakeClock Clock { get; }

        public LessonBridge Bridge { get; }

        public Profile Profile => ProfileService.Profile;

        public int SaveCount => ProfileService.SaveCount;

        /// <summary>教室端的「今天」。</summary>
        public DateTime Now => Clock.Now;

        public DateTime Tomorrow => Now.Date.AddDays(1);

        /// <summary>按宿主语义为某天建一张临时层，返回它的 ID。</summary>
        public Guid AddOverlayFor(Guid sourcePlanId, DateTime date) =>
            ProfileService.CreateTemp(sourcePlanId, date) ?? throw new InvalidOperationException("该日期已经有临时层了。");

        /// <summary>设定「按星期几」那张常规课表。</summary>
        public void SetWeeklyPlan(Guid planId) => Lessons.WeeklyPlanId = planId;
    }

    /// <summary>
    /// 档案服务替身（<see cref="IPublicProfileService"/>）。
    /// <para>两个方法的语义严格照 ClassIsland/Services/ProfileService.cs 实现，
    /// 因为「恢复原课表」的缺陷正是踩在这上面。</para>
    /// </summary>
    private sealed class FakeProfileService(FakeClock clock) : IPublicProfileService
    {
        public Profile Profile { get; set; } = new();

        public string CurrentProfilePath { get; set; } = string.Empty;

        public bool IsCurrentProfileTrusted => true;

        public int SaveCount { get; private set; }

        public void SaveProfile() => SaveCount++;

        public void SaveProfile(string filename) => SaveCount++;

        public Guid? CreateTempClassPlan(Guid id, Guid? timeLayoutId = null, DateTime? enableDateTime = null) =>
            CreateTemp(id, enableDateTime);

        public Guid? CreateTempClassPlan(
            Guid id, Guid? timeLayoutId, DateTime? enableDateTime, bool createTempTimeLayout) =>
            CreateTemp(id, enableDateTime);

        /// <summary>
        /// 对齐宿主 ProfileService.CreateTempClassPlan：那天已经有临时层就返回 null；
        /// 否则复制原课表、置 IsOverlay、写 <c>OrderedSchedules[date]</c>、
        /// 置 <c>OverlayClassPlanId</c> 与总开关。
        /// </summary>
        public Guid? CreateTemp(Guid sourcePlanId, DateTime? date)
        {
            var day = (date ?? clock.Now).Date;
            if (Profile.OrderedSchedules.TryGetValue(day, out var scheduled)
                && Profile.ClassPlans.TryGetValue(scheduled.ClassPlanId, out var existing)
                && existing.IsOverlay)
            {
                return null;
            }

            var source = Profile.ClassPlans[sourcePlanId];
            var overlay = new ClassPlan { Name = source.Name + "（临时层）", IsOverlay = true };
            foreach (var info in source.Classes)
            {
                overlay.Classes.Add(new ClassInfo { Index = info.Index, SubjectId = info.SubjectId });
            }

            var id = Guid.NewGuid();
            Profile.ClassPlans[id] = overlay;
            Profile.OrderedSchedules[day] = new OrderedSchedule { ClassPlanId = id };
            Profile.OverlayClassPlanId = id;
            Profile.IsOverlayClassPlanEnabled = true;
            SaveCount++;
            return id;
        }

        /// <summary>
        /// 对齐宿主 ProfileService.ClearTempClassPlan：**只删「今天」那一条预定**，
        /// 然后清理不再被任何预定引用的临时层课表（宿主接着调 CleanExpiredTempClassPlan）。
        /// </summary>
        public void ClearTempClassPlan()
        {
            if (Profile.OverlayClassPlanId is not { } current || !Profile.ClassPlans.ContainsKey(current))
            {
                return;
            }

            Profile.OrderedSchedules.Remove(clock.Now.Date);
            Profile.OverlayClassPlanId = null;
            if (Profile.ClassPlans.TryGetValue(current, out var plan) && plan.IsOverlay
                && !Profile.OrderedSchedules.Values.Any(entry => entry.ClassPlanId == current))
            {
                Profile.ClassPlans.Remove(current);
            }

            SaveCount++;
        }

        public void ConvertToStdClassPlan()
        {
        }

        public void ConvertToStdClassPlan(Guid id)
        {
        }

        public void SetupTempClassPlanGroup(Guid key, DateTime? expireTime = null)
        {
        }

        public void ClearTempClassPlanGroup()
        {
        }
    }

    /// <summary>
    /// 课表服务替身。
    /// <para><see cref="GetClassPlanByDate"/> 照宿主语义实现：**该日期的预定（含临时层）优先**，
    /// 没有预定才回到「按星期几」的常规课表。换课逻辑正是用它决定「改的是哪一张课表」，
    /// 所以这里不能图省事直接返回 CurrentClassPlan。</para>
    /// </summary>
    private sealed class FakeLessonsService(FakeProfileService profileService) : IPublicLessonsService
    {
        /// <summary>「按星期几」那张常规课表（测试里固定成这一张）。</summary>
        public Guid? WeeklyPlanId { get; set; }

        public bool IsTimerRunning => true;

        public ClassPlan? CurrentClassPlan { get; set; }

        public int CurrentSelectedIndex { get; set; }

        public Subject NextClassSubject { get; set; } = Subject.Fallback;

        public TimeLayoutItem NextBreakingTimeLayoutItem { get; set; } = TimeLayoutItem.Empty;

        public TimeLayoutItem NextClassTimeLayoutItem { get; set; } = TimeLayoutItem.Empty;

        public TimeSpan OnClassLeftTime { get; set; }

        public TimeSpan OnBreakingTimeLeftTime { get; set; }

        public TimeState CurrentState { get; set; } = TimeState.Breaking;

        public TimeLayoutItem CurrentTimeLayoutItem { get; set; } = TimeLayoutItem.Empty;

        public Subject? CurrentSubject { get; set; } = Subject.Fallback;

        public bool IsClassPlanEnabled { get; set; }

        public bool IsClassPlanLoaded { get; set; }

        public bool IsLessonConfirmed { get; set; }

        public ClassPlan? GetClassPlanByDate(DateTime date)
        {
            var profile = profileService.Profile;
            if (profile.OrderedSchedules.TryGetValue(date.Date, out var ordered)
                && profile.ClassPlans.TryGetValue(ordered.ClassPlanId, out var plan)
                && (!plan.IsOverlay || profile.IsOverlayClassPlanEnabled))
            {
                return plan;
            }

            return WeeklyPlanId is { } id && profile.ClassPlans.TryGetValue(id, out var weekly) ? weekly : null;
        }
    }

    /// <summary>时钟替身：把「今天」固定住，测试才不会随运行日期漂移。</summary>
    private sealed class FakeClock(DateTime now) : IExactTimeService
    {
        public DateTime Now { get; set; } = now;

        public string SyncStatusMessage { get; set; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public event PropertyChangingEventHandler? PropertyChanging
        {
            add { }
            remove { }
        }

        public void Sync()
        {
        }

        public DateTime GetCurrentLocalDateTime() => Now;
    }

    /// <summary>建一张课表（按顺序给出每节的科目）。</summary>
    private static Guid AddPlan(Profile profile, string name, IReadOnlyList<Guid> subjects)
    {
        var id = Guid.NewGuid();
        var plan = new ClassPlan { Name = name };
        for (var i = 0; i < subjects.Count; i++)
        {
            plan.Classes.Add(new ClassInfo { Index = i, SubjectId = subjects[i] });
        }

        profile.ClassPlans[id] = plan;
        return id;
    }

    private static TimeLayoutItem Slot(int startHour, int startMinute, int endHour, int endMinute) => new()
    {
        StartTime = new TimeSpan(startHour, startMinute, 0),
        EndTime = new TimeSpan(endHour, endMinute, 0),
    };

    private static string Text(DateTime date) => date.ToString("yyyy-MM-dd");
}
