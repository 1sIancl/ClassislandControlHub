using ControlHub.Protocol.Dtos;

namespace ControlHub.Server.Services;

/// <summary>
/// 由「作息时间段 + 每天课程」组装内容包。CSES 导入与 AI 导入共用，
/// 保证两条导入路径产出的时间表、科目与课表结构完全一致。
/// </summary>
public static class TimetableBuilder
{
    /// <summary>一节课的时间段。</summary>
    public readonly record struct Slot(string Start, string End);

    /// <summary>某一天的课程名，与 <see cref="Slot"/> 序列一一对应；null 或空串表示空堂。</summary>
    public readonly record struct DayCourses(int Day, IReadOnlyList<string?> Courses);

    /// <summary>科目定义。</summary>
    public readonly record struct SubjectSpec(string Name, string? Simplified, string? Teacher);

    /// <summary>由上课时间段生成时间表项：相邻两节之间若有空隙，补一个「课间」。</summary>
    public static List<TimeLayoutItemDto> BuildLayoutItems(IReadOnlyList<Slot> slots)
    {
        var items = new List<TimeLayoutItemDto>();
        for (var i = 0; i < slots.Count; i++)
        {
            items.Add(new TimeLayoutItemDto
            {
                StartTime = slots[i].Start,
                EndTime = slots[i].End,
                Kind = TimeItemKind.Class,
            });

            if (i < slots.Count - 1 && slots[i].End != slots[i + 1].Start)
            {
                items.Add(new TimeLayoutItemDto
                {
                    StartTime = slots[i].End,
                    EndTime = slots[i + 1].Start,
                    Kind = TimeItemKind.Break,
                });
            }
        }

        return items;
    }

    /// <summary>组装一份完整内容包：一张时间表 + 每天一张课表 + 用到的科目。</summary>
    public static ContentBundleDto BuildBundle(
        string layoutName,
        IReadOnlyList<Slot> slots,
        IReadOnlyList<DayCourses> days,
        IReadOnlyList<SubjectSpec> subjects)
    {
        var content = new ContentBundleDto();

        // 科目先落位，课表节点只引用 ID。显式声明的科目优先，课表里出现但没声明的补进来。
        var specs = new List<SubjectSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in subjects)
        {
            var name = spec.Name?.Trim();
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
            {
                specs.Add(spec with { Name = name });
            }
        }

        foreach (var day in days)
        {
            foreach (var course in day.Courses)
            {
                var name = course?.Trim();
                if (!string.IsNullOrEmpty(name) && seen.Add(name))
                {
                    specs.Add(new SubjectSpec(name, GuessInitial(name), null));
                }
            }
        }

        var subjectIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var id = Guid.NewGuid().ToString("D");
            subjectIds[spec.Name] = id;
            content.Subjects.Add(new SubjectDto
            {
                Id = id,
                Name = spec.Name,
                Initial = spec.Simplified?.Trim() ?? string.Empty,
                TeacherName = spec.Teacher?.Trim() ?? string.Empty,
            });
        }

        var layoutId = Guid.NewGuid().ToString("D");
        content.TimeLayouts.Add(new TimeLayoutDto
        {
            Id = layoutId,
            Name = string.IsNullOrWhiteSpace(layoutName) ? "作息时间表" : layoutName.Trim(),
            Items = BuildLayoutItems(slots),
        });

        foreach (var day in days)
        {
            var plan = new ClassPlanDto
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = $"{DayLabel(day.Day)}课表",
                TimeLayoutId = layoutId,
                IsEnabled = true,
                DaysOfWeek = [day.Day],
            };

            for (var i = 0; i < slots.Count; i++)
            {
                var course = i < day.Courses.Count ? day.Courses[i]?.Trim() : null;
                var subjectId = string.IsNullOrEmpty(course) || !subjectIds.TryGetValue(course, out var id)
                    ? null
                    : id;

                plan.Slots.Add(new ClassPlanSlotDto
                {
                    Index = i,
                    StartTime = slots[i].Start,
                    SubjectId = subjectId,
                });
            }

            content.ClassPlans.Add(plan);
        }

        return content;
    }

    /// <summary>取科目名的首字作为简称，用于大屏紧凑显示。</summary>
    public static string GuessInitial(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? string.Empty : trimmed[..1];
    }

    /// <summary>把 "H:MM" / "HH:MM" / "HH:MM:SS" 统一成 HH:mm:ss；无法识别时返回 null。</summary>
    public static string? NormalizeTime(string? value)
    {
        var parts = (value ?? string.Empty).Trim().Split(':');
        if (parts.Length is not (2 or 3)
            || !int.TryParse(parts[0], out var h) || h is < 0 or > 23
            || !int.TryParse(parts[1], out var m) || m is < 0 or > 59)
        {
            return null;
        }

        var s = 0;
        if (parts.Length == 3 && (!int.TryParse(parts[2], out s) || s is < 0 or > 59))
        {
            return null;
        }

        return $"{h:D2}:{m:D2}:{s:D2}";
    }

    /// <summary>在 HH:mm:ss 上增加若干分钟（跨天回绕）。</summary>
    public static string AddMinutes(string time, int minutes)
    {
        var parts = time.Split(':');
        var total = ((int.Parse(parts[0]) * 60) + int.Parse(parts[1]) + minutes) % 1440;
        if (total < 0)
        {
            total += 1440;
        }

        return $"{total / 60:D2}:{total % 60:D2}:00";
    }

    /// <summary>星期的中文简称（1=周一 … 7=周日）。</summary>
    public static string DayLabel(int day) => day switch
    {
        1 => "周一",
        2 => "周二",
        3 => "周三",
        4 => "周四",
        5 => "周五",
        6 => "周六",
        7 => "周日",
        _ => $"第 {day} 天",
    };
}
