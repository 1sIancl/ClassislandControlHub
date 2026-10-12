namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 教室端课表状态机（与 ClassIsland 的 <c>TimeState</c> 对应，但用字符串表达，
/// 避免把宿主的枚举泄漏到协议里）。
/// </summary>
public static class LessonLiveStates
{
    /// <summary>未加载课表 / 无时间点。</summary>
    public const string None = "none";

    /// <summary>正在上课。</summary>
    public const string OnClass = "onClass";

    /// <summary>预备铃（上课前的那一小段）。</summary>
    public const string Prepare = "prepare";

    /// <summary>课间休息。</summary>
    public const string Breaking = "breaking";

    /// <summary>放学（当天课已上完）。</summary>
    public const string AfterSchool = "afterSchool";
}

/// <summary>
/// 教室端 ClassIsland 的课表实时状态。
/// <para>随心跳上报（体积很小，几个字段），也用于管理端展示——「下节课是什么、什么时候开始、
/// 还有多久」这类信息**只有教室端自己算得准**：教师可能在教室机本地改过课表、也可能刚做过换课，
/// 服务端按自己的档案推算出来的结果与实际大屏上显示的可能不是同一节课。</para>
/// </summary>
public sealed class LessonStateDto
{
    /// <summary>状态，取值见 <see cref="LessonLiveStates"/>。</summary>
    public string State { get; set; } = LessonLiveStates.None;

    /// <summary>宿主认定的「今天」（<c>yyyy-MM-dd</c>），用于跨天判断与界面展示。</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>当前课表名（含「（临时层）」后缀时说明教室端正处在换课状态）。</summary>
    public string? ClassPlanName { get; set; }

    /// <summary>当前时间点的科目名（课间时为课间名称，无则为空）。</summary>
    public string? CurrentSubject { get; set; }

    /// <summary>下节课（或正在上的这节课）的科目名。</summary>
    public string? NextSubject { get; set; }

    /// <summary>下节课开始时间（<c>HH:mm</c>）。</summary>
    public string? NextStartAt { get; set; }

    /// <summary>下节课结束时间（<c>HH:mm</c>）。</summary>
    public string? NextEndAt { get; set; }

    /// <summary>
    /// 距上课剩余秒数；**正在上课时是距下课**（与 ClassIsland 的语义一致）。
    /// 界面用「上报时刻 + 该秒数」在前端本地递减，不必每秒都发心跳。
    /// </summary>
    public int LeftSeconds { get; set; }

    /// <summary>是否处于换课状态（当天有临时层 / 临时调休）。</summary>
    public bool IsAdjusted { get; set; }

    /// <summary>教室端上报时刻（UTC）。</summary>
    public DateTimeOffset ReportedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>一节可选的课（今天的第 N 节）。</summary>
public sealed class LessonSlotDto
{
    /// <summary>节次下标（0 基，与档案里的 <c>Classes</c> 顺序一致）。</summary>
    public int Index { get; set; }

    /// <summary>课程序号（界面上的「第 3 节」= Index + 1）。</summary>
    public int Number => Index + 1;

    /// <summary>科目 ID（可能为空：那天这一节没排课）。</summary>
    public string? SubjectId { get; set; }

    /// <summary>科目名（空则显示为「未排课」）。</summary>
    public string SubjectName { get; set; } = string.Empty;

    /// <summary>开始时间（<c>HH:mm</c>）。</summary>
    public string StartAt { get; set; } = string.Empty;

    /// <summary>结束时间（<c>HH:mm</c>）。</summary>
    public string EndAt { get; set; } = string.Empty;

    /// <summary>是否被临时换过（ClassIsland 的 <c>ClassInfo.IsChangedClass</c>）。</summary>
    public bool IsChanged { get; set; }

    /// <summary>是否正在上这一节。</summary>
    public bool IsCurrent { get; set; }

    /// <summary>是否是下一节。</summary>
    public bool IsNext { get; set; }
}

/// <summary>可供调休选用的课表。</summary>
public sealed class LessonPlanOptionDto
{
    /// <summary>课表 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>课表名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>是否默认启用。</summary>
    public bool IsEnabled { get; set; }

    /// <summary>是否为临时层课表（换课产生的副本）。</summary>
    public bool IsOverlay { get; set; }
}

/// <summary>可供换课选用的科目。</summary>
public sealed class LessonSubjectOptionDto
{
    /// <summary>科目 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>科目名。</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// 教室端的课表目录：今天各节、可选科目、可选课表。
/// <para>**按需拉取**而不是塞进心跳：一个学校的科目 + 课表可能有几十条，
/// 每次心跳都带上会让心跳从几百字节涨到几 KB（教室机是每分钟一次）。</para>
/// </summary>
public sealed class LessonCatalogDto
{
    /// <summary>目录对应的日期（<c>yyyy-MM-dd</c>）。</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>当天生效的课表 ID。</summary>
    public string ClassPlanId { get; set; } = string.Empty;

