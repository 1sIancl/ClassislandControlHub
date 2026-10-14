namespace ControlHub.Protocol.Dtos;

/// <summary>提醒的重复规则。</summary>
public static class ReminderRepeats
{
    /// <summary>只提醒一次。</summary>
    public const string Once = "once";

    /// <summary>每天（可每 N 天）。</summary>
    public const string Daily = "daily";

    /// <summary>每周（可指定星期几、每 N 周）。</summary>
    public const string Weekly = "weekly";

    /// <summary>每月（可指定几号、每 N 月）。</summary>
    public const string Monthly = "monthly";

    /// <summary>全部取值。</summary>
    public static readonly string[] All = [Once, Daily, Weekly, Monthly];
}

/// <summary>提醒的投递目标。</summary>
public static class ReminderTargets
{
    /// <summary>全部在线设备。</summary>
    public const string All = "all";

    /// <summary>指定分组（楼栋 / 楼层）。</summary>
    public const string Group = "group";

    /// <summary>指定单台设备（教室）。</summary>
    public const string Device = "device";

    /// <summary>全部取值。</summary>
    public static readonly string[] AllValues = [All, Group, Device];
}

/// <summary>提醒的投递方式。</summary>
public static class ReminderChannels
{
    /// <summary>推送到教室大屏（复用远程提醒指令，可语音播报）。</summary>
    public const string Screen = "screen";

    /// <summary>全部取值。邮件 / 短信等需要外部服务凭证，暂未实现。</summary>
    public static readonly string[] All = [Screen];
}

/// <summary>
/// 提醒到点后**做什么**（#20 定时开关机）。
/// <para>排程本身（哪天 / 几点 / 重复规则）与「做什么」正交，所以复用提醒的排程器：
/// 同一套时间推算、同一套错过补发与触发历史，只是到点后分别下发通知 / 关机 / 唤醒。</para>
/// </summary>
public static class ReminderActions
{
    /// <summary>推送到教室大屏（默认；老数据的取值也是它）。</summary>
    public const string Notify = "notify";

    /// <summary>
    /// 关闭教室机：下发 <c>power.shutdown</c> 指令，**只发给在线设备**——
    /// 关机是不可逆的物理动作，离线设备排队到明天早上开机时再执行只会更糟。
    /// </summary>
    public const string PowerShutdown = "power.shutdown";

    /// <summary>
    /// 唤醒教室机：直接发 Wake-on-LAN 魔术包，**只发给非停用设备**（不看在线）。
    /// <para>要唤醒的机器按定义就是关着的（离线）；要求在线等于这条功能永远不生效。
    /// 没上报过网卡地址的设备会被跳过并如实说明。</para>
    /// </summary>
    public const string PowerWake = "power.wake";

    /// <summary>全部取值。</summary>
    public static readonly string[] All = [Notify, PowerShutdown, PowerWake];

    /// <summary>是否是「对教室机做物理动作」的动作（迟到就不该再执行）。</summary>
    public static bool IsPower(string? action) => action is PowerShutdown or PowerWake;

    /// <summary>判断取值是否合法（空视为 <see cref="Notify"/>，兼容老客户端）。</summary>
    public static bool IsValid(string? action) =>
        string.IsNullOrWhiteSpace(action) || All.Contains(action);
}

/// <summary>
/// 提醒的**触发方式**（#12）。
/// <para>「按时间」是默认（老数据的取值），由调度器按 <c>next_fire_at</c> 触发；
/// 「设备上线」由心跳里「离线 → 在线」的边沿触发——它的 <c>next_fire_at</c> 始终为空，
/// 因此天然不会被调度器选中（<c>GetDueRemindersAsync</c> 只取非空的）。</para>
/// </summary>
public static class ReminderTriggers
{
    /// <summary>按时间触发（默认）。</summary>
    public const string Time = "time";

    /// <summary>设备上线时触发：目标里的某台教室机从离线变为在线时执行一次。</summary>
    public const string DeviceOnline = "device_online";

    /// <summary>全部取值。</summary>
    public static readonly string[] All = [Time, DeviceOnline];

    /// <summary>取值是否合法（空视为按时间，兼容老客户端）。</summary>
    public static bool IsValid(string? value) => string.IsNullOrWhiteSpace(value) || All.Contains(value);

    /// <summary>是否属于「事件触发」（不听排程、不写 next_fire_at，靠事件发生执行）。</summary>
    public static bool IsEvent(string? value) =>
        string.Equals(value, DeviceOnline, StringComparison.OrdinalIgnoreCase);
}

/// <summary>定时提醒。</summary>
public sealed class ReminderDto
{
    /// <summary>提醒 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>提醒内容。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>投递方式，见 <see cref="ReminderChannels"/>。</summary>
    public string Channel { get; set; } = ReminderChannels.Screen;

    /// <summary>到点执行的动作，见 <see cref="ReminderActions"/>。</summary>
    public string Action { get; set; } = ReminderActions.Notify;

    /// <summary>触发方式，见 <see cref="ReminderTriggers"/>。</summary>
    public string TriggerKind { get; set; } = ReminderTriggers.Time;

