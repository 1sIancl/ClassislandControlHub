using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Server.Services;

/// <summary>
/// `.cidata`（ClassIsland 官方「数据迁移」导出的包）解析与映射。
///
/// <para>
/// **它到底是什么**（按宿主源码 <c>ClassIsland\Views\DataTransferPage.axaml.cs</c> 的导出/导入实现）：
/// 一个 ZIP，内容就是宿主数据目录的整目录打包，条目为——
/// 根级 <c>Settings.json</c>、<c>Profiles/&lt;档案名&gt;.json</c>（**可能有多个**，每个是一份档案）、
/// <c>Config/…</c>（<c>Automations/</c>、<c>ComponentLayouts/</c>、<c>Themes/</c>、
/// <c>Management/</c>、<c>PluginsIndex/</c> 等）、<c>Plugins/…</c>。
/// </para>
///
/// <para>
/// ⚠️ **没有 <c>manifest.json</c>，包内也没有任何版本号或「勾选了哪些内容」的记录**
/// （版本只出现在导出文件名里，宿主导入时也不做版本校验）——所以这里只能按**结构**判断，
/// 不要去找版本字段。
/// </para>
///
/// <para>
/// **能映射到集控的部分只有四块**：时间表、课表、科目、课表群。其余（<c>OrderedSchedules</c>
/// 按日期的调休、<c>Config/Automations</c> 自动化、<c>Settings.json</c> 应用设置、<c>Plugins/</c> 插件）
/// 在集控的档案模型里**没有落点**，一律如实列进 <see cref="CidataProfileEntry.Warnings"/> 而不是悄悄丢掉。
/// </para>
///
/// <para>
/// **对齐口径（与插件侧 <c>ClassIslandAdapter</c> 互为逆映射，改一边必须改另一边）**：
/// 宿主的<b>一张课表只代表一天</b>（星期由 <c>TimeRule.WeekDay</c> 决定，0=周日..6=周六），
/// 而集控的 <see cref="ClassPlanDto"/> 也是「一张课表 = 一个星期几」（<c>DaysOfWeek</c> 取单个值），
/// 所以这里是 1:1，**不要**把一周的课表合并成一张。
/// 课表的逐格课程 <c>Classes</c> 是按「时间表里第 N 个『上课』时间点」对齐的（课间/分割线/行动不占格子）。
/// </para>
/// </summary>
public static class CidataImporter
{
    /// <summary>整包大小上限。`Plugins/` 会显著撑大文件，但 64MB 已足够一般学校。</summary>
    public const long MaxArchiveBytes = 64L * 1024 * 1024;

    /// <summary>包内单个档案文件（`Profiles/*.json`）的未压缩大小上限。</summary>
    public const long MaxProfileEntryBytes = 16L * 1024 * 1024;

    /// <summary>包内档案数量上限（防止有人把几百份档案打进来把预览撑爆）。</summary>
    public const int MaxProfileCount = 64;

    private const string ProfilesPrefix = "Profiles/";
    private const string ConfigPrefix = "Config/";
    private const string PluginsPrefix = "Plugins/";

    /// <summary>ClassIsland 的「默认课表群」常量 GUID（<c>ClassPlanGroup.DefaultGroupGuid</c>）。</summary>
    public const string HostDefaultGroupGuid = "acaf4ef0-e261-4262-b941-34ea93cb4369";

    private static readonly Guid HostDefaultGroupGuidValue = Guid.Parse(HostDefaultGroupGuid);

    /// <summary>解包并转换包内全部档案。</summary>
    /// <exception cref="InvalidDataException">文件不是 ZIP、或缺档案、或档案内容不合法时抛出（消息可直接展示给管理员）。</exception>
    public static CidataArchive Inspect(Stream stream)
    {
        if (stream is null)
        {
            throw new InvalidDataException("没有拿到文件内容。");
        }

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("这不是一个有效的 .cidata 文件（它应该是一个 ZIP 包）：" + ex.Message);
        }

