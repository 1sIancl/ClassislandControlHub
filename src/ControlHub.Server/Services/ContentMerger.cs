using ControlHub.Protocol.Dtos;

namespace ControlHub.Server.Services;

/// <summary>
/// 把导入得到的内容包合并进档案已有内容。
/// <para>
/// 合并规则：科目按名称去重（已存在但简称/教师为空时用导入值补全）；时间表按作息时间序列去重；
/// 课表按「时间表 + 星期」去重，命中则就地替换，否则新增。
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

            var existing = target.ClassPlans.FirstOrDefault(p =>
                p.TimeLayoutId == plan.TimeLayoutId && SameDays(p.DaysOfWeek, plan.DaysOfWeek));
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

        MergeSettings(target, incoming);
        return result;
    }

    private static void MergeSettings(ContentBundleDto target, ContentBundleDto incoming)
    {
        foreach (var (key, value) in incoming.Settings.Values)
        {
            target.Settings.Values[key] = value;
        }
    }

    private static bool SameDays(List<int> left, List<int> right) =>
        left.Count == right.Count && left.OrderBy(d => d).SequenceEqual(right.OrderBy(d => d));

    private static string LayoutKey(TimeLayoutDto layout) =>
        string.Join("|", layout.Items.Select(i => $"{i.StartTime}-{i.EndTime}"));
}