    /// <summary>目标类型，见 <see cref="ReminderTargets"/>。</summary>
    public string TargetType { get; set; } = ReminderTargets.All;

    /// <summary>目标 ID（分组 / 设备）；<see cref="ReminderTargets.All"/> 时为空。</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>目标的展示名（服务端回填，便于列表直接显示）。</summary>
    public string TargetName { get; set; } = string.Empty;

    /// <summary>是否同时语音播报。</summary>
    public bool Speak { get; set; }

    /// <summary>是否启用。停用后不再触发，但保留配置与历史。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>重复规则，见 <see cref="ReminderRepeats"/>。</summary>
    public string RepeatKind { get; set; } = ReminderRepeats.Once;

    /// <summary>重复间隔：每 N 天 / 周 / 月。</summary>
    public int RepeatInterval { get; set; } = 1;

    /// <summary>每周重复时的星期几（1=周一 … 7=周日）。</summary>
    public List<int> RepeatWeekdays { get; set; } = [];

    /// <summary>每月重复时的日期（1-31）；0 表示沿用起始日期的号数。</summary>
    public int RepeatDayOfMonth { get; set; }

    /// <summary>触发时刻，形如 <c>08:00</c>（服务器本地时间）。</summary>
    public string FireTime { get; set; } = "08:00";

    /// <summary>生效起始日期，形如 <c>2026-09-27</c>。</summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>结束日期；留空表示长期有效。</summary>
    public string? EndDate { get; set; }

    /// <summary>下次触发时间（UTC）。</summary>
    public DateTimeOffset? NextFireAt { get; set; }

    /// <summary>最近一次触发时间（UTC）。</summary>
    public DateTimeOffset? LastFiredAt { get; set; }

    /// <summary>累计触发次数。</summary>
    public int FireCount { get; set; }

    /// <summary>归属账号 ID（提醒按用户隔离）。</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>归属账号用户名。</summary>
    public string OwnerName { get; set; } = string.Empty;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>新建或修改提醒。</summary>
public sealed class ReminderUpsertRequest
{
    /// <summary>标题。</summary>
    public string? Title { get; set; }

    /// <summary>提醒内容。</summary>
    public string? Content { get; set; }

    /// <summary>到点执行的动作，见 <see cref="ReminderActions"/>；留空 = 发通知。</summary>
    public string? Action { get; set; }

    /// <summary>
    /// 触发方式，见 <see cref="ReminderTriggers"/>；留空 = 按时间。
    /// <para>选「设备上线」时，排程相关字段（重复规则 / 日期 / 时间）会被忽略——它们对事件触发的提醒没有意义。</para>
    /// </summary>
    public string? TriggerKind { get; set; }

    /// <summary>目标类型。</summary>
    public string? TargetType { get; set; }

    /// <summary>目标 ID。</summary>
    public string? TargetId { get; set; }

    /// <summary>是否语音播报。</summary>
    public bool Speak { get; set; }

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>重复规则。</summary>
    public string? RepeatKind { get; set; }

    /// <summary>重复间隔。</summary>
    public int RepeatInterval { get; set; } = 1;

    /// <summary>每周重复的星期几集合。</summary>
    public List<int>? RepeatWeekdays { get; set; }

    /// <summary>每月重复的日期（1-31）；0 表示沿用起始日期。</summary>
    public int RepeatDayOfMonth { get; set; }

    /// <summary>触发时刻（HH:mm）。</summary>
    public string? FireTime { get; set; }

    /// <summary>生效起始日期（yyyy-MM-dd）。</summary>
    public string? StartDate { get; set; }

    /// <summary>结束日期（yyyy-MM-dd），可空。</summary>
    public string? EndDate { get; set; }
}

/// <summary>一次提醒触发记录。</summary>
public sealed class ReminderFireDto
{
    /// <summary>记录 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>提醒 ID。</summary>
    public string ReminderId { get; set; } = string.Empty;

    /// <summary>提醒标题（回填，便于历史列表直接显示）。</summary>
    public string ReminderTitle { get; set; } = string.Empty;

    /// <summary>这次执行的动作，见 <see cref="ReminderActions"/>。</summary>
    public string Action { get; set; } = ReminderActions.Notify;

    /// <summary>触发时间（UTC）。</summary>
    public DateTimeOffset FiredAt { get; set; }

    /// <summary>成功下发的设备数。</summary>
    public int Affected { get; set; }

    /// <summary>因离线跳过的设备数。</summary>
    public int Skipped { get; set; }

    /// <summary>补充说明（目标描述等）。</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>提醒总览（供列表页顶部统计）。</summary>
public sealed class ReminderSummaryDto
{
    /// <summary>提醒总数。</summary>
    public int Total { get; set; }

    /// <summary>启用中的数量。</summary>
    public int Enabled { get; set; }

    /// <summary>最近 24 小时触发次数。</summary>
    public int FiredLast24Hours { get; set; }

    /// <summary>下一次触发时间（UTC）。</summary>
    public DateTimeOffset? NextFireAt { get; set; }
}
