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
