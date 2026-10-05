using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Http;

namespace ControlHub.Server.Data;

/// <summary>
/// 配置档案的冲突检测（#44）。
///
/// 检查六类问题，分「硬错误」与「需人工确认」两档：
///   - 硬错误（Blocking）：课表引用了不存在的科目 / 时间表、节次超出时间表范围、
///     同一时间有多个课表同时生效、时间表内部两节时间重叠。这些**必然**让课表显示不对。
///   - 提醒：**同一教师同一时间被排到多个档案**。不一定错——可能只是同名老师，
///     也可能是合班上课——所以不阻塞，只提示人去确认。
///
/// 教师判定按**姓名**（档案里没有教师 ID），跨档案同名即视为同一人；界面上要写清楚这一点。
/// </summary>
public sealed partial class HubStore
{
    /// <summary>单次最多返回的冲突条数。</summary>
    private const int MaxConflicts = 200;

    private static readonly string[] WeekdayNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    /// <summary>扫描全部档案，返回冲突报告。</summary>
    public async Task<ProfileConflictReportDto> DetectProfileConflictsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await GetProfilesAsync(cancellationToken);
        var report = new ProfileConflictReportDto { ProfileCount = rows.Count };

        var conflicts = new List<ProfileConflictDto>();

        // (教师名, 星期) → 该教师在这个星期几的所有占用时段
        var occupancy = new Dictionary<(string Teacher, int Day), List<Occupancy>>();

        foreach (var row in rows)
        {
            var content = HubJson.DeserializeOrDefault(row.Content, new ContentBundleDto());
            var subjects = new Dictionary<string, SubjectDto>(StringComparer.Ordinal);
            foreach (var subject in content.Subjects)
            {
                subjects[subject.Id] = subject;
            }

            var layouts = new Dictionary<string, TimeLayoutDto>(StringComparer.Ordinal);
            foreach (var layout in content.TimeLayouts)
            {
                layouts[layout.Id] = layout;
                // ① 时间表内部：两节时间重叠
                conflicts.AddRange(DetectLayoutOverlap(layout, row));
            }

            // ② 同一时间有多个课表同时生效（客户端只能挑一个，结果不可预期）
            conflicts.AddRange(DetectDuplicateActivePlans(content, row));

            foreach (var plan in content.ClassPlans)
            {
                if (!plan.IsEnabled)
                {
                    continue;
                }

                report.ClassPlanCount++;

                if (!layouts.TryGetValue(plan.TimeLayoutId, out var layout))
                {
                    conflicts.Add(new ProfileConflictDto
                    {
                        Kind = "missing-timelayout",
                        Blocking = true,
                        Message = $"课表「{plan.Name}」引用的时间表不存在（可能被删掉了），这节课表在教室里无法生效。",
                        ProfileId = row.Id,
                        ProfileName = row.Name,
                    });
                    continue;
                }

                var classItems = layout.Items.Where(i => i.Kind == TimeItemKind.Class).ToList();
                // DaysOfWeek 为空 = 不限（每天都生效），与客户端语义一致。
                // 注意：三元里不能用集合表达式（推断不出目标类型），显式写 List<int>。
                var days = plan.DaysOfWeek.Count > 0
                    ? plan.DaysOfWeek.Distinct().Where(d => d is >= 0 and <= 6).ToList()
                    : new List<int> { 0, 1, 2, 3, 4, 5, 6 };

                foreach (var slot in plan.Slots)
                {
                    if (!slot.IsEnabled || string.IsNullOrWhiteSpace(slot.SubjectId))
                    {
                        continue;
                    }

                    if (!subjects.TryGetValue(slot.SubjectId, out var subject))
                    {
                        conflicts.Add(new ProfileConflictDto
                        {
                            Kind = "missing-subject",
                            Blocking = true,
                            Message = $"课表「{plan.Name}」第 {slot.Index + 1} 节引用的科目已不存在，"
                                      + "那一节在教室里会显示为空。",
                            ProfileId = row.Id,
                            ProfileName = row.Name,
                        });
                        continue;
                    }

                    if (slot.Index < 0 || slot.Index >= classItems.Count)
                    {
                        conflicts.Add(new ProfileConflictDto
                        {
                            Kind = "slot-out-of-range",
                            Blocking = true,
                            Message = $"课表「{plan.Name}」第 {slot.Index + 1} 节超出了时间表「{layout.Name}」"
                                      + $"的节数（共 {classItems.Count} 节），这一节不会被显示。",
                            ProfileId = row.Id,
                            ProfileName = row.Name,
                        });
                        continue;
                    }

                    var teacher = subject.TeacherName?.Trim();
                    if (string.IsNullOrEmpty(teacher))
                    {
                        continue;
                    }

                    var item = classItems[slot.Index];
                    var where = $"「{plan.Name}」第 {slot.Index + 1} 节（{TrimSeconds(item.StartTime)}-{TrimSeconds(item.EndTime)}）";
                    foreach (var day in days)
                    {
                        if (!occupancy.TryGetValue((teacher, day), out var list))
                        {
                            list = [];
                            occupancy[(teacher, day)] = list;
                        }

                        list.Add(new Occupancy(row.Id, row.Name, item.StartTime, item.EndTime, where));
                    }
                }
            }
        }

