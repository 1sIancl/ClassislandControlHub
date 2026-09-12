using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;

namespace ControlHub.Server.Services;

/// <summary>
/// 配置内容校验与规范化。
/// <para>
/// 管理员在 Web 端保存的内容会先经过这里：补齐缺失标识、按时间排序、对齐课表节次，
/// 从而保证下发到客户端的数据一定是自洽的，客户端侧可以「拿到即用」。
/// </para>
/// </summary>
public static class ContentNormalizer
{
    /// <summary>
    /// 规范化内容包（就地修改），并返回收集到的问题列表。
    /// 问题分为「已自动修复」与「阻断性错误」，后者会抛出 <see cref="HubException"/>。
    /// </summary>
    /// <param name="content">待规范化的内容包。</param>
    /// <param name="autoFix">是否自动修复可修复的问题。</param>
    public static List<string> Normalize(ContentBundleDto content, bool autoFix = true)
    {
        var notes = new List<string>();

        if (content is null)
        {
            throw HubException.Validation("内容为空。");
        }

        NormalizeSubjects(content, notes, autoFix);
        NormalizeTimeLayouts(content, notes, autoFix);
        NormalizeClassPlans(content, notes, autoFix);

        content.Settings ??= new ProfileSettingsDto();
        content.Settings.Values ??= [];
        return notes;
    }

