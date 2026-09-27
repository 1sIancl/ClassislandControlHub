using System.Globalization;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Server.Services;

/// <summary>
/// CSES（The Course Schedule Exchange Schema）导入器。
/// <para>CSES 为 YAML 文本（.yml/.yaml，UTF-8），结构固定：
/// <c>version</c> / <c>subjects[name, simplified_name, teacher, room]</c> /
/// <c>schedules[name, enable_day, weeks, classes[subject, start_time, end_time]]</c>。</para>
/// <para>导入范围：科目、按作息去重后的时间表，以及按 <c>enable_day</c> 生成的逐格课表。
/// 集控的课表模型不区分周次，因此同一作息的单双周 schedule 会合并到同一天（先出现的先占位）。</para>
/// </summary>
public static class CsesImporter
{
    /// <summary>CSES 科目。</summary>
    public sealed class CsesSubject
    {
        public string Name { get; set; } = string.Empty;
        public string? SimplifiedName { get; set; }
        public string? Teacher { get; set; }
        public string? Room { get; set; }
    }

    /// <summary>CSES 课程（一节课的时间与科目名）。</summary>
    public sealed class CsesClass
    {
        public string Subject { get; set; } = string.Empty;
        public string StartTime { get; set; } = "00:00:00";
        public string EndTime { get; set; } = "00:00:00";
    }

    /// <summary>CSES 课表（某一天启用的一组课程）。</summary>
    public sealed class CsesSchedule
    {
        public string Name { get; set; } = string.Empty;
        public int EnableDay { get; set; } = 1;
        public string Weeks { get; set; } = "all";
        public List<CsesClass> Classes { get; set; } = [];
    }

    /// <summary>CSES 文档。</summary>
    public sealed class CsesDocument
    {
        public int Version { get; set; } = 1;
        public List<CsesSubject> Subjects { get; set; } = [];
        public List<CsesSchedule> Schedules { get; set; } = [];
    }

    /// <summary>
    /// 解析 CSES YAML 文本。
    /// <para>
    /// 兼容两种常见的序列缩进风格：YamlDotNet 默认的「顶格序列」（<c>subjects:</c> 后接同列 <c>- </c>），
    /// 以及把 <c>- </c> 再缩进两格的写法。两者在 ClassIsland 的导出文件里都可能出现。
    /// </para>
    /// </summary>
    public static CsesDocument Parse(string yaml)
    {
        const int SectionNone = 0;
        const int SectionSubjects = 1;
        const int SectionSchedules = 2;

        var doc = new CsesDocument();
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return doc;
        }

        var lines = yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var section = SectionNone;
        CsesSubject? subject = null;
        CsesSchedule? schedule = null;
        CsesClass? cls = null;
        // 是否位于某个 schedule 的 classes 子列表内，以及 classes: 键所在的缩进列。
        var inClasses = false;
        var classesKeyIndent = -1;

        foreach (var raw in lines)
        {
            var line = StripComment(raw);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            var content = line.Trim();
            var isItem = content[0] == '-' && (content.Length == 1 || content[1] == ' ');
            if (isItem)
            {
                content = content.Length > 1 ? content[1..].Trim() : string.Empty;
            }

            if (isItem)
            {
                if (section == SectionSubjects)
                {
                    subject = new CsesSubject();
                    doc.Subjects.Add(subject);
                    ApplySubjectField(subject, content);
                }
                else if (section == SectionSchedules)
                {
                    // classes 的列表项与 schedules 的列表项可能同属一层（顶格序列风格），
                    // 用 classes: 所在列区分：不浅于该列的列表项属于当前 schedule 的 classes。
                    if (inClasses && indent >= classesKeyIndent)
                    {
                        cls = new CsesClass();
                        schedule!.Classes.Add(cls);
                        ApplyClassField(cls, content);
                    }
                    else
                    {
                        schedule = new CsesSchedule();
                        doc.Schedules.Add(schedule);
                        cls = null;
                        inClasses = false;
                        ApplyScheduleField(schedule, content);
                    }
                }
                continue;
            }

            var (key, value) = SplitKeyValue(content);

            // 顶层键（无缩进且不是列表项）
            if (indent == 0)
            {
                switch (key)
                {
                    case "version" when int.TryParse(value, out var version):
                        doc.Version = version;
                        break;
                    case "subjects":
                        section = SectionSubjects;
                        break;
                    case "schedules":
                        section = SectionSchedules;
                        break;
                }
                continue;
            }

            if (section == SectionSubjects && subject is not null)
            {
                ApplySubjectField(subject, content);
            }
            else if (section == SectionSchedules && schedule is not null)
            {
                if (key == "classes")
                {
                    inClasses = true;
                    classesKeyIndent = indent;
                }
                else if (inClasses && cls is not null && indent > classesKeyIndent)
                {
                    ApplyClassField(cls, content);
                }
                else
                {
                    inClasses = false;
                    ApplyScheduleField(schedule, content);
                }
            }
        }

