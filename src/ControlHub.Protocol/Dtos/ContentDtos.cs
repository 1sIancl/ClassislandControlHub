namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 时间点类型。与 ClassIsland 的 <c>TimeLayoutItem.TimeType</c> 整数枚举一一对应，
/// 但协议层使用字符串以保证可读性与向前兼容。
/// </summary>
public static class TimeItemKind
{
    /// <summary>上课（对应 TimeType = 0）。</summary>
    public const string Class = "class";

    /// <summary>课间休息（对应 TimeType = 1）。</summary>
    public const string Break = "break";

    /// <summary>分割线（对应 TimeType = 2）。</summary>
    public const string Separator = "separator";

    /// <summary>行动（对应 TimeType = 3）。</summary>
    public const string Action = "action";

    /// <summary>协议字符串 → ClassIsland 的 <c>TimeType</c> 整数。</summary>
    public static int ToTimeType(string? kind) => kind switch
    {
        Break => 1,
        Separator => 2,
        Action => 3,
        _ => 0,
    };

    /// <summary>ClassIsland 的 <c>TimeType</c> 整数 → 协议字符串。</summary>
    public static string FromTimeType(int timeType) => timeType switch
    {
        1 => Break,
        2 => Separator,
        3 => Action,
        _ => Class,
    };

    /// <summary>判断是否为合法的类型取值。</summary>
    public static bool IsValid(string? kind) =>
        kind is Class or Break or Separator or Action;
}

/// <summary>
/// 科目。对应 ClassIsland 档案中的 <c>Subject</c>。
/// </summary>
public sealed class SubjectDto
{
    /// <summary>科目稳定标识（GUID 字符串）。客户端以此 ID 关联课表节点。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>科目全称，例如「语文」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>科目简称，用于大屏紧凑显示，例如「语」。为空时由客户端按首字推导。</summary>
    public string Initial { get; set; } = string.Empty;

    /// <summary>任课教师姓名，可为空。</summary>
    public string TeacherName { get; set; } = string.Empty;

    /// <summary>是否为户外课程（用于天气/户外提醒等场景）。</summary>
    public bool IsOutDoor { get; set; }
}

/// <summary>
/// 时间表中的一个时间点，对应 ClassIsland 的 <c>TimeLayoutItem</c>。
/// </summary>
public sealed class TimeLayoutItemDto
{
    /// <summary>开始时间，格式 <c>HH:mm:ss</c>。</summary>
    public string StartTime { get; set; } = "00:00:00";

    /// <summary>结束时间，格式 <c>HH:mm:ss</c>。当类型为分割线/行动时该值会被忽略。</summary>
    public string EndTime { get; set; } = "00:00:00";

    /// <summary>时间点类型，取值见 <see cref="TimeItemKind"/>。</summary>
    public string Kind { get; set; } = TimeItemKind.Class;

    /// <summary>自定义课间名称（仅课间类型有效），为空时客户端显示「课间休息」。</summary>
    public string? BreakName { get; set; }

    /// <summary>是否默认隐藏该时间点。</summary>
    public bool IsHideDefault { get; set; }

    /// <summary>默认科目 ID（当课表节点未指定科目时的回退值），可为空。</summary>
    public string? DefaultSubjectId { get; set; }
}

/// <summary>
/// 时间表（作息时间）。对应 ClassIsland 的 <c>TimeLayout</c>。
/// </summary>
public sealed class TimeLayoutDto
{
    /// <summary>时间表稳定标识（GUID 字符串）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>时间表名称，例如「夏季作息」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>时间点列表。下发时由服务端按开始时间升序排好，客户端无需再排序。</summary>
    public List<TimeLayoutItemDto> Items { get; set; } = [];

    /// <summary>该时间表中「上课」类型的时间点数量，即课表节数。由服务端计算，便于客户端校验。</summary>
    public int ClassItemCount =>
        Items.Count(i => i.Kind == TimeItemKind.Class);
}

/// <summary>
/// 课表中的一个课程节点，对应 ClassIsland 的 <c>ClassInfo</c>。
/// </summary>
public sealed class ClassPlanSlotDto
{
    /// <summary>
    /// 节点序号，从 0 开始，与时间表中第 N 个「上课」时间点一一对应。
    /// 这是两端对齐课表的核心字段。
    /// </summary>
    public int Index { get; set; }

    /// <summary>该节点对应的上课开始时间（<c>HH:mm:ss</c>）。冗余字段，用于服务端/客户端交叉校验。</summary>
    public string? StartTime { get; set; }

    /// <summary>科目 ID。为空表示该节无课（自习/空堂）。</summary>
    public string? SubjectId { get; set; }

    /// <summary>该节点是否启用。禁用后客户端会隐藏这节课。</summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// 课表。对应 ClassIsland 的 <c>ClassPlan</c>。
/// </summary>
public sealed class ClassPlanDto
{
    /// <summary>课表稳定标识（GUID 字符串）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>课表名称，例如「周一至周五」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>所属时间表 ID。</summary>
    public string TimeLayoutId { get; set; } = string.Empty;

    /// <summary>是否默认启用（不依赖触发规则即生效）。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>是否为临时层（换课）课表。集控下发通常为 <c>false</c>。</summary>
    public bool IsOverlay { get; set; }

    /// <summary>临时层对应的源课表 ID。</summary>
    public string? OverlaySourceId { get; set; }

    /// <summary>所属课表群 ID。留空则由客户端归入默认课表群。</summary>
    public string? GroupId { get; set; }

    /// <summary>触发规则：适用星期几，取值 <c>0=周日 .. 6=周六</c>。为空表示不限。</summary>
    public List<int> DaysOfWeek { get; set; } = [];

    /// <summary>触发规则：周次间隔（每 N 周生效），0 表示每周都生效。</summary>
    public int WeekInterval { get; set; }

    /// <summary>
    /// 触发规则：在 <see cref="WeekInterval"/> 周期内生效的周次序号（从 0 开始）。
    /// 例如 <c>WeekInterval = 2, WeekOffset = 0</c> 表示「单周」，<c>WeekOffset = 1</c> 表示「双周」。
    /// </summary>
    public int WeekOffset { get; set; }

    /// <summary>课程节点列表，按 <see cref="ClassPlanSlotDto.Index"/> 升序。</summary>
    public List<ClassPlanSlotDto> Slots { get; set; } = [];
}

/// <summary>
/// 档案中的自定义设置项。集控可下发任意键值，插件按需读取。
/// </summary>
public sealed class ProfileSettingsDto
{
    /// <summary>自定义键值对。键建议使用 <c>命名空间.键名</c> 形式避免冲突。</summary>
    public Dictionary<string, string> Values { get; set; } = [];

    /// <summary>强制执行的开关项，例如是否锁定客户端本地编辑。</summary>
    public bool LockLocalEditing { get; set; }

    /// <summary>下发后可读的用户提示信息。</summary>
    public string? Announcement { get; set; }
}

/// <summary>
/// 一次同步下发的完整内容包。
/// </summary>
public sealed class ContentBundleDto
{
    /// <summary>时间表集合。</summary>
    public List<TimeLayoutDto> TimeLayouts { get; set; } = [];

    /// <summary>课表集合。</summary>
    public List<ClassPlanDto> ClassPlans { get; set; } = [];

    /// <summary>科目集合。</summary>
    public List<SubjectDto> Subjects { get; set; } = [];

    /// <summary>自定义设置。</summary>
    public ProfileSettingsDto Settings { get; set; } = new();
}