    private static void NormalizeSubjects(ContentBundleDto content, List<string> notes, bool autoFix)
    {
        content.Subjects ??= [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<SubjectDto>();

        foreach (var subject in content.Subjects)
        {
            if (string.IsNullOrWhiteSpace(subject.Name))
            {
                notes.Add("跳过了一个未填写名称的科目。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(subject.Id))
            {
                if (!autoFix)
                {
                    throw HubException.Validation($"科目「{subject.Name}」缺少标识。");
                }

                subject.Id = HubChecksum.NewId();
                notes.Add($"科目「{subject.Name}」缺少标识，已自动分配。");
            }

            if (!seen.Add(subject.Id))
            {
                if (!autoFix)
                {
                    throw HubException.Validation($"科目标识重复：{subject.Id}。");
                }

                subject.Id = HubChecksum.NewId();
                seen.Add(subject.Id);
                notes.Add($"科目「{subject.Name}」标识重复，已重新分配。");
            }

            subject.Name = subject.Name.Trim();
            subject.Initial = string.IsNullOrWhiteSpace(subject.Initial)
                ? subject.Name[..1]
                : subject.Initial.Trim();
            subject.TeacherName = subject.TeacherName?.Trim() ?? string.Empty;
            cleaned.Add(subject);
        }

        content.Subjects = cleaned;
    }

    private static void NormalizeTimeLayouts(ContentBundleDto content, List<string> notes, bool autoFix)
    {
        content.TimeLayouts ??= [];
        var cleaned = new List<TimeLayoutDto>();

        foreach (var layout in content.TimeLayouts)
        {
            if (string.IsNullOrWhiteSpace(layout.Name))
            {
                notes.Add("跳过了一个未填写名称的时间表。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(layout.Id))
            {
                if (!autoFix)
                {
                    throw HubException.Validation($"时间表「{layout.Name}」缺少标识。");
                }

                layout.Id = HubChecksum.NewId();
                notes.Add($"时间表「{layout.Name}」缺少标识，已自动分配。");
            }

            layout.Items ??= [];
            var items = new List<TimeLayoutItemDto>();
            foreach (var item in layout.Items)
            {
                if (!HubTime.TryParse(item.StartTime, out var start))
                {
                    notes.Add($"时间表「{layout.Name}」中有一个开始时间无法识别（{item.StartTime}），已跳过。");
                    continue;
                }

                if (!TimeItemKind.IsValid(item.Kind))
                {
                    if (!autoFix)
                    {
                        throw HubException.Validation($"时间点类型无效：{item.Kind}。");
                    }

                    item.Kind = TimeItemKind.Class;
                    notes.Add($"时间表「{layout.Name}」中存在未知时间点类型，已按「上课」处理。");
                }

                // 分割线与行动类型在 ClassIsland 中结束时间等于开始时间，这里保持一致。
                var end = TimeItemKind.ToTimeType(item.Kind) is 2 or 3
                    ? start
                    : HubTime.TryParse(item.EndTime, out var parsedEnd) ? parsedEnd : start;

                if (end < start)
                {
                    (start, end) = (end, start);
                    notes.Add($"时间表「{layout.Name}」中 {HubTime.ToText(start)} 的结束时间早于开始时间，已自动交换。");
                }

                item.StartTime = HubTime.ToText(start);
                item.EndTime = HubTime.ToText(end);
                if (string.IsNullOrWhiteSpace(item.BreakName))
                {
                    item.BreakName = null;
                }

                items.Add(item);
            }

            layout.Items = [.. items.OrderBy(i => HubTime.Parse(i.StartTime))];

            if (layout.Items.Count == 0)
            {
                notes.Add($"时间表「{layout.Name}」没有任何有效时间点。");
            }

            cleaned.Add(layout);
        }

        content.TimeLayouts = cleaned;
    }

    private static void NormalizeClassPlans(ContentBundleDto content, List<string> notes, bool autoFix)
    {
        content.ClassPlans ??= [];
        var layoutMap = content.TimeLayouts.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
        var subjectIds = content.Subjects.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<ClassPlanDto>();

        foreach (var plan in content.ClassPlans)
        {
            if (string.IsNullOrWhiteSpace(plan.Name))
            {
                notes.Add("跳过了一个未填写名称的课表。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(plan.Id))
            {
                if (!autoFix)
                {
                    throw HubException.Validation($"课表「{plan.Name}」缺少标识。");
                }

                plan.Id = HubChecksum.NewId();
                notes.Add($"课表「{plan.Name}」缺少标识，已自动分配。");
            }

            if (!layoutMap.TryGetValue(plan.TimeLayoutId ?? string.Empty, out var layout))
            {
                if (content.TimeLayouts.Count == 0)
                {
                    notes.Add($"课表「{plan.Name}」没有可关联的时间表，已跳过。");
                    continue;
                }

                if (!autoFix)
                {
                    throw HubException.Validation($"课表「{plan.Name}」关联的时间表不存在。");
                }

                layout = content.TimeLayouts[0];
                plan.TimeLayoutId = layout.Id;
                notes.Add($"课表「{plan.Name}」关联的时间表不存在，已改为关联「{layout.Name}」。");
            }
            else
            {
                plan.TimeLayoutId = layout.Id;
            }

            // 以时间表中「上课」类型的时间点为基准，对齐课表节次。
            var classItems = layout.Items
                .Where(i => i.Kind == TimeItemKind.Class)
                .ToList();
            var slotMap = (plan.Slots ?? [])
                .GroupBy(s => s.Index)
                .ToDictionary(g => g.Key, g => g.First());

            var slots = new List<ClassPlanSlotDto>();
            for (var index = 0; index < classItems.Count; index++)
            {
                var timeItem = classItems[index];
                slotMap.TryGetValue(index, out var slot);
                slot ??= new ClassPlanSlotDto { Index = index };

                if (!string.IsNullOrWhiteSpace(slot.SubjectId) && !subjectIds.Contains(slot.SubjectId))
                {
                    notes.Add($"课表「{plan.Name}」第 {index + 1} 节引用了不存在的科目，已清空。");
                    slot.SubjectId = null;
                }

                slot.Index = index;
                slot.StartTime = timeItem.StartTime;
                slots.Add(slot);
            }

            var dropped = slotMap.Keys.Count(k => k < 0 || k >= classItems.Count);
            if (dropped > 0)
            {
                notes.Add($"课表「{plan.Name}」有 {dropped} 个课程节点超出时间表节数，已忽略。");
            }

            plan.Slots = slots;
            plan.DaysOfWeek ??= [];
            cleaned.Add(plan);
        }

        content.ClassPlans = cleaned;
    }

    /// <summary>深拷贝内容包，避免规范化时污染调用方持有的对象。</summary>
    public static ContentBundleDto Clone(ContentBundleDto content) =>
        HubJson.Deserialize<ContentBundleDto>(HubJson.Serialize(content)) ?? new ContentBundleDto();
}
