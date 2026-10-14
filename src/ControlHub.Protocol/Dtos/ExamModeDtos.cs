namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 考试模式的发起请求（`#21`）。
/// <para>考试模式把选定教室的档案**临时**换成考试档案，考完一键还原成各自原来的档案。</para>
/// </summary>
public sealed class ExamModeStartRequest
{
    /// <summary>要切换到的档案（考试档案）ID。</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>范围：<c>all</c>（全部教室）/ <c>group</c>（某个分组）/ <c>device</c>（指定教室）。</summary>
    public string Scope { get; set; } = "all";

    /// <summary>范围非 <c>all</c> 时的目标 ID 列表（分组 ID 或教室 ID）。</summary>
    public List<string> TargetIds { get; set; } = [];

    /// <summary>随通知发给教室端的说明（默认「考试进行中，请勿操作教室设备」）。</summary>
    public string? Message { get; set; }

    /// <summary>是否顺便给教室端推一条屏幕提醒（默认推）。</summary>
    public bool Notify { get; set; } = true;
}

/// <summary>考试模式的状态。</summary>
public sealed class ExamModeDto
{
    /// <summary>是否有正在进行的考试模式。</summary>
    public bool Active { get; set; }

    /// <summary>当前（或最近一次）的考试档案 ID。</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>考试档案名（界面直接显示，省一次查档案）。</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>说明文案。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>开始时间。</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>谁开始的。</summary>
    public string StartedBy { get; set; } = string.Empty;

    /// <summary>目标教室数量。</summary>
    public int TargetCount { get; set; }

    /// <summary>目标教室明细（含各自的「原档案名」，方便事后核对还原是否合理）。</summary>
    public List<ExamModeTargetDto> Targets { get; set; } = [];
}

/// <summary>考试模式里的一个目标教室。</summary>
public sealed class ExamModeTargetDto
{
    /// <summary>教室 ID。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>教室名。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>这台教室原来用的档案 ID；空表示它当时用分组默认档案。</summary>
    public string PreviousProfileId { get; set; } = string.Empty;

    /// <summary>原来那个档案的名字（查不到时为空）。</summary>
    public string PreviousProfileName { get; set; } = string.Empty;
}