    /// <summary>当天生效的课表名。</summary>
    public string ClassPlanName { get; set; } = string.Empty;

    /// <summary>当天是否需要按调休处理（该日期被指定过课表）。</summary>
    public bool IsAdjustedDate { get; set; }

    /// <summary>今天各节。</summary>
    public List<LessonSlotDto> Slots { get; set; } = [];

    /// <summary>可选科目。</summary>
    public List<LessonSubjectOptionDto> Subjects { get; set; } = [];

    /// <summary>可选课表（调休用）。</summary>
    public List<LessonPlanOptionDto> ClassPlans { get; set; } = [];
}

/// <summary>
/// 临时换课请求（A 端 → 教室端）。
/// <para>落点在 ClassIsland 的**临时层课表**上：与在大屏上手动换课完全等价，
/// 宿主的换课界面会显示同一份结果，且「清除临时层」即可原样恢复。</para>
/// </summary>
public sealed class LessonSwapRequestDto
{
    /// <summary>目标日期（<c>yyyy-MM-dd</c>），留空表示教室端的今天。</summary>
    public string? Date { get; set; }

    /// <summary>目标节次（0 基）。</summary>
    public int ClassIndex { get; set; }

    /// <summary>换成哪个科目（<see cref="Swap"/> 为 false 时使用）。</summary>
    public string? SubjectId { get; set; }

    /// <summary>换成的科目名（按名字匹配；与 <see cref="SubjectId"/> 二选一，ID 优先）。</summary>
    public string? SubjectName { get; set; }

    /// <summary>是否与另一节**对调**（ClassIsland 的「换课模式」，默认行为）。</summary>
    public bool Swap { get; set; }

    /// <summary>对调的另一节（<see cref="Swap"/> 为 true 时必填）。</summary>
    public int SwapWithIndex { get; set; } = -1;

    /// <summary>是否直接改原课表（默认 false = 走临时层，可一键恢复）。</summary>
    public bool WriteToSourceClassPlan { get; set; }

    /// <summary>是否清除临时层（恢复原课表）。为 true 时忽略其它字段。</summary>
    public bool Restore { get; set; }
}

/// <summary>
/// 调休请求（A 端 → 教室端）：给某个日期指定一张课表。
/// <para>等价于在 ClassIsland 的课表设置里手动把那天绑到另一张课表（例如「周六按周三的课表上课」）。
/// 教室端可以随时撤销，次日自动回到按星期几的正常规则。</para>
/// </summary>
public sealed class LessonAdjustRequestDto
{
    /// <summary>目标日期（<c>yyyy-MM-dd</c>）。</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>该日期改用哪张课表（ID）。</summary>
    public string? ClassPlanId { get; set; }

    /// <summary>该日期改用哪张课表（名称，按名字匹配；ID 优先）。</summary>
    public string? ClassPlanName { get; set; }

    /// <summary>是否撤销该日期的调休。</summary>
    public bool Restore { get; set; }
}

/// <summary>教室端实时课表状态（管理端视图：设备信息 + 课表状态）。</summary>
public sealed class DeviceLessonStateDto
{
    /// <summary>设备 ID。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名（教室名）。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>所属分组（楼栋 / 楼层）。</summary>
    public string? GroupName { get; set; }

    /// <summary>设备是否在线。</summary>
    public bool IsOnline { get; set; }

    /// <summary>课表实时状态；从未上报过则为 <c>null</c>。</summary>
    public LessonStateDto? Lesson { get; set; }

    /// <summary>最近一次上报课表目录的时间（为空表示还没拉取过）。</summary>
    public DateTimeOffset? CatalogAt { get; set; }
}

/// <summary>管理端实时课表总览。</summary>
public sealed class LessonLiveOverviewDto
{
    /// <summary>服务端当前时间（UTC），前端据此把「剩余秒数」换算成本地倒计时。</summary>
    public DateTimeOffset ServerTime { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>各设备的课表状态。</summary>
    public List<DeviceLessonStateDto> Devices { get; set; } = [];
}

/// <summary>下发换课 / 调休时的目标范围。</summary>
public sealed class LessonDispatchRequest
{
    /// <summary>目标设备 ID 列表；与 <see cref="GroupId"/> 二选一。</summary>
    public List<string> DeviceIds { get; set; } = [];

    /// <summary>目标分组 ID（楼栋 / 楼层），选中该分组下所有未停用设备。</summary>
    public string? GroupId { get; set; }

    /// <summary>临时换课参数（<c>kind = lesson.swap</c> 时使用）。</summary>
    public LessonSwapRequestDto? Swap { get; set; }

    /// <summary>调休参数（<c>kind = lesson.adjust</c> 时使用）。</summary>
    public LessonAdjustRequestDto? Adjust { get; set; }
}