        // ③ 同一教师、同一天，时段互相重叠且跨了档案
        foreach (var ((teacher, day), list) in occupancy)
        {
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];
                    if (a.ProfileId == b.ProfileId || !Overlaps(a.Start, a.End, b.Start, b.End))
                    {
                        continue;
                    }

                    conflicts.Add(new ProfileConflictDto
                    {
                        Kind = "teacher-overlap",
                        // 合班上课、同名老师都属正常，所以不阻塞，只提示确认。
                        Blocking = false,
                        Message = $"教师「{teacher}」{WeekdayNames[day]} {TrimSeconds(a.Start)}-{TrimSeconds(a.End)} "
                                  + $"同时出现在「{a.ProfileName}」{a.Where} 与「{b.ProfileName}」{b.Where}。"
                                  + "（按姓名判定：若只是同名老师，或本就是合班上课，可忽略。）",
                        ProfileId = a.ProfileId,
                        ProfileName = a.ProfileName,
                    });
                }
            }
        }

        var ordered = conflicts
            .OrderByDescending(c => c.Blocking)
            .ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ThenBy(c => c.ProfileName, StringComparer.Ordinal)
            .ThenBy(c => c.Message, StringComparer.Ordinal)
            .ToList();

        if (ordered.Count > MaxConflicts)
        {
            report.Truncated = true;
            ordered = ordered.Take(MaxConflicts).ToList();
        }

        report.Conflicts = ordered;
        return report;
    }

    /// <summary>一次占用记录（用于教师冲突的两两比对）。</summary>
    private sealed record Occupancy(string ProfileId, string ProfileName, string Start, string End, string Where);

    /// <summary>同一时间表内两节「上课」项的时间是否重叠。</summary>
    private static IEnumerable<ProfileConflictDto> DetectLayoutOverlap(TimeLayoutDto layout, ProfileRow row)
    {
        var classes = layout.Items.Where(i => i.Kind == TimeItemKind.Class).ToList();
        for (var i = 0; i < classes.Count; i++)
        {
            for (var j = i + 1; j < classes.Count; j++)
            {
                if (!Overlaps(classes[i].StartTime, classes[i].EndTime,
                        classes[j].StartTime, classes[j].EndTime))
                {
                    continue;
                }

                yield return new ProfileConflictDto
                {
                    Kind = "timelayout-overlap",
                    Blocking = true,
                    Message = $"时间表「{layout.Name}」第 {i + 1} 节（{TrimSeconds(classes[i].StartTime)}-"
                              + $"{TrimSeconds(classes[i].EndTime)}）与第 {j + 1} 节"
                              + $"（{TrimSeconds(classes[j].StartTime)}-{TrimSeconds(classes[j].EndTime)}）时间重叠，"
                              + "教室里这两节的显示会互相挤占。",
                    ProfileId = row.Id,
                    ProfileName = row.Name,
                };
            }
        }
    }

    /// <summary>同一时间内有多个启用的课表会同时生效（含课表群相同的场景）。</summary>
    private static IEnumerable<ProfileConflictDto> DetectDuplicateActivePlans(ContentBundleDto content, ProfileRow row)
    {
        var layouts = content.TimeLayouts.ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);

        // 只比较「默认启用」的课表：靠触发规则（周次 / 星期）区分的多套课表是正常用法。
        var always = content.ClassPlans
            .Where(p => p.IsEnabled && !p.IsOverlay
                        && p.DaysOfWeek.Count == 0 && p.WeekInterval == 0)
            .ToList();

        for (var i = 0; i < always.Count; i++)
        {
            for (var j = i + 1; j < always.Count; j++)
            {
                var a = always[i];
                var b = always[j];
                if (!string.Equals(a.GroupId ?? string.Empty, b.GroupId ?? string.Empty, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return new ProfileConflictDto
                {
                    Kind = "duplicate-classplan",
                    Blocking = true,
                    Message = $"课表「{a.Name}」与「{b.Name}」都会默认生效"
                              + $"（时间表：{Name(layouts, a.TimeLayoutId)} / {Name(layouts, b.TimeLayoutId)}），"
                              + "教室里同时存在两套课表，显示结果不确定。",
                    ProfileId = row.Id,
                    ProfileName = row.Name,
                };
            }
        }
    }

    private static string Name(Dictionary<string, string> layouts, string id) =>
        layouts.TryGetValue(id, out var name) ? name : "（已删除）";

    /// <summary>两个 <c>HH:mm:ss</c> 区间是否重叠（端点相接不算重叠）。</summary>
    private static bool Overlaps(string startA, string endA, string startB, string endB)
    {
        if (!TryParse(startA, out var a1) || !TryParse(endA, out var a2)
            || !TryParse(startB, out var b1) || !TryParse(endB, out var b2))
        {
            return false;
        }

        // 起止相同或相接（a2 == b1）不算重叠——课间本来就首尾相接。
        return a1 < b2 && b1 < a2;
    }

    private static bool TryParse(string? text, out TimeSpan value) =>
        TimeSpan.TryParse(string.IsNullOrWhiteSpace(text) ? "00:00:00" : text, out value);

    /// <summary>把 <c>08:00:00</c> 显示成 <c>08:00</c>。</summary>
    private static string TrimSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 5)
        {
            return text ?? string.Empty;
        }

        return text[..5];
    }
}
