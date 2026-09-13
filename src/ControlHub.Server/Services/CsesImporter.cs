using System.Globalization;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Server.Services;

/// <summary>
/// CSES（The Course Schedule Exchange Schema）导入器。
/// <para>CSES 为 YAML 文本（.yml/.yaml，UTF-8），结构固定：
/// <c>version</c> / <c>subjects[name, simplified_name, teacher, room]</c> /
/// <c>schedules[name, enable_day, weeks, classes[subject, start_time, end_time]]</c>。</para>
/// <para>按需求「仅导入时间表、科目及附加设置」：不导入逐格课表安排，
/// 只从 schedules 推导去重后的时间表，并导入科目。</para>
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

    /// <summary>解析 CSES YAML 文本。</summary>
    public static CsesDocument Parse(string yaml)
    {
        var doc = new CsesDocument();
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return doc;
        }

        var lines = yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // section: 0=none, 1=subjects, 2=schedules
        var section = 0;
        CsesSubject? subject = null;
        CsesSchedule? schedule = null;
        CsesClass? cls = null;
        // inClasses: 当前 schedule 是否处于 classes 子列表
        var inClasses = false;

        foreach (var raw in lines)
        {
            var line = StripComment(raw);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            var content = line.Trim();
            var isListItem = content.StartsWith("- ", StringComparison.Ordinal) || content == "-";

            if (isListItem)
            {
                content = content.Length > 2 ? content[2..].Trim() : string.Empty;
            }

            // 顶层键（无缩进）
            if (indent == 0 && !isListItem)
            {
                var (key, _) = SplitKeyValue(content);
                section = key switch
                {
                    "version" => 0,
                    "subjects" => 1,
                    "schedules" => 2,
                    _ => section,
                };
                if (key == "version")
                {
                    var (_, v) = SplitKeyValue(content);
                    if (int.TryParse(v, out var ver)) doc.Version = ver;
                }
                continue;
            }

            if (indent <= 2 && isListItem)
            {
                // 新列表项
                if (section == 1)
                {
                    subject = new CsesSubject();
                    doc.Subjects.Add(subject);
                    ApplySubjectField(subject, content);
                }
                else if (section == 2)
                {
                    schedule = new CsesSchedule();
                    doc.Schedules.Add(schedule);
                    cls = null;
                    inClasses = false;
                    ApplyScheduleField(schedule, content);
                }
                continue;
            }

            if (indent <= 2)
            {
                continue;
            }

            // 缩进属性
            var (fk, fv) = SplitKeyValue(content);

            if (section == 1 && subject is not null)
            {
                ApplySubjectField(subject, content);
            }
            else if (section == 2 && schedule is not null)
            {
                if (fk == "classes")
                {
                    inClasses = true;
                    continue;
                }

                if (isListItem && inClasses)
                {
                    cls = new CsesClass();
                    schedule.Classes.Add(cls);
                    ApplyClassField(cls, content);
                }
                else if (inClasses && cls is not null)
                {
                    ApplyClassField(cls, content);
                }
                else
                {
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
        // 去除行内注释（简单处理：不在引号内的 # ）
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"' || ch == '\'') inQuote = !inQuote;
            else if (ch == '#' && !inQuote) return line[..i];
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
            _ => 1,
        };
    }

    private static string NormalizeTime(string v)
    {
        // 兼容 "HH:MM" 与 "HH:MM:SS"
        var t = v.Trim();
        var parts = t.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m))
        {
            return $"{h:D2}:{m:D2}:00";
        }
        if (parts.Length == 3)
        {
            return t;
        }
        return "00:00:00";
    }

    /// <summary>按需求把 CSES 转换为集控内容包（仅时间表 + 科目 + 附加设置）。</summary>
    public static ContentBundleDto ToContent(CsesDocument doc)
    {
        var content = new ContentBundleDto();

        // 科目：按名称去重
        var subjectByName = new Dictionary<string, SubjectDto>(StringComparer.Ordinal);
        foreach (var s in doc.Subjects)
        {
            var name = s.Name?.Trim();
            if (string.IsNullOrEmpty(name) || subjectByName.ContainsKey(name))
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
            subjectByName[name] = dto;
            content.Subjects.Add(dto);
        }

        // 时间表：从 schedules 的 classes 推导，按时间序列去重
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sch in doc.Schedules)
        {
            var classes = sch.Classes
                .Where(c => !string.IsNullOrWhiteSpace(c.StartTime) && c.StartTime != "00:00:00")
                .OrderBy(c => c.StartTime, StringComparer.Ordinal)
                .ToList();
            if (classes.Count == 0)
            {
                continue;
            }

            var key = string.Join("|", classes.Select(c => $"{c.StartTime}-{c.EndTime}"));
            if (!seen.Add(key))
            {
                continue;
            }

            var items = new List<TimeLayoutItemDto>();
            for (var i = 0; i < classes.Count; i++)
            {
                items.Add(new TimeLayoutItemDto
                {
                    StartTime = classes[i].StartTime,
                    EndTime = classes[i].EndTime,
                    Kind = TimeItemKind.Class,
                });

                if (i < classes.Count - 1 && classes[i].EndTime != classes[i + 1].StartTime)
                {
                    items.Add(new TimeLayoutItemDto
                    {
                        StartTime = classes[i].EndTime,
                        EndTime = classes[i + 1].StartTime,
                        Kind = TimeItemKind.Break,
                    });
                }
            }

            content.TimeLayouts.Add(new TimeLayoutDto
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = string.IsNullOrWhiteSpace(sch.Name) ? $"CSES 时间表 {content.TimeLayouts.Count + 1}" : sch.Name,
                Items = items,
            });
        }

        // 附加设置：记录导入来源信息
        content.Settings.Values["cses.version"] = doc.Version.ToString(CultureInfo.InvariantCulture);
        content.Settings.Values["cses.subjectCount"] = content.Subjects.Count.ToString(CultureInfo.InvariantCulture);
        content.Settings.Values["cses.timeLayoutCount"] = content.TimeLayouts.Count.ToString(CultureInfo.InvariantCulture);

        return content;
    }
}