        using (archive)
        {
            var result = new CidataArchive();

            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');

                if (string.Equals(name, "Settings.json", StringComparison.OrdinalIgnoreCase))
                {
                    result.HasSettings = true;
                    continue;
                }

                if (name.StartsWith(ConfigPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    result.ConfigEntryCount++;
                    continue;
                }

                if (name.StartsWith(PluginsPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    result.PluginEntryCount++;
                    continue;
                }

                if (!IsProfileEntry(name))
                {
                    continue;
                }

                if (result.Profiles.Count >= MaxProfileCount)
                {
                    throw new InvalidDataException(
                        $"包里的档案超过 {MaxProfileCount} 份，请先清理或分批导入。");
                }

                if (entry.Length > MaxProfileEntryBytes)
                {
                    throw new InvalidDataException(
                        $"档案文件 {entry.Name} 超过 {MaxProfileEntryBytes / 1024 / 1024}MB，不像是一份正常的课表档案。");
                }

                result.Profiles.Add(Convert(entry.Name, ReadText(entry)));
            }

            if (result.Profiles.Count == 0)
            {
                throw new InvalidDataException(
                    "包里没有找到任何档案（Profiles/*.json）——导出时请勾上「课表与档案」。");
            }

            return result;
        }
    }

    /// <summary>把一份档案 JSON 映射成集控内容包。</summary>
    public static CidataProfileEntry Convert(string fileName, string json)
    {
        ProfileJson? profile;
        try
        {
            profile = JsonSerializer.Deserialize<ProfileJson>(json, HubJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{fileName} 不是有效的 JSON：{ex.Message}");
        }

        if (profile is null)
        {
            throw new InvalidDataException($"{fileName} 是空的。");
        }

        var entry = new CidataProfileEntry
        {
            FileName = fileName,
            ProfileName = string.IsNullOrWhiteSpace(profile.Name) ? StripExtension(fileName) : profile.Name.Trim(),
        };

        var content = entry.Content;
        var warnings = entry.Warnings;

        // ── 科目 ──
        foreach (var (id, subject) in profile.Subjects ?? [])
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            content.Subjects.Add(new SubjectDto
            {
                Id = id.Trim(),
                Name = subject.Name?.Trim() ?? string.Empty,
                Initial = subject.Initial?.Trim() ?? string.Empty,
                TeacherName = subject.TeacherName?.Trim() ?? string.Empty,
                IsOutDoor = subject.IsOutDoor,
            });
        }

        // ── 时间表 ──
        var skippedLayoutNames = new List<string>();
        var skippedLayoutIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, layout) in profile.TimeLayouts ?? [])
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var dto = new TimeLayoutDto
            {
                Id = id.Trim(),
                Name = string.IsNullOrWhiteSpace(layout.Name) ? "未命名时间表" : layout.Name.Trim(),
            };

            var items = new List<TimeLayoutItemDto>();
            var unusable = false;

            foreach (var item in layout.Layouts ?? [])
            {
                var start = ParseTime(item.StartTime, item.StartSecond);
                var end = ParseTime(item.EndTime, item.EndSecond);

                // 时间点解析不出来时不「猜一个」：猜错会让整张时间表的节次全部错位，
                // 而且错位后即使点数对了也看不出来。宁可整张时间表跳过并在预览里说明。
                if (start is null || end is null)
                {
                    unusable = true;
                    break;
                }

                items.Add(new TimeLayoutItemDto
                {
                    StartTime = start,
                    EndTime = end,
                    Kind = TimeItemKind.FromTimeType(item.TimeType),
                    BreakName = string.IsNullOrWhiteSpace(item.BreakName) ? null : item.BreakName.Trim(),
                    IsHideDefault = item.IsHideDefault,
                    DefaultSubjectId = AsOptionalGuid(item.DefaultClassId),
                });
            }

            if (unusable)
            {
                skippedLayoutNames.Add(dto.Name);
                skippedLayoutIds.Add(dto.Id);
                continue;
            }

            dto.Items = items;
            content.TimeLayouts.Add(dto);
        }

        if (skippedLayoutNames.Count > 0)
        {
            warnings.Add($"时间表 {string.Join("、", skippedLayoutNames)} 里有时间点无法解析，整张已跳过"
                + "（用到它的课表也一并跳过）；请检查这份包是不是被改过。");
        }

        var layoutIds = content.TimeLayouts.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subjectIds = content.Subjects.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── 课表群 ──
        // 注意：集控的内容包里**没有**课表群清单，课表只带一个 GroupId 字符串。
        // 这里只把包内定义的群列出来供预览展示，不写进档案。
        foreach (var (id, group) in profile.ClassPlanGroups ?? [])
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            entry.Groups.Add(new CidataGroupInfo
            {
                Id = id.Trim(),
                Name = string.IsNullOrWhiteSpace(group.Name) ? "未命名课表群" : group.Name.Trim(),
                IsGlobal = group.IsGlobal,
            });
        }

        // ── 课表 ──
        var skippedByRule = new List<string>();
        var skippedByLayout = new List<string>();
        var skippedByUnusableLayout = new List<string>();
        var rangeLimited = 0;
        var enableDateLimited = 0;
        var misaligned = 0;

        foreach (var (id, plan) in profile.ClassPlans ?? [])
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var planName = string.IsNullOrWhiteSpace(plan.Name) ? "未命名课表" : plan.Name.Trim();

            if (plan.IsOverlay)
            {
                // 临时层课表是「某天的临时换课」，集控侧有专门的临时换课通道；档案里不下发。
                entry.SkippedOverlayPlans++;
                continue;
            }

            var layoutId = (plan.TimeLayoutId ?? string.Empty).Trim();
            var layout = content.TimeLayouts.FirstOrDefault(l =>
                string.Equals(l.Id, layoutId, StringComparison.OrdinalIgnoreCase));

            if (layout is null)
            {
                if (skippedLayoutIds.Contains(layoutId))
                {
                    skippedByUnusableLayout.Add(planName);
                }
                else
                {
                    skippedByLayout.Add(planName);
                }

                continue;
            }

            var rule = plan.TimeRule ?? new TimeRuleJson();

            // 集控的课表只能表达「星期几」这一种触发方式（对应宿主的 Weekly）。
            // 日期型 / 循环型规则无法表达——按「星期几」硬塞会变成每天都在用这张课表，属于静默改语义。
            if (rule.Type is 1 or 2)
            {
                skippedByRule.Add(planName);
                continue;
            }

            var classItems = layout.Items.Where(i => i.Kind == TimeItemKind.Class).ToList();
            var classes = plan.Classes ?? [];

            if (classes.Count != classItems.Count)
            {
                misaligned++;
            }

            var dto = new ClassPlanDto
            {
                Id = id.Trim(),
                Name = planName,
                TimeLayoutId = layout.Id,
                IsEnabled = plan.IsEnabled,
                GroupId = ToGroupId(plan.AssociatedGroup),
                // 宿主与集控的星期编码一致（0=周日..6=周六，都沿用 .NET 的 DayOfWeek），不需要 ±1。
                DaysOfWeek = [rule.WeekDay is >= 0 and <= 6 ? rule.WeekDay : 1],
            };

            ApplyWeekRotation(dto, rule);

            for (var index = 0; index < Math.Min(classes.Count, classItems.Count); index++)
            {
                var cell = classes[index];
                var subjectId = AsOptionalGuid(cell.SubjectId);

                dto.Slots.Add(new ClassPlanSlotDto
                {
                    Index = index,
                    StartTime = classItems[index].StartTime,
                    // 指向包内不存在的科目时留空：留一个悬空 ID 会让教室端显示成一格空白且查不出原因。
                    SubjectId = subjectId is not null && subjectIds.Contains(subjectId) ? subjectId : null,
                    IsEnabled = cell.IsEnabled,
                });
            }

            if (rule.RestrictsEnableRange)
            {
                rangeLimited++;
            }

            if (rule.EnableDates is { Count: > 0 })
            {
                enableDateLimited++;
            }

            content.ClassPlans.Add(dto);
        }

        if (skippedByUnusableLayout.Count > 0)
        {
            warnings.Add($"有 {skippedByUnusableLayout.Count} 张课表用的时间表没能解析出来，已一并跳过："
                + Join(skippedByUnusableLayout) + "。");
        }

        if (skippedByLayout.Count > 0)
        {
            warnings.Add($"有 {skippedByLayout.Count} 张课表引用了包里不存在的时间表，已跳过："
                + Join(skippedByLayout) + "。");
        }

        if (skippedByRule.Count > 0)
        {
            warnings.Add($"有 {skippedByRule.Count} 张课表用的是「日期型 / 循环型」触发规则，集控只能按星期几下发，"
                + $"已跳过：{Join(skippedByRule)}。这类课表建议在教室端本地维护。");
        }

        if (misaligned > 0)
        {
            warnings.Add($"有 {misaligned} 张课表的节数与时间表对不上（包里的逐格课程数量与「上课」时间点数量不一致），"
                + "已按能对上的部分导入，请导入后核对这几张课表。");
        }

        if (rangeLimited > 0)
        {
            warnings.Add($"有 {rangeLimited} 张课表带「生效日期范围」，集控的档案没有这一概念，"
                + "导入后这些课表会按星期几一直生效。");
        }

        if (enableDateLimited > 0)
        {
            warnings.Add($"有 {enableDateLimited} 张课表指定了「启用日期」（只在某些日子生效），集控无法表达，"
                + "这部分设置会丢失。");
        }

        if (entry.SkippedOverlayPlans > 0)
        {
            warnings.Add($"包里有 {entry.SkippedOverlayPlans} 张临时层课表（临时换课用的），未导入——"
                + "临时换课请走「快捷换课」通道。");
        }

        // ── 明确没有落点的部分 ──
        var orderedCount = profile.OrderedSchedules?.Count ?? 0;
        if (orderedCount > 0)
        {
            entry.SkippedOrderedSchedules = orderedCount;
            warnings.Add($"包里有 {orderedCount} 条「按日期的调休 / 换课」（OrderedSchedules），"
                + "集控的档案模型没有对应结构，未导入——需要的话请在集控里重新设置。");
        }

        return entry;
    }

    /// <summary>把宿主的周次轮换（单双周）映射到集控的 <c>WeekInterval</c> / <c>WeekOffset</c>。</summary>
    private static void ApplyWeekRotation(ClassPlanDto dto, TimeRuleJson rule)
    {
        // 宿主：WeekCountDiv 0=每周、1=第一周、2=第二周…（配合 WeekCountDivTotal 一起看，默认 2 周）。
        // 集控：WeekInterval 周期长度、WeekOffset 周期内第几周（0-based）。
        var div = rule.WeekCountDiv;
        if (div <= 0)
        {
            return;
        }

        // 宿主的默认值是 2 周，字段省略时按 2 算。
        var total = rule.WeekCountDivTotal.GetValueOrDefault(2);
        if (total < 2)
        {
            return;
        }

        dto.WeekInterval = total;
        dto.WeekOffset = Math.Min(div - 1, total - 1);
    }

    /// <summary>把宿主的课表群映射成集控的课表群 ID；默认群留空（由教室端归入它自己的默认群）。</summary>
    private static string? ToGroupId(string? associatedGroup)
    {
        var value = (associatedGroup ?? string.Empty).Trim();
        if (value.Length == 0 || !Guid.TryParse(value, out var guid))
        {
            return null;
        }

        // 全零 GUID = 宿主的「全局课表群」，是一个真实存在的群，要原样保留。
        if (guid == Guid.Empty)
        {
            return Guid.Empty.ToString();
        }

        // 宿主的默认课表群在教室端本来就存在，留空让教室端按自己的默认群处理更稳。
        return guid == HostDefaultGroupGuidValue ? null : guid.ToString();
    }

    private static bool IsProfileEntry(string path)
    {
        if (!path.StartsWith(ProfilesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = path[ProfilesPrefix.Length..];
        return rest.Length > 5
            && rest.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !rest.Contains('/');
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>解析宿主的 <c>TimeSpan</c> 文本（<c>"08:00:00"</c>）。</summary>
    private static string? ParseTime(string? value, string? legacyValue)
    {
        if (TryFormat(value, out var formatted))
        {
            return formatted;
        }

        // 2024 及更早的档案里时间点在 StartSecond/EndSecond 上（DateTime 串），
        // 新宿主已把它标为过时，但老文件仍在流通，这里兼容一下取「当天时间」。
        if (TryFormat(legacyValue, out formatted))
        {
            return formatted;
        }

        return null;
    }

    private static bool TryFormat(string? value, out string formatted)
    {
        formatted = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span))
        {
            // 更早的档案把时间点写成 DateTime 串（"2024-05-25T08:00:00"），取它的「当天时间」。
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var legacy))
            {
                span = legacy.TimeOfDay;
            }
            else
            {
                return false;
            }
        }

        // 只取「当天时间」：时间是跨天累计的（例如晚自习到次日），而集控的时间点是 HH:mm:ss。
        var seconds = (long)Math.Round(span.TotalSeconds);
        seconds = ((seconds % 86400) + 86400) % 86400;
        formatted = TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>把 GUID 文本转成「可空」形式；空串与全零 GUID（宿主的「未指定」写法）都算空。</summary>
    private static string? AsOptionalGuid(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (Guid.TryParse(text, out var guid))
        {
            return guid == Guid.Empty ? null : guid.ToString();
        }

        return null;
    }

    private static string StripExtension(string fileName) =>
        fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? fileName[..^5] : fileName;

    /// <summary>警告文案里最多列前几个名字，避免一条警告写满整屏。</summary>
    private static string Join(List<string> names) =>
        names.Count <= 5 ? string.Join("、", names) : string.Join("、", names.Take(5)) + $" 等 {names.Count} 张";

    // ── 以下内部模型只取需要的字段：宿主 JSON 是 PascalCase，
    //    HubJson.Options 是大小写不敏感的，未知字段自动忽略。 ──

    private sealed class ProfileJson
    {
        public string? Name { get; set; }
        public Dictionary<string, TimeLayoutJson>? TimeLayouts { get; set; }
        public Dictionary<string, ClassPlanJson>? ClassPlans { get; set; }
        public Dictionary<string, SubjectJson>? Subjects { get; set; }
        public Dictionary<string, ClassPlanGroupJson>? ClassPlanGroups { get; set; }
        public Dictionary<string, OrderedScheduleJson>? OrderedSchedules { get; set; }
    }

    private sealed class TimeLayoutJson
    {
        public string? Name { get; set; }
        public List<TimeLayoutItemJson>? Layouts { get; set; }
    }

    private sealed class TimeLayoutItemJson
    {
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }

        /// <summary>旧版字段（宿主的过时属性），仅作回退解析。</summary>
        public string? StartSecond { get; set; }

        /// <summary>旧版字段（宿主的过时属性），仅作回退解析。</summary>
        public string? EndSecond { get; set; }

        public int TimeType { get; set; }
        public string? BreakName { get; set; }
        public bool IsHideDefault { get; set; }
        public string? DefaultClassId { get; set; }
    }

    private sealed class ClassPlanJson
    {
        public string? Name { get; set; }
        public string? TimeLayoutId { get; set; }
        public List<ClassInfoJson>? Classes { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsOverlay { get; set; }
        public string? AssociatedGroup { get; set; }
        public TimeRuleJson? TimeRule { get; set; }
    }

    private sealed class ClassInfoJson
    {
        public string? SubjectId { get; set; }
        public bool IsEnabled { get; set; } = true;
    }

    private sealed class TimeRuleJson
    {
        /// <summary>0=Weekly、1=Date、2=Loop（宿主默认 Weekly 时会省略该字段）。</summary>
        public int Type { get; set; }

        /// <summary>0=周日、1..6=周一..周六（宿主默认 0 时会省略）。</summary>
        public int WeekDay { get; set; }

        public int WeekCountDiv { get; set; }

        /// <summary>宿主的默认值是 2，但字段可能省略，所以这里用可空类型区分「没写」与「写了 0」。</summary>
        public int? WeekCountDivTotal { get; set; }

        public bool RestrictsEnableRange { get; set; }
        public List<string>? EnableDates { get; set; }
    }

    private sealed class SubjectJson
    {
        public string? Name { get; set; }
        public string? Initial { get; set; }
        public string? TeacherName { get; set; }
        public bool IsOutDoor { get; set; }
    }

    private sealed class ClassPlanGroupJson
    {
        public string? Name { get; set; }
        public bool IsGlobal { get; set; }
    }

    private sealed class OrderedScheduleJson
    {
        public string? ClassPlanId { get; set; }
    }
}

