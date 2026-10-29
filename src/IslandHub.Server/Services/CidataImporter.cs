using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;

namespace IslandHub.Server.Services;

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
/// **能映射到 IslandHub 的部分只有四块**：时间表、课表、科目、课表群。其余（<c>OrderedSchedules</c>
/// 按日期的调休、<c>Config/Automations</c> 自动化、<c>Settings.json</c> 应用设置、<c>Plugins/</c> 插件）
/// 在 IslandHub 的档案模型里**没有落点**，一律如实列进 <see cref="CidataProfileEntry.Warnings"/> 而不是悄悄丢掉。
/// </para>
///
/// <para>
/// **对齐口径（与插件侧 <c>ClassIslandAdapter</c> 互为逆映射，改一边必须改另一边）**：
/// 宿主的<b>一张课表只代表一天</b>（星期由 <c>TimeRule.WeekDay</c> 决定，0=周日..6=周六），
/// 而 IslandHub 的 <see cref="ClassPlanDto"/> 也是「一张课表 = 一个星期几」（<c>DaysOfWeek</c> 取单个值），
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
                    // 应用设置整块不导入，但**轮换起点与多周偏移要看一眼**：
                    // 多周课表离开这两个值就不知道「哪一周算第 1 周」，导进来会是错的日子。
                    result.HostRotation = ParseHostRotation(ReadText(entry));
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

                result.Profiles.Add(Convert(entry.Name, ReadText(entry), result.HostRotation));
            }

            if (result.Profiles.Count == 0)
            {
                throw new InvalidDataException(
                    "包里没有找到任何档案（Profiles/*.json）——导出时请勾上「课表与档案」。");
            }

            return result;
        }
    }

    /// <summary>
    /// 把一份档案 JSON 映射成 IslandHub 内容包。
    /// </summary>
    /// <param name="fileName">包内文件名（例如 <c>Default.json</c>）。</param>
    /// <param name="json">档案 JSON 文本。</param>
    /// <param name="hostRotation">包内 <c>Settings.json</c> 里的轮换起点 / 多周偏移（没有就传 null）。</param>
    public static CidataProfileEntry Convert(string fileName, string json, CidataRotationInfo? hostRotation = null)
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
        // 分割线 / 行动出现了「结束时间 ≠ 开始时间」的历史脏数据：保留原值，但要在预览里说一句。
        var dirtyTimeline = false;
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

                var kind = TimeItemKind.FromTimeType(item.TimeType);

                items.Add(new TimeLayoutItemDto
                {
                    StartTime = start,
                    EndTime = end,
                    Kind = kind,
                    BreakName = string.IsNullOrWhiteSpace(item.BreakName) ? null : item.BreakName.Trim(),
                    IsHideDefault = item.IsHideDefault,
                    DefaultSubjectId = AsOptionalGuid(item.DefaultClassId),
                });

                // 分割线（TimeType=2）与行动（TimeType=3）是**独立时间点**，不是上课节点的附属属性。
                // 两者都已作为 items 写入（Kind 分别为 separator / action），这里只是额外记账：
                // 预览要能数清"有几条"，管理员才知道包里的作息被完整搬过来了。
                if (kind == TimeItemKind.Separator)
                {
                    entry.Separators.Add(new CidataSeparatorInfo
                    {
                        Layout = dto.Name,
                        Time = start.Length > 5 ? start[..5] : start,
                    });

                    // 零时长是常态，但历史数据里确实见过 Start != End；保留原值，只是提示一句。
                    if (!string.Equals(start, end, StringComparison.Ordinal))
                    {
                        dirtyTimeline = true;
                    }
                }
                else if (kind == TimeItemKind.Action)
                {
                    var actionType = string.IsNullOrWhiteSpace(item.Action?.Type) ? "Unknown" : item.Action!.Type!.Trim();
                    entry.Actions.Add(new CidataActionInfo
                    {
                        Layout = dto.Name,
                        Time = start.Length > 5 ? start[..5] : start,
                        Name = item.Action?.Name?.Trim() ?? string.Empty,
                        Type = actionType,
                    });
                    entry.ActionTypeCounts[actionType] = entry.ActionTypeCounts.GetValueOrDefault(actionType) + 1;

                    if (!string.Equals(start, end, StringComparison.Ordinal))
                    {
                        dirtyTimeline = true;
                    }
                }
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

        // ── 分割线 / 行动的如实说明 ──
        // 这两类都是**独立时间点**（TimeType 2 / 3），已按原位置、原顺序导入；
        // 但「行动」引用的通知内容与自动化配置属于教室端本机，IslandHub 的档案模型里没有落点，
        // 所以只保留"到点该做点什么"的位置，内容本身不随包走——这一点必须写在预览里，
        // 否则换一台机器导入后，行动会变成「到点但什么都不做」，而且没人知道为什么。
        if (entry.Actions.Count > 0)
        {
            var types = string.Join("、", entry.ActionTypeCounts
                .OrderByDescending(pair => pair.Value)
                .Select(pair => $"{pair.Key} {pair.Value} 条"));
            warnings.Add($"{entry.Actions.Count} 条「行动」时间点已按原位置导入（{types}），"
                + "但行动引用的通知内容 / 自动化配置属于教室端本机，不随包导入——"
                + "教室端原有的会保留，换机器则需要重新配置。");

            var unknown = entry.ActionTypeCounts
                .Where(pair => pair.Key is not ("Notification" or "Automation"))
                .Sum(pair => pair.Value);
            if (unknown > 0)
            {
                warnings.Add($"{unknown} 条行动的 Type 无法识别（可能来自插件或更新的宿主版本），"
                    + "已按「到点的行动节点」导入，内容需要人工确认。");
            }
        }

        if (dirtyTimeline)
        {
            warnings.Add("部分分割线 / 行动时间点的结束时间与开始时间不一致（宿主历史脏数据），"
                + "已保留原始结束时间；教室里按开始时间显示。");
        }

        var layoutIds = content.TimeLayouts.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subjectIds = content.Subjects.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── 课表群 ──
        // 注意：IslandHub 的内容包里**没有**课表群清单，课表只带一个 GroupId 字符串。
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
                // 临时层课表是「某天的临时换课」，IslandHub 侧有专门的临时换课通道；档案里不下发。
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

            // IslandHub 的课表只能表达「星期几」这一种触发方式（对应宿主的 Weekly）。
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
                // 宿主与 IslandHub 的星期编码一致（0=周日..6=周六，都沿用 .NET 的 DayOfWeek），不需要 ±1。
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
            warnings.Add($"有 {skippedByRule.Count} 张课表用的是「日期型 / 循环型」触发规则，IslandHub 只能按星期几下发，"
                + $"已跳过：{Join(skippedByRule)}。这类课表建议在教室端本地维护。");
        }

        if (misaligned > 0)
        {
            warnings.Add($"有 {misaligned} 张课表的节数与时间表对不上（包里的逐格课程数量与「上课」时间点数量不一致），"
                + "已按能对上的部分导入，请导入后核对这几张课表。");
        }

        if (rangeLimited > 0)
        {
            warnings.Add($"有 {rangeLimited} 张课表带「生效日期范围」，IslandHub 的档案没有这一概念，"
                + "导入后这些课表会按星期几一直生效。");
        }

        if (enableDateLimited > 0)
        {
            warnings.Add($"有 {enableDateLimited} 张课表指定了「启用日期」（只在某些日子生效），IslandHub 无法表达，"
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
                + "IslandHub 的档案模型没有对应结构，未导入——需要的话请在 IslandHub 里重新设置。");
        }

        // ── 多周轮换的起点与偏移（来自包里的 Settings.json）──
        ApplyHostRotation(entry, hostRotation);

        return entry;
    }

    /// <summary>
    /// 把包内宿主的轮换起点 / 多周偏移接到档案的轮换设置上。
    ///
    /// <para>
    /// 只在**这份档案确实有多周课表**（有课表的周期 ≥2）时才处理：没有多周课表的话，
    /// 起点填与不填都不影响，硬写进去反而会让管理员以为「这个档案在轮换」。
    /// 起点一律**提示核对**——宿主机从来没设过的话，那个值只是「导出那天所在周的周日」，
    /// 照着它算周次会整学期错位，而这种错位要到上课那天才看得出来。
    /// </para>
    /// </summary>
    private static void ApplyHostRotation(CidataProfileEntry entry, CidataRotationInfo? host)
    {
        var cycle = entry.Content.ClassPlans
            .Where(p => p.WeekInterval >= 2)
            .Select(p => p.WeekInterval)
            .DefaultIfEmpty(0)
            .Max();

        if (cycle < 2)
        {
            return;
        }

        // 周次必须写进档案的轮换设置：保存时规范化会「档案没启用轮换 → 清掉课表周次」，
        // 不写等于把刚导进来的单双周课表整套作废。
        entry.Content.Rotation.Weeks = cycle;

        if (host is null)
        {
            entry.Warnings.Add($"这份档案是 {cycle} 周一轮换，但包里没有可用的轮换起点（Settings.json 缺失或读不出来），"
                + "导入后请在档案的「多周轮换」里把起点填成开学第一周，否则教室端会按各自的本地日期算周次。");
            return;
        }

        if (host.SemesterStartDate is { } start)
        {
            entry.Content.Rotation.SemesterStartDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            entry.Warnings.Add($"这份档案是 {cycle} 周一轮换，轮换起点按包内宿主的「{start:yyyy-MM-dd}」导入，"
                + "请核对它确实是开学第一周（宿主机上没设置过的话，这个值只是导出那天所在周的周日，照它算会整学期错位）。");
        }

        if (host.Offsets.TryGetValue(cycle, out var offset) && offset != 0)
        {
            entry.Content.Rotation.Offset = offset;
            entry.Warnings.Add($"{cycle} 周轮换在宿主上带着 {offset} 周的手动偏移（临时调过进度），已一并导入。");
        }
    }

    /// <summary>从包内 <c>Settings.json</c> 里取出与多周轮换有关的两个设置。</summary>
    private static CidataRotationInfo? ParseHostRotation(string json)
    {
        SettingsJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SettingsJson>(json, HubJson.Options);
        }
        catch (JsonException)
        {
            // 设置解析失败不该拖垮导入：轮换起点是「锦上添花」，照旧导课表。
            return null;
        }

        var info = new CidataRotationInfo();

        if (DateTime.TryParse(parsed?.SingleWeekStartTime, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start))
        {
            info.SemesterStartDate = DateOnly.FromDateTime(start);
        }

        // 宿主的 MultiWeekRotationOffset 下标就是周期长度（2-first），-1 表示该周期没设置。
        if (parsed?.MultiWeekRotationOffset is { Count: > 0 } offsets)
        {
            for (var cycle = 2; cycle < offsets.Count; cycle++)
            {
                if (offsets[cycle] >= 0)
                {
                    info.Offsets[cycle] = offsets[cycle];
                }
            }
        }

        return info.SemesterStartDate is null && info.Offsets.Count == 0 ? null : info;
    }

    /// <summary>把宿主的周次轮换（单双周）映射到 IslandHub 的 <c>WeekInterval</c> / <c>WeekOffset</c>。</summary>
    private static void ApplyWeekRotation(ClassPlanDto dto, TimeRuleJson rule)
    {
        // 宿主：WeekCountDiv 0=每周、1=第一周、2=第二周…（配合 WeekCountDivTotal 一起看，默认 2 周）。
        // IslandHub：WeekInterval 周期长度、WeekOffset 周期内第几周（0-based）。
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

    /// <summary>把宿主的课表群映射成 IslandHub 的课表群 ID；默认群留空（由教室端归入它自己的默认群）。</summary>
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

        // 只取「当天时间」：时间是跨天累计的（例如晚自习到次日），而 IslandHub 的时间点是 HH:mm:ss。
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

    /// <summary>包内 <c>Settings.json</c> 里与多周轮换有关的部分（其余设置一律不导入）。</summary>
    private sealed class SettingsJson
    {
        /// <summary>轮换起点（宿主的「周次轮换起点 / 开学日期」）。</summary>
        public string? SingleWeekStartTime { get; set; }

        /// <summary>多周轮换偏移，下标为周期长度（2-first），<c>-1</c> 表示该周期没设置。</summary>
        public List<int>? MultiWeekRotationOffset { get; set; }
    }

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

        /// <summary>
        /// 行动（<c>TimeType = 3</c>）承载的具体动作：通知 / 自动化 / 插件自定义类型。
        /// <para>只用来在预览里说清"这条行动原本要做什么"——内容不随包导入（见导入说明）。</para>
        /// </summary>
        public TimeLayoutItemActionJson? Action { get; set; }
    }

    /// <summary>行动时间点里的 <c>Action</c> 字段（只取几个通用字段，其余原样忽略）。</summary>
    private sealed class TimeLayoutItemActionJson
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        public JsonElement? Settings { get; set; }
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

    /// <summary>包内宿主的多周轮换设置（起点 / 各周期的手动偏移）；没有或不涉及时为 <c>null</c>。</summary>
    public CidataRotationInfo? HostRotation { get; set; }

    /// <summary><c>Config/</c> 下的条目数（自动化、组件布局、主题、管理策略等）。</summary>
    public int ConfigEntryCount { get; set; }

    /// <summary><c>Plugins/</c> 下的条目数（插件与插件设置）。</summary>
    public int PluginEntryCount { get; set; }

    /// <summary>包里那些「IslandHub 没有对应落点」的部分，用于预览页如实告知。</summary>
    public List<string> NotImportedNotes()
    {
        var notes = new List<string>();
        if (HasSettings)
        {
            notes.Add("应用设置（Settings.json）：主题、界面、天气等是教室端本机的设置，IslandHub 不下发，未导入。");
        }

        if (ConfigEntryCount > 0)
        {
            notes.Add($"其它配置（Config/ 下 {ConfigEntryCount} 个文件）：自动化、组件布局、主题、管理策略等，"
                + "IslandHub 的档案模型没有对应结构，未导入。");
        }

        if (PluginEntryCount > 0)
        {
            notes.Add($"插件（Plugins/ 下 {PluginEntryCount} 个文件）：插件请用「插件管理」下发，未导入。");
        }

        return notes;
    }
}

