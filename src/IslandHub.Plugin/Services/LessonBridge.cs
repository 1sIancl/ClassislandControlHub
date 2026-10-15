using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.IPC.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using IslandHub.Protocol.Dtos;
using Microsoft.Extensions.Logging;

namespace IslandHub.Plugin.Services;

/// <summary>
/// 与 ClassIsland 的课表服务对接：读「现在上什么、下节上什么、还有多久」，
/// 并执行临时换课与调休。
///
/// <para>为什么单独一个类而不是塞进 <see cref="ClassIslandAdapter"/>：那一个是「把 IslandHub 下发的
/// <i>档案内容</i>写进宿主」，属于<b>分发</b>；这里是「直接操作宿主<b>当前</b>的课表状态」
/// （ClassIsland 的临时层）。两者都会改同一份档案，混在一个类里很容易让人误以为是一件事。</para>
///
/// <para>所有对宿主模型的读写**必须在 UI 线程**（课表控件订阅了这些对象，后台线程改会抛
/// 「another thread owns it」，表现为「看起来成功、其实报错」），所以每个公开方法都先切线程。</para>
///
/// <para>依赖的是 <see cref="IPublicProfileService"/> / <see cref="IPublicLessonsService"/> 这两个
/// **公开**接口，而不是宿主的 <c>IProfileService</c> / <c>ILessonsService</c>：后者带有
/// <c>internal</c> 成员（如 <c>IProfileService.LoadProfileAsync</c>、<c>ILessonsService.CurrentOverlayStatus</c>），
/// 外部程序集**根本无法实现**，于是这段最容易踩宿主语义坑的代码就没法做无 GUI 验证
/// （tools/IslandHub.Verify 就是靠公开接口把语义固化成断言的）。这里用到的成员全都在这两个窄接口上，
/// 换过去没有任何行为差别。</para>
/// </summary>
public sealed class LessonBridge(
    IPublicProfileService profileService,
    IPublicLessonsService lessonsService,
    IExactTimeService exactTimeService,
    ILogger<LessonBridge> logger)
{
    /// <summary>读一次课表实时状态（心跳与「快捷换课」页面的数据来源）。</summary>
    public LessonStateDto ReadState() => UiThread.Run(ReadStateCore);

    /// <summary>读课表目录（今天各节、科目、课表），供管理端渲染下拉框。</summary>
    public LessonCatalogDto ReadCatalog() => UiThread.Run(ReadCatalogCore);

    /// <summary>临时换课 / 恢复原课表，返回给管理端看的一句话结果。</summary>
    public string Swap(LessonSwapRequestDto request) => UiThread.Run(() => SwapCore(request));

    /// <summary>调休 / 撤销调休，返回给管理端看的一句话结果。</summary>
    public string Adjust(LessonAdjustRequestDto request) => UiThread.Run(() => AdjustCore(request));

    // ────────────────────────────── 读 ──────────────────────────────

    private LessonStateDto ReadStateCore()
    {
        var now = exactTimeService.GetCurrentLocalDateTime();
        var plan = lessonsService.CurrentClassPlan;
        var profile = profileService.Profile;

        var state = new LessonStateDto
        {
            Date = now.ToString("yyyy-MM-dd"),
            ReportedAt = DateTimeOffset.Now,
            ClassPlanName = plan?.Name,
            State = MapState(lessonsService.CurrentState),
            CurrentSubject = HostSubjectName(lessonsService.CurrentSubject),
        };

        // 「下节课」用宿主算好的时间点：NextClassTimeLayoutItem 在**正在上课时指向当前这节课**
        // （LessonsService 的取值条件是 TimeType == 0 && EndTime >= now，与主界面右上角口径一致），
        // 当天课已上完时它是 TimeLayoutItem.Empty（StartTime / EndTime 都是 Zero），
        // 所以下面用 EndTime > Zero 判空。科目回到课表里按同一时间点取，
        // 拿到的才是这节课**实际排的**科目，而不是宿主的兜底值。
        var nextItem = lessonsService.NextClassTimeLayoutItem;
        if (nextItem is not null && nextItem.EndTime > TimeSpan.Zero)
        {
            state.NextStartAt = Format(nextItem.StartTime);
            state.NextEndAt = Format(nextItem.EndTime);
            state.NextSubject = ResolveSubjectName(plan, nextItem) ?? HostSubjectName(lessonsService.NextClassSubject);
        }

        state.LeftSeconds = lessonsService.CurrentState switch
        {
            TimeState.OnClass => (int)Math.Max(0, lessonsService.OnBreakingTimeLeftTime.TotalSeconds),
            TimeState.Breaking or TimeState.PrepareOnClass => (int)Math.Max(0, lessonsService.OnClassLeftTime.TotalSeconds),
            _ => 0,
        };

        state.IsAdjusted = IsAdjusted(now.Date);

        // 多周轮换：课表只写「我是周期里的第几周」，**哪一周算第 1 周**由宿主的轮换起点与手动偏移决定，
        // 那些设置可能被人在教室机上改过，所以「现在是第几周」只有教室端自己算得准——由它上报，
        // 管理端直接显示，不再拿服务端的档案去推算（推算结果与实际大屏可能不是同一周）。
        var (rotationWeek, rotationWeeks) = HostRotationSettings.ReadCurrentWeek(
            MaxRotationWeeks(profile), DateOnly.FromDateTime(now));
        state.RotationWeek = rotationWeek;
        state.RotationWeeks = rotationWeeks;

        return state;
    }

    /// <summary>当前档案实际用到的最大轮换周期（没有多周课表时返回 1）。</summary>
    private static int MaxRotationWeeks(Profile? profile)
    {
        if (profile is null)
        {
            return 1;
        }

        var max = 0;
        foreach (var plan in profile.ClassPlans.Values)
        {
            if (plan.TimeRule.WeekCountDiv > 0 && plan.TimeRule.WeekCountDivTotal >= 2)
            {
                max = Math.Max(max, plan.TimeRule.WeekCountDivTotal);
            }
        }

        return max >= 2 ? max : 1;
    }

    private LessonCatalogDto ReadCatalogCore()
    {
        var now = exactTimeService.GetCurrentLocalDateTime();
        var profile = profileService.Profile;
        var today = now.Date;

        // 当天实际生效的课表：优先用宿主已经判定好的（含调休与临时层），退而求其次按日期取。
        var plan = lessonsService.CurrentClassPlan ?? lessonsService.GetClassPlanByDate(today);

        var catalog = new LessonCatalogDto
        {
            Date = today.ToString("yyyy-MM-dd"),
            ClassPlanId = FindPlanId(plan)?.ToString() ?? string.Empty,
            ClassPlanName = plan?.Name ?? string.Empty,
            IsAdjustedDate = IsAdjusted(today),
        };

        if (plan is not null)
        {
            var currentStart = lessonsService.CurrentTimeLayoutItem?.StartTime;
            var nextStart = lessonsService.NextClassTimeLayoutItem?.StartTime;

            for (var i = 0; i < plan.Classes.Count; i++)
            {
                var info = plan.Classes[i];
                var slot = CurrentLayoutItem(info);

                catalog.Slots.Add(new LessonSlotDto
                {
                    Index = i,
                    SubjectId = info.SubjectId == Guid.Empty ? null : info.SubjectId.ToString(),
                    SubjectName = SubjectName(info.SubjectId) ?? "未排课",
                    StartAt = slot is null ? string.Empty : Format(slot.StartTime),
                    EndAt = slot is null ? string.Empty : Format(slot.EndTime),
                    IsChanged = info.IsChangedClass,
                    IsCurrent = slot is not null && currentStart is not null && slot.StartTime == currentStart,
                    IsNext = slot is not null && nextStart is not null && slot.StartTime == nextStart,
                });
            }
        }

        foreach (var (id, subject) in profile.Subjects)
        {
            catalog.Subjects.Add(new LessonSubjectOptionDto
            {
                Id = id.ToString(),
                Name = Clean(subject.Name) ?? "未命名",
            });
        }

        foreach (var (id, item) in profile.ClassPlans)
        {
            catalog.ClassPlans.Add(new LessonPlanOptionDto
            {
                Id = id.ToString(),
                Name = Clean(item.Name) ?? "未命名课表",
                IsEnabled = item.IsEnabled,
                IsOverlay = item.IsOverlay,
            });
        }

        // 下拉框按名字排序：管理员找的是「周三的课表」这种名字，不是 GUID。
        catalog.Subjects.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        catalog.ClassPlans.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        return catalog;
    }

    // ────────────────────────────── 临时换课 ──────────────────────────────

    private string SwapCore(LessonSwapRequestDto request)
    {
        var profile = profileService.Profile;

        // 恢复：撤销该日期的调整（今天 = 清临时层，其它日期 = 删那天的预定）。
        // 为什么不能一律调 ClearTempClassPlan()：宿主那个方法**固定删「今天」那一条**
        // （ProfileService.ClearTempClassPlan 里用的是 GetCurrentLocalDateTime().Date），
        // 于是「把日期选成明天 → 点恢复原课表」会清掉**今天**的临时层：
        // 明天照旧、今天反而被改坏，而且界面上没有任何提示。
        if (request.Restore)
        {
            return RestoreCore(request.Date);
        }

        var date = ParseDate(request.Date) ?? exactTimeService.GetCurrentLocalDateTime().Date;
        var source = lessonsService.GetClassPlanByDate(date) ?? lessonsService.CurrentClassPlan;
        if (source is null)
        {
            return $"换课失败：{date:yyyy-MM-dd} 没有生效的课表。";
        }

        var sourceId = FindPlanId(source);
        if (sourceId is null)
        {
            return "换课失败：找不到当天课表在档案里的记录。";
        }

        Guid targetId;
        if (request.WriteToSourceClassPlan)
        {
            // 直接改原课表：不建临时层，改动会留在档案里（适合「排错了，就是要改」）。
            targetId = sourceId.Value;
        }
        else
        {
            // 走临时层：复用当天已有的临时层，没有才新建。
            // 复用很重要——连改两节课时不该每改一次就多出一张临时课表。
            var scheduled = profile.OrderedSchedules.TryGetValue(date, out var os) ? os.ClassPlanId : (Guid?)null;
            if (scheduled is { } existing
                && profile.ClassPlans.TryGetValue(existing, out var scheduledPlan)
                && scheduledPlan.IsOverlay)
            {
                targetId = existing;
            }
            else
            {
                // CreateTempClassPlan 在「那天已经有临时层」时返回 null（宿主刻意的语义）。
                var created = profileService.CreateTempClassPlan(sourceId.Value, null, date);
                if (created is { } newPlanId)
                {
                    targetId = newPlanId;
                }
                else if (profile.OrderedSchedules.TryGetValue(date, out var reused))
                {
                    // 复用**那一天**已有的临时层（连改两节课时不该每改一次就多出一张临时课表）。
                    // ⚠️ 不能用 profile.OverlayClassPlanId 兜底：它是「今天」的指针，
                    // 给明天换课时会静默写进今天的临时层。
                    targetId = reused.ClassPlanId;
                }
                else
                {
                    // 退回原课表等于「直接改原课表」：会把一次性换课永久写进档案、
                    // 且无法用「恢复原课表」撤销。宁可明确失败。
                    return "换课失败：无法为教室端创建临时层课表。";
                }
            }
        }

        if (!profile.ClassPlans.TryGetValue(targetId, out var plan))
        {
            return "换课失败：目标课表不存在。";
        }

        if (request.Swap)
        {
            if (request.SwapWithIndex < 0
                || request.SwapWithIndex >= plan.Classes.Count
                || request.ClassIndex < 0
                || request.ClassIndex >= plan.Classes.Count)
            {
                return "换课失败：对调的两节课有一节不存在。";
            }

            var a = plan.Classes[request.ClassIndex];
            var b = plan.Classes[request.SwapWithIndex];
            (a.SubjectId, b.SubjectId) = (b.SubjectId, a.SubjectId);
            a.IsChangedClass = true;
            b.IsChangedClass = true;
        }
        else
        {
            if (request.ClassIndex < 0 || request.ClassIndex >= plan.Classes.Count)
            {
                return "换课失败：第 " + (request.ClassIndex + 1) + " 节不存在。";
            }

            var subjectId = ResolveSubjectId(request.SubjectId, request.SubjectName);
            if (subjectId is null)
            {
                return "换课失败：找不到要换成的科目（可能教室端还没有这个科目）。";
            }

            plan.Classes[request.ClassIndex].SubjectId = subjectId.Value;
            plan.Classes[request.ClassIndex].IsChangedClass = true;
        }

        profileService.SaveProfile();
        var name = plan.Name;
        logger.LogInformation("临时换课已写入课表 {Plan} 第 {Index} 节。", name, request.ClassIndex + 1);
        return request.Swap
            ? $"已把第 {request.ClassIndex + 1} 节与第 {request.SwapWithIndex + 1} 节对调（课表：{name}）。"
            : $"已把第 {request.ClassIndex + 1} 节换成「{SubjectName(plan.Classes[request.ClassIndex].SubjectId)}」（课表：{name}）。";
    }

    /// <summary>恢复原课表：今天是清临时层，其它日期是删掉那天的预定。</summary>
    /// <remarks>
    /// <para>为什么要分开：宿主只提供 <c>ClearTempClassPlan()</c>，而它**固定删今天那一条**
    /// `OrderedSchedules`。请求里带的日期一旦不是今天，就不能再走它。</para>
    /// </remarks>
    private string RestoreCore(string? requestedDate)
    {
        var profile = profileService.Profile;
        var today = exactTimeService.GetCurrentLocalDateTime().Date;
        var date = ParseDate(requestedDate) ?? today;

        if (date == today)
        {
            profileService.ClearTempClassPlan();
            profileService.SaveProfile();
            logger.LogInformation("已清除今天的临时层课表。");
            return "已恢复原课表（清除了今天的临时层）。";
        }

        if (!profile.OrderedSchedules.TryGetValue(date, out var entry))
        {
            return $"{date:yyyy-MM-dd} 本来就没有调整过，无需恢复。";
        }

        var planId = entry.ClassPlanId;
        profile.OrderedSchedules.Remove(date);
        RemoveOrphanOverlay(planId);
        profileService.SaveProfile();
        logger.LogInformation("已撤销 {Date} 的课表调整。", date.ToString("yyyy-MM-dd"));
        return $"已撤销 {date:yyyy-MM-dd} 的课表调整。";
    }

    /// <summary>把「已经没有任何日期引用」的临时层课表从档案里删掉。</summary>
    /// <remarks>
    /// <para>对齐宿主 <c>CleanExpiredTempClassPlan</c> 的清理口径（ClearTempClassPlan 会顺带调它）。
    /// 不清理的话，每撤销一次就留下一张「XX（临时层）」课表：档案里越积越多，
    /// 调休的下拉框里也会多出一堆没人认识的临时层。</para>
    /// </remarks>
    private void RemoveOrphanOverlay(Guid planId)
    {
        var profile = profileService.Profile;
        if (!profile.ClassPlans.TryGetValue(planId, out var plan) || !plan.IsOverlay)
        {
            return;
        }

        // 还有别的日期预定了它，就不能删。
        if (profile.OrderedSchedules.Values.Any(entry => entry.ClassPlanId == planId))
        {
            return;
        }

        profile.ClassPlans.Remove(planId);
        if (profile.OverlayClassPlanId == planId)
        {
            profile.OverlayClassPlanId = null;
        }

        logger.LogInformation("已清理不再被引用的临时层课表 {PlanId}。", planId);
    }

    // ────────────────────────────── 调休 ──────────────────────────────

    private string AdjustCore(LessonAdjustRequestDto request)
    {
        var profile = profileService.Profile;
        var date = ParseDate(request.Date);
        if (date is null)
        {
            return "调休失败：日期格式不正确。";
        }

        if (request.Restore)
        {
            profile.OrderedSchedules.Remove(date.Value);
            profileService.SaveProfile();
            logger.LogInformation("已撤销 {Date} 的调休。", date.Value.ToString("yyyy-MM-dd"));
            return $"{date.Value:yyyy-MM-dd} 的调休已撤销，那天回到按星期几的正常课表。";
        }

        var planId = ResolvePlanId(request.ClassPlanId, request.ClassPlanName);
        if (planId is null)
        {
            return "调休失败：找不到指定的课表。";
        }

        // 与 ClassIsland 手动调休完全一致：给这一天绑一张课表。
        // 次日不需要谁来清理——「没有预定过的日期」本来就按星期几的规则走。
        profile.OrderedSchedules[date.Value] = new OrderedSchedule { ClassPlanId = planId.Value };
        profileService.SaveProfile();

        var planName = profile.ClassPlans.TryGetValue(planId.Value, out var plan) ? plan.Name : "所选课表";
        logger.LogInformation("已把 {Date} 调休为课表 {Plan}。", date.Value.ToString("yyyy-MM-dd"), planName);
        return $"已把 {date.Value:yyyy-MM-dd} 调休为「{planName}」。";
    }

    // ────────────────────────────── 工具 ──────────────────────────────

    /// <summary>该日期是否被改过课表（有当天临时层或调休预定）。</summary>
    /// <remarks>
    /// <para>**只看该日期的 `OrderedSchedules` 预定，不看 `OverlayClassPlanId`。**
    /// 后者是「今天那张临时层」的快捷指针，而且由宿主的主计时器每 ~50ms 按当天重算一次
    /// （ClassIsland/Services/LessonsService.cs 的 LoadCurrentClassPlan：今天没有临时层就置 null）。
    /// 早先这里写成「OverlayClassPlanId 非空 **或** 今天有预定」，于是
    /// <b>只要今天有临时层，任何一个日期都会被判成「已调整」</b>——管理端给明天做调休时会看到
    /// 「本日课表已调整」的误报。</para>
    /// <para>而临时层与调休**都会**写进 `OrderedSchedules`（临时层由
    /// ProfileService.CreateTempClassPlan 末尾写入 `OrderedSchedules[date]`），所以只查这一处
    /// 既准确又足够。临时层还要再看一眼总开关 `IsOverlayClassPlanEnabled`：关掉时宿主的
    /// GetClassPlanByDate 会跳过这条预定、走正常规则，教室大屏上仍是普通课表，
    /// 此时也不该说「已调整」。</para>
    /// </remarks>
    private bool IsAdjusted(DateTime date)
    {
        var profile = profileService.Profile;
        if (!profile.OrderedSchedules.TryGetValue(date, out var entry))
        {
            return false;
        }

        // 调休绑的是普通课表，一定生效；临时层要看总开关。
        if (profile.ClassPlans.TryGetValue(entry.ClassPlanId, out var plan) && plan.IsOverlay)
        {
            return profile.IsOverlayClassPlanEnabled;
        }

        return true;
    }

    /// <summary>课表在档案里的 GUID（按引用相等查找）。</summary>
    private Guid? FindPlanId(ClassPlan? plan)
    {
        if (plan is null)
        {
            return null;
        }

        foreach (var (id, item) in profileService.Profile.ClassPlans)
        {
            if (ReferenceEquals(item, plan))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>科目名；未排课（Guid 为空）返回 null。</summary>
    private string? SubjectName(Guid subjectId)
    {
        if (subjectId == Guid.Empty)
        {
            return null;
        }

        return profileService.Profile.Subjects.TryGetValue(subjectId, out var subject)
            ? Clean(subject.Name) ?? "未命名"
            : null;
    }

    /// <summary>按时间点从课表里找这节课的科目名（宿主的 NextClassSubject 只是兜底值）。</summary>
    private string? ResolveSubjectName(ClassPlan? plan, TimeLayoutItem item)
    {
        if (plan is null)
        {
            return null;
        }

        foreach (var info in plan.Classes)
        {
            var slot = CurrentLayoutItem(info);
            if (slot is null)
            {
                continue;
            }

            if (slot.StartTime == item.StartTime && slot.EndTime == item.EndTime)
            {
                return SubjectName(info.SubjectId);
            }
        }

        return null;
    }

    /// <summary>取这一节对应的时间表项。<b>会吞异常</b>：课表引用了不存在的时间表时宿主内部会抛。</summary>
    private TimeLayoutItem? CurrentLayoutItem(ClassInfo info)
    {
        try
        {
            return info.CurrentTimeLayoutItem;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "读取课表时间点失败（时间表缺失或不完整）。");
            return null;
        }
    }

    private Guid? ResolveSubjectId(string? id, string? name)
    {
        if (Guid.TryParse(id, out var parsed) && profileService.Profile.Subjects.ContainsKey(parsed))
        {
            return parsed;
        }

        var wanted = (name ?? string.Empty).Trim();
        if (wanted.Length == 0)
        {
            return null;
        }

        foreach (var (subjectId, subject) in profileService.Profile.Subjects)
        {
            if (string.Equals(subject.Name?.Trim(), wanted, StringComparison.CurrentCulture))
            {
                return subjectId;
            }
        }

        return null;
    }

    private Guid? ResolvePlanId(string? id, string? name)
    {
        if (Guid.TryParse(id, out var parsed) && profileService.Profile.ClassPlans.ContainsKey(parsed))
        {
            return parsed;
        }

        var wanted = (name ?? string.Empty).Trim();
        if (wanted.Length == 0)
        {
            return null;
        }

        foreach (var (planId, plan) in profileService.Profile.ClassPlans)
        {
            if (string.Equals(plan.Name?.Trim(), wanted, StringComparison.CurrentCulture))
            {
                return planId;
            }
        }

        return null;
    }

    private static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTime.TryParse(text.Trim(), out var date) ? date.Date : null;
    }

    private static string MapState(TimeState state) => state switch
    {
        TimeState.OnClass => LessonLiveStates.OnClass,
        TimeState.PrepareOnClass => LessonLiveStates.Prepare,
        TimeState.Breaking => LessonLiveStates.Breaking,
        TimeState.AfterSchool => LessonLiveStates.AfterSchool,
        _ => LessonLiveStates.None,
    };

    private static string Format(TimeSpan time) => $"{time.Hours:00}:{time.Minutes:00}";

    /// <summary>
    /// 宿主上报的科目名；宿主的**兜底科目**（<see cref="Subject.Fallback"/>，Name 是 <c>"???"</c>）
    /// 与空白科目都当作「没有科目」返回 null。
    /// </summary>
    /// <remarks>
    /// <para>⚠️ 宿主的 <c>CurrentSubject</c> / <c>NextClassSubject</c> **不会返回 null**：取不到科目时
    /// 给的是静态的 <c>Subject.Fallback</c>（LessonsService 里到处是 <c>?? Subject.Fallback</c> 的赋值）。
    /// 早先这里直接取 <c>Name</c>，于是「教室端还没排课」会在管理界面上显示成
    /// 「下节：???」——看着像数据坏了，其实是宿主刻意给的占位符。</para>
    /// <para>课间的 Subject.Breaking（Name 是「课间休息」这类）与真实科目都照常返回。</para>
    /// </remarks>
    private static string? HostSubjectName(Subject? subject)
    {
        if (subject is null || ReferenceEquals(subject, Subject.Fallback))
        {
            return null;
        }

        return Clean(subject.Name);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
