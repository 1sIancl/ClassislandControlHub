using IslandHub.Protocol.Dtos;

namespace IslandHub.Server.Services;

/// <summary>
/// 把导入得到的内容包合并进档案已有内容。
/// <para>
/// 合并规则：科目按名称去重（已存在但简称/教师为空时用导入值补全）；时间表按作息时间序列去重；
/// 课表按「时间表 + 星期 + 周次」去重（**周次不能省**：单双周本来就是同一天的两张课表），
/// 命中则就地替换，否则新增；多周轮换设置取更长的周期、起点与偏移以导入的包为准。
/// </para>
/// </summary>
public static class ContentMerger
{
    /// <summary>合并结果统计。</summary>
    public sealed class MergeResult
    {
        /// <summary>新增科目数。</summary>
        public int AddedSubjects { get; set; }

        /// <summary>被补全了简称或教师的科目数。</summary>
        public int EnrichedSubjects { get; set; }

        /// <summary>新增时间表数。</summary>
        public int AddedTimeLayouts { get; set; }

        /// <summary>新增课表数。</summary>
        public int AddedClassPlans { get; set; }

        /// <summary>被覆盖更新的课表数。</summary>
        public int UpdatedClassPlans { get; set; }
    }

    /// <summary>合并内容包。调用方负责持久化。</summary>
    public static MergeResult Merge(ContentBundleDto target, ContentBundleDto incoming, bool includeClassPlans)
    {
        var result = new MergeResult();

        var subjectByName = new Dictionary<string, SubjectDto>(StringComparer.Ordinal);
        foreach (var subject in target.Subjects)
        {
            subjectByName.TryAdd(subject.Name, subject);
        }

        // 导入内容里的科目 ID 可能与档案里已存在的同科科目不同，逐个记录最终生效的 ID。
        var subjectIdMap = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var subject in incoming.Subjects)
        {
            if (subjectByName.TryGetValue(subject.Name, out var existing))
            {
                var enriched = false;
                if (string.IsNullOrWhiteSpace(existing.Initial) && !string.IsNullOrWhiteSpace(subject.Initial))
                {
                    existing.Initial = subject.Initial;
                    enriched = true;
                }

                if (string.IsNullOrWhiteSpace(existing.TeacherName) && !string.IsNullOrWhiteSpace(subject.TeacherName))
                {
                    existing.TeacherName = subject.TeacherName;
                    enriched = true;
                }

                if (enriched)
                {
                    result.EnrichedSubjects++;
                }

                subjectIdMap[subject.Id] = existing.Id;
                continue;
            }

            target.Subjects.Add(subject);
            subjectByName[subject.Name] = subject;
            subjectIdMap[subject.Id] = subject.Id;
            result.AddedSubjects++;
        }

        var layoutByKey = new Dictionary<string, TimeLayoutDto>(StringComparer.Ordinal);
        foreach (var layout in target.TimeLayouts)
        {
            layoutByKey.TryAdd(LayoutKey(layout), layout);
        }