/// <summary>包内的一条分割线时间点（<c>TimeType = 2</c>）。</summary>
public sealed class CidataSeparatorInfo
{
    /// <summary>所属时间表名。</summary>
    public string Layout { get; set; } = string.Empty;

    /// <summary>出现时刻（<c>HH:mm</c>）。</summary>
    public string Time { get; set; } = string.Empty;
}

/// <summary>包内的一条行动时间点（<c>TimeType = 3</c>）。</summary>
public sealed class CidataActionInfo
{
    /// <summary>所属时间表名。</summary>
    public string Layout { get; set; } = string.Empty;

    /// <summary>出现时刻（<c>HH:mm</c>）。</summary>
    public string Time { get; set; } = string.Empty;

    /// <summary>行动名称（宿主里给人看的那个名字）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>Action.Type</c>：<c>Notification</c> / <c>Automation</c> / 其它（插件或新版本引入）。</summary>
    public string Type { get; set; } = string.Empty;
}

/// <summary>包内宿主的多周轮换设置（从 <c>Settings.json</c> 里取）。</summary>
public sealed class CidataRotationInfo
{
    /// <summary>轮换起点（开学第一周的第一天）。</summary>
    public DateOnly? SemesterStartDate { get; set; }

    /// <summary>各周期长度对应的手动偏移（键为周期长度，2-first）。</summary>
    public Dictionary<int, int> Offsets { get; } = [];
}

