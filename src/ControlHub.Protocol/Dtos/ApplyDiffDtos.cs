namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 「本次下发会改什么」的差异预览：对比设备**当前实际应用**的档案与**即将下发**的档案。
/// <para>用于下发前的二次确认，避免管理员在不知情的情况下把整间教室的课表换掉。</para>
/// </summary>
public sealed class ApplyDiffDto
{
    /// <summary>是否存在可对比的基线。设备从未成功应用过配置时为 <c>false</c>（此时列出「将首次下发什么」）。</summary>
    public bool HasBaseline { get; set; }

    /// <summary>设备当前实际应用的档案名（无基线时为空）。</summary>
    public string? AppliedProfileName { get; set; }

    /// <summary>设备当前实际应用的档案版本号。</summary>
    public long AppliedRevision { get; set; }

    /// <summary>本次将下发的档案名。</summary>
    public string? TargetProfileName { get; set; }

    /// <summary>本次将下发的档案版本号。</summary>
    public long TargetRevision { get; set; }

    /// <summary>按内容分区（时间表 / 课表 / 科目 / 自定义设置）列出的差异。</summary>
    public List<ApplyDiffSectionDto> Sections { get; set; } = [];

    /// <summary>整体结论说明。</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>某个内容分区的差异清单。</summary>
public sealed class ApplyDiffSectionDto
{
    /// <summary>分区名：时间表 / 课表 / 科目 / 自定义设置。</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>新增项（描述文本）。</summary>
    public List<string> Added { get; set; } = [];

    /// <summary>移除项。</summary>
    public List<string> Removed { get; set; } = [];

    /// <summary>内容有变化的项。</summary>
    public List<string> Changed { get; set; } = [];

    /// <summary>该分区的差异总数（便于管理端一眼看出哪个分区变化最大）。</summary>
    public int Total => Added.Count + Removed.Count + Changed.Count;
}