        return doc;
    }

    private static void ApplySubjectField(CsesSubject s, string content)
    {
        var (k, v) = SplitKeyValue(content);
        switch (k)
        {
            case "name": s.Name = v; break;
            case "simplified_name": s.SimplifiedName = v; break;
            case "teacher": s.Teacher = v; break;
            case "room": s.Room = v; break;
        }
    }

    private static void ApplyScheduleField(CsesSchedule s, string content)
    {
        var (k, v) = SplitKeyValue(content);
        switch (k)
        {
            case "name": s.Name = v; break;
            case "enable_day": s.EnableDay = ParseDay(v); break;
            case "weeks": s.Weeks = string.IsNullOrWhiteSpace(v) ? "all" : v.ToLowerInvariant(); break;
        }
    }

    private static void ApplyClassField(CsesClass c, string content)
    {
        var (k, v) = SplitKeyValue(content);
        switch (k)
        {
            case "subject": c.Subject = v; break;
            case "start_time": c.StartTime = NormalizeTime(v); break;
            case "end_time": c.EndTime = NormalizeTime(v); break;
        }
    }

    private static (string Key, string Value) SplitKeyValue(string content)
    {
        var idx = content.IndexOf(':');
        if (idx < 0)
        {
            return (content.Trim(), string.Empty);
        }

        var key = content[..idx].Trim();
        var value = content[(idx + 1)..].Trim().Trim('"', '\'');
        return (key, value);
    }

    private static string StripComment(string line)
    {
        // YAML 规定「#」前需有空白才算注释；引号内的「#」原样保留。
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line[..i];
            }
        }

        return line;
    }

    private static int ParseDay(string v)
    {
        if (int.TryParse(v, out var n) && n is >= 1 and <= 7)
        {
            return n; // CSES 1-7 = 周一到周日
        }

        return v.Trim().ToLowerInvariant() switch
        {
            "mon" or "monday" or "周一" or "星期一" => 1,
            "tue" or "tuesday" or "周二" or "星期二" => 2,
            "wed" or "wednesday" or "周三" or "星期三" => 3,
            "thu" or "thursday" or "周四" or "星期四" => 4,
            "fri" or "friday" or "周五" or "星期五" => 5,
            "sat" or "saturday" or "周六" or "星期六" => 6,
            "sun" or "sunday" or "周日" or "周天" or "星期日" or "星期天" => 7,
            _ => 0, // 无法识别时返回 0，由 ToContent 按出现顺序兜底分配星期
        };
    }

    private static string NormalizeTime(string v) => TimetableBuilder.NormalizeTime(v) ?? "00:00:00";

    /// <summary>
    /// 生成时间表名称：只对应一个 schedule 时沿用它的名字（如「星期一」）；
    /// 多天合并成一张时用「作息时间表」，只有存在多张同类时间表时才带序号。
    /// </summary>
    private static string ResolveLayoutName(List<string> scheduleNames, int layoutTotal, int created)
    {
        if (scheduleNames.Count == 1)
        {
            return scheduleNames[0];
        }

        return layoutTotal == 1 ? "作息时间表" : $"作息时间表 {created + 1}";
    }

    /// <summary>
    /// 把 CSES 转换为集控内容包：科目、按作息去重的时间表，以及按 <c>enable_day</c> 生成的逐格课表。
    /// </summary>
    public static ContentBundleDto ToContent(CsesDocument doc)
    {
        var content = new ContentBundleDto();
        var subjectByName = CreateSubjects(content, doc);

        // 时间表：从 schedules 的 classes 推导。多数学校的各天作息完全一致，
        // 因此按「时间序列」合并：只有时间序列不同的 schedule 才会另起一张时间表。
        var order = new List<string>();
        var merged = new Dictionary<string, LayoutGroup>(StringComparer.Ordinal);

        for (var i = 0; i < doc.Schedules.Count; i++)
        {
            var schedule = doc.Schedules[i];
            var classes = schedule.Classes
                .Where(c => c.StartTime != "00:00:00")
                .OrderBy(c => c.StartTime, StringComparer.Ordinal)
                .ToList();
            if (classes.Count == 0)
            {
                continue;
            }

            var key = string.Join("|", classes.Select(c => $"{c.StartTime}-{c.EndTime}"));
            if (!merged.TryGetValue(key, out var entry))
            {
                entry = new LayoutGroup(classes);
                merged[key] = entry;
                order.Add(key);
            }

            entry.Schedules.Add(schedule);
            // enable_day 缺失或无法识别时，按出现顺序依次落到周一到周日。
            entry.Days.Add(schedule.EnableDay is >= 1 and <= 7 ? schedule.EnableDay : (i % 7) + 1);
        }

        foreach (var key in order)
        {
            var entry = merged[key];
            var slots = entry.Classes
                .Select(c => new TimetableBuilder.Slot(c.StartTime, c.EndTime))
                .ToList();

            var layoutId = Guid.NewGuid().ToString("D");

            // 只对应一个 schedule 时沿用它的名字（如「星期一」）；多天合并成一张时用通用名。
            var scheduleNames = entry.Schedules
                .Select(s => s.Name?.Trim() ?? string.Empty)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            content.TimeLayouts.Add(new TimeLayoutDto
            {
                Id = layoutId,
                Name = ResolveLayoutName(scheduleNames, order.Count, content.TimeLayouts.Count),
                Items = TimetableBuilder.BuildLayoutItems(slots),
            });

            BuildClassPlans(content, subjectByName, layoutId, slots, entry);
        }

        // 附加设置：记录导入来源信息
        content.Settings.Values["cses.version"] = doc.Version.ToString(CultureInfo.InvariantCulture);
        content.Settings.Values["cses.subjectCount"] = content.Subjects.Count.ToString(CultureInfo.InvariantCulture);
        content.Settings.Values["cses.timeLayoutCount"] = content.TimeLayouts.Count.ToString(CultureInfo.InvariantCulture);
        content.Settings.Values["cses.classPlanCount"] = content.ClassPlans.Count.ToString(CultureInfo.InvariantCulture);

        return content;
    }

    /// <summary>先按声明导入科目，课表里出现但没声明的科目在排课时补齐。</summary>
    private static Dictionary<string, SubjectDto> CreateSubjects(ContentBundleDto content, CsesDocument doc)
    {
        var byName = new Dictionary<string, SubjectDto>(StringComparer.Ordinal);
        foreach (var s in doc.Subjects)
        {
            var name = s.Name?.Trim();
            if (string.IsNullOrEmpty(name) || byName.ContainsKey(name))
            {
                continue;
            }

            var dto = new SubjectDto
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = name,
                Initial = s.SimplifiedName?.Trim() ?? string.Empty,
                TeacherName = s.Teacher?.Trim() ?? string.Empty,
            };
            byName[name] = dto;
            content.Subjects.Add(dto);
        }

        return byName;
    }

    private static string? ResolveSubject(ContentBundleDto content, Dictionary<string, SubjectDto> byName,
        string name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (byName.TryGetValue(trimmed, out var existing))
        {
            return existing.Id;
        }

        var dto = new SubjectDto
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = trimmed,
            Initial = TimetableBuilder.GuessInitial(trimmed),
        };
        byName[trimmed] = dto;
        content.Subjects.Add(dto);
        return dto.Id;
    }

    /// <summary>
    /// 同一作息下每天生成一张课表。集控的课表模型不区分周次，
    /// 因此同一天的多个 schedule（如单双周）按顺序填入，先出现的先占位。
    /// </summary>
    private static void BuildClassPlans(ContentBundleDto content, Dictionary<string, SubjectDto> subjectByName,
        string layoutId, List<TimetableBuilder.Slot> slots, LayoutGroup group)
    {
        var plansByDay = new Dictionary<int, ClassPlanDto>();

        for (var i = 0; i < group.Schedules.Count; i++)
        {
            var day = group.Days[i];
            if (!plansByDay.TryGetValue(day, out var plan))
            {
                plan = new ClassPlanDto
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Name = $"{TimetableBuilder.DayLabel(day)}课表",
                    TimeLayoutId = layoutId,
                    IsEnabled = true,
                    DaysOfWeek = [day],
                };

                for (var index = 0; index < slots.Count; index++)
                {
                    plan.Slots.Add(new ClassPlanSlotDto { Index = index, StartTime = slots[index].Start });
                }

                plansByDay[day] = plan;
                content.ClassPlans.Add(plan);
            }

            foreach (var item in group.Schedules[i].Classes)
            {
                var index = slots.FindIndex(slot => slot.Start == item.StartTime);
                if (index < 0)
                {
                    continue;
                }

                var subjectId = ResolveSubject(content, subjectByName, item.Subject);
                if (subjectId is not null)
                {
                    plan.Slots[index].SubjectId = subjectId;
                }
            }
        }
    }

    /// <summary>共享同一张作息时间表的 schedule 集合。</summary>
    private sealed class LayoutGroup(List<CsesClass> classes)
    {
        public List<CsesClass> Classes { get; } = classes;

        public List<CsesSchedule> Schedules { get; } = [];

        public List<int> Days { get; } = [];
    }
}