/// <summary>包内的一份档案，以及它映射成 IslandHub 内容包的结果。</summary>
public sealed class CidataProfileEntry
{
    /// <summary>包内文件名，例如 <c>Default.json</c>（导入时用它指定哪一份）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>档案名（优先取档案里的 <c>Name</c>，为空时用文件名）。</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>映射出的 IslandHub 内容包。</summary>
    public ContentBundleDto Content { get; set; } = new();

    /// <summary>导入时会丢东西的地方，逐条写清「丢了什么、为什么」。</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>包内按日期调休 / 换课的条数（未导入）。</summary>
    public int SkippedOrderedSchedules { get; set; }

    /// <summary>包内临时层课表的张数（未导入）。</summary>
    public int SkippedOverlayPlans { get; set; }

    /// <summary>包里识别到的分割线时间点（<c>TimeType = 2</c>，已导入）。</summary>
    public List<CidataSeparatorInfo> Separators { get; } = [];

    /// <summary>包里识别到的行动时间点（<c>TimeType = 3</c>，位置已导入，内容不随包走）。</summary>
    public List<CidataActionInfo> Actions { get; } = [];

    /// <summary>行动按 <c>Action.Type</c> 的计数（预览里给管理员看"都包含哪几类"）。</summary>
    public Dictionary<string, int> ActionTypeCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>包内定义的课表群（含默认与全局）。IslandHub 内容包里没有群清单，这里仅用于预览展示。</summary>
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