        var layoutIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layout in incoming.TimeLayouts)
        {
            var key = LayoutKey(layout);
            if (layoutByKey.TryGetValue(key, out var existing))
            {
                layoutIdMap[layout.Id] = existing.Id;
                continue;
            }

            target.TimeLayouts.Add(layout);
            layoutByKey[key] = layout;
            layoutIdMap[layout.Id] = layout.Id;
            result.AddedTimeLayouts++;
        }

        if (!includeClassPlans)
        {
            MergeSettings(target, incoming);
            return result;
        }

        foreach (var plan in incoming.ClassPlans)
        {
            if (layoutIdMap.TryGetValue(plan.TimeLayoutId, out var layoutId))
            {
                plan.TimeLayoutId = layoutId;
            }

            foreach (var slot in plan.Slots)
            {
                if (!string.IsNullOrEmpty(slot.SubjectId)
                    && subjectIdMap.TryGetValue(slot.SubjectId, out var subjectId))
                {
                    slot.SubjectId = subjectId;
                }
            }

            // 去重键必须带上**周次**：单双周的两张课表同属一天、时间表也相同，
            // 只按「时间表 + 星期」去重会把它们并成一张——轮换直接失效，而且看不出哪里错了。
            //
            // 还必须带上**原始规则**：日期型 / 循环型的课表虽然也带着 WeekDay（宿主里的残留值），
            // 但它的实际生效日期由 `EnableDates` 决定。真实包里就有「F5ri（日期型）」与
            // 「周五（按星期几）」同为 WeekDay=5 的情况——按旧键合并会把其中一张的内容整份覆盖掉。
            var existing = target.ClassPlans.FirstOrDefault(p =>
                p.TimeLayoutId == plan.TimeLayoutId && SameDays(p.DaysOfWeek, plan.DaysOfWeek)
                && p.WeekInterval == plan.WeekInterval && p.WeekOffset == plan.WeekOffset
                && SameTimeRule(p, plan));
            if (existing is null)
            {
                target.ClassPlans.Add(plan);
                result.AddedClassPlans++;
                continue;
            }

            existing.Name = plan.Name;
            existing.IsEnabled = plan.IsEnabled;
            existing.Slots = plan.Slots;
            result.UpdatedClassPlans++;
        }

        MergeRotation(target, incoming);
        MergeSettings(target, incoming);
        return result;
    }

    /// <summary>
    /// 合并「多周轮换」设置。
    /// <para>
    /// 周期取两者中更长的：导入进来的包带着多周课表，档案这边的周期不跟上，那些课表就永远不生效。
    /// 轮换起点与手动偏移以**导入的包**为准（那是某个真实学期在用的值），导入侧为空时保留原设置。
    /// </para>
    /// </summary>
    private static void MergeRotation(ContentBundleDto target, ContentBundleDto incoming)
    {
        // 周期同时看「导入包声明的周期」与「包里课表实际用的周期」：调用方漏设声明值时，
        // 也不能让带多周课表的包进来之后档案周期还是 1——保存时的规范化会把课表周次清掉。
        var incomingWeeks = Math.Max(
            incoming.Rotation?.Weeks ?? 1,
            incoming.ClassPlans
                .Where(p => p.WeekInterval >= 2)
                .Select(p => p.WeekInterval)
                .DefaultIfEmpty(0)
                .Max());

        target.Rotation ??= new RotationSettingsDto();
        if (incomingWeeks > target.Rotation.Weeks)
        {
            target.Rotation.Weeks = incomingWeeks;
        }

        if (incoming.Rotation is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(incoming.Rotation.SemesterStartDate))
        {
            target.Rotation.SemesterStartDate = incoming.Rotation.SemesterStartDate;
        }

        if (incoming.Rotation.Offset != 0)
        {
            target.Rotation.Offset = incoming.Rotation.Offset;
        }
    }

    private static void MergeSettings(ContentBundleDto target, ContentBundleDto incoming)
    {
        foreach (var (key, value) in incoming.Settings.Values)
        {
            target.Settings.Values[key] = value;
        }
    }

    /// <summary>
    /// 两张课表的「原始触发规则」是否算同一张。
    /// <para>
    /// 两条都为空（老档案，没有原始规则）→ 视为相同，保持原有的覆盖行为；
    /// 都非空 → 逐字比较；只有一条为空 → 视为不同（一个是按星期几、一个带原始规则，合并会丢语义）。
    /// </para>
    /// </summary>
    private static bool SameTimeRule(ClassPlanDto left, ClassPlanDto right)
    {
        var leftRaw = left.TimeRuleRaw;
        var rightRaw = right.TimeRuleRaw;

        if (string.IsNullOrEmpty(leftRaw) && string.IsNullOrEmpty(rightRaw))
        {
            return true;
        }

        return string.Equals(leftRaw, rightRaw, StringComparison.Ordinal);
    }

    private static bool SameDays(List<int> left, List<int> right) =>
        left.Count == right.Count && left.OrderBy(d => d).SequenceEqual(right.OrderBy(d => d));

    private static string LayoutKey(TimeLayoutDto layout) =>
        string.Join("|", layout.Items.Select(i => $"{i.StartTime}-{i.EndTime}"));
}
