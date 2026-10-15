using System.Globalization;
using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;

namespace IslandHub.Server.Services;

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
        NormalizeRotation(content, notes, autoFix);

        content.Settings ??= new ProfileSettingsDto();
        content.Settings.Values ??= [];
        return notes;
    }

    /// <summary>轮换周期上限（周）。与 ClassIsland 宿主的「最大轮换周期」一致。</summary>
    public const int MaxRotationWeeks = 9;

    /// <summary>手动偏移的允许范围（周）。</summary>
    public const int MaxRotationOffset = 9;

    /// <summary>
    /// 校验并规范化「多周轮换」：周期范围、轮换起点格式、手动偏移范围，
    /// 以及**课表周次与档案周期的一致性**。
    ///
    /// <para>
    /// 最后一条是这里最要紧的：档案说「3 周轮换」而某张课表写着「第 5 周」，或课表标了周次
    /// 但档案没启用轮换——两种都会让教室端某一天空堂（课表文件都在、配置也下发成功，
    /// 只是那张课表永远不满足触发条件），属于本项目最怕的**静默失效**。
    /// </para>
    /// </summary>
    private static void NormalizeRotation(ContentBundleDto content, List<string> notes, bool autoFix)
    {
        content.Rotation ??= new RotationSettingsDto();
        var rotation = content.Rotation;

        if (rotation.Weeks is < 1 or > MaxRotationWeeks)
        {
            if (!autoFix)
            {
                throw HubException.Validation($"轮换周期必须在 1~{MaxRotationWeeks} 周之间（收到 {rotation.Weeks}）。");
            }

            var fixedWeeks = Math.Clamp(rotation.Weeks, 1, MaxRotationWeeks);
            notes.Add($"轮换周期 {rotation.Weeks} 周超出 1~{MaxRotationWeeks} 的范围，已按 {fixedWeeks} 周处理。");
            rotation.Weeks = fixedWeeks;
        }

        if (rotation.Offset is < -MaxRotationOffset or > MaxRotationOffset)
        {
            if (!autoFix)
            {
                throw HubException.Validation($"轮换偏移必须在 -{MaxRotationOffset}~{MaxRotationOffset} 周之间（收到 {rotation.Offset}）。");
            }

            var fixedOffset = Math.Clamp(rotation.Offset, -MaxRotationOffset, MaxRotationOffset);
            notes.Add($"轮换偏移 {rotation.Offset} 周超出 ±{MaxRotationOffset} 周的范围，已按 {fixedOffset} 周处理。");
            rotation.Offset = fixedOffset;
        }

        var rawStart = rotation.SemesterStartDate?.Trim();
        if (string.IsNullOrEmpty(rawStart))
        {
            rotation.SemesterStartDate = null;
        }
        else if (DateOnly.TryParse(rawStart, CultureInfo.InvariantCulture, out var startDate))
        {
            rotation.SemesterStartDate = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else
        {
            if (!autoFix)
            {
                throw HubException.Validation($"轮换起点「{rawStart}」无法识别，请填写 yyyy-MM-dd（例如 2026-09-06）。");
            }

            rotation.SemesterStartDate = null;
            notes.Add($"轮换起点「{rawStart}」无法识别，已改为由教室端本地设置决定（请填 yyyy-MM-dd）。");
        }

        if (rotation.Weeks <= 1)
        {
            var cleared = 0;
            foreach (var plan in content.ClassPlans)
            {
                if (plan.WeekInterval == 0 && plan.WeekOffset == 0)
                {
                    continue;
                }

                plan.WeekInterval = 0;
                plan.WeekOffset = 0;
                cleared++;
            }

            if (cleared > 0)
            {
                notes.Add($"档案没有启用多周轮换，已清除 {cleared} 张课表的周次设置（它们改回每周生效）。");
            }

            return;
        }

        foreach (var plan in content.ClassPlans)
        {
            // WeekInterval = 0 表示「这张课表每周都生效」——轮换档案里也允许（例如只有周二单双周不同）。
            if (plan.WeekInterval == 0 && plan.WeekOffset == 0)
            {
                continue;
            }

            if (plan.WeekInterval != rotation.Weeks)
            {
                notes.Add($"课表「{plan.Name}」的轮换周期（{plan.WeekInterval} 周）与档案的 {rotation.Weeks} 周不一致，已按档案周期调整。");
                plan.WeekInterval = rotation.Weeks;
            }

            if (plan.WeekOffset < 0 || plan.WeekOffset >= rotation.Weeks)
            {
                var fixedPlanOffset = Math.Clamp(plan.WeekOffset, 0, rotation.Weeks - 1);
                notes.Add($"课表「{plan.Name}」的周次超出第 1~{rotation.Weeks} 周的范围，已改为第 {fixedPlanOffset + 1} 周。");
                plan.WeekOffset = fixedPlanOffset;
            }
        }
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