/// <summary>解包结果：包内全部档案 + 包级的「有什么」信息。</summary>
public sealed class CidataArchive
{
    /// <summary>包内的档案（`Profiles/*.json`，可能有多份）。</summary>
    public List<CidataProfileEntry> Profiles { get; } = [];

    /// <summary>是否带 <c>Settings.json</c>（应用设置）。</summary>
    public bool HasSettings { get; set; }

    /// <summary><c>Config/</c> 下的条目数（自动化、组件布局、主题、管理策略等）。</summary>
    public int ConfigEntryCount { get; set; }

    /// <summary><c>Plugins/</c> 下的条目数（插件与插件设置）。</summary>
    public int PluginEntryCount { get; set; }

    /// <summary>包里那些「集控没有对应落点」的部分，用于预览页如实告知。</summary>
    public List<string> NotImportedNotes()
    {
        var notes = new List<string>();
        if (HasSettings)
        {
            notes.Add("应用设置（Settings.json）：主题、界面、天气等是教室端本机的设置，集控不下发，未导入。");
        }

        if (ConfigEntryCount > 0)
        {
            notes.Add($"其它配置（Config/ 下 {ConfigEntryCount} 个文件）：自动化、组件布局、主题、管理策略等，"
                + "集控的档案模型没有对应结构，未导入。");
        }

        if (PluginEntryCount > 0)
        {
            notes.Add($"插件（Plugins/ 下 {PluginEntryCount} 个文件）：插件请用「插件管理」下发，未导入。");
        }

        return notes;
    }
}

/// <summary>包内的一份档案，以及它映射成集控内容包的结果。</summary>
public sealed class CidataProfileEntry
{
    /// <summary>包内文件名，例如 <c>Default.json</c>（导入时用它指定哪一份）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>档案名（优先取档案里的 <c>Name</c>，为空时用文件名）。</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>映射出的集控内容包。</summary>
    public ContentBundleDto Content { get; set; } = new();

    /// <summary>导入时会丢东西的地方，逐条写清「丢了什么、为什么」。</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>包内按日期调休 / 换课的条数（未导入）。</summary>
    public int SkippedOrderedSchedules { get; set; }

    /// <summary>包内临时层课表的张数（未导入）。</summary>
    public int SkippedOverlayPlans { get; set; }

    /// <summary>包内定义的课表群（含默认与全局）。集控内容包里没有群清单，这里仅用于预览展示。</summary>
    public List<CidataGroupInfo> Groups { get; } = [];
}

/// <summary>`.cidata` 里的一份课表群（仅用于预览展示，不写进档案内容）。</summary>
public sealed class CidataGroupInfo
{
    /// <summary>课表群 ID（宿主里的 GUID）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>课表群名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>是否为宿主的「全局课表群」。</summary>
    public bool IsGlobal { get; set; }
}
