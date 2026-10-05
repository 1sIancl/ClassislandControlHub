namespace ControlHub.Protocol.Dtos;

/// <summary>报表覆盖的时间范围（含首尾两天，按服务器本地日期）。</summary>
public sealed class ReportRangeDto
{
    /// <summary>起始日期（<c>yyyy-MM-dd</c>）。</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>结束日期（<c>yyyy-MM-dd</c>）。</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>跨度天数。</summary>
    public int Days { get; set; }
}

/// <summary>
/// 通用「名称 + 数字」桶：按日、按类型、按设备、按账号都用它，前端只要会渲染一种结构。
/// </summary>
public sealed class ReportBucketDto
{
    /// <summary>原始键（设备 ID / 动作码 / 日期等），供跳转或排序用。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>展示名（已经过中文化 / 截断处理）。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>主计数（含义随报表而定：条数、设备数、分钟数…）。</summary>
    public int Count { get; set; }

    /// <summary>补充计数（例如「失败数」），无补充时为 0。</summary>
    public int Extra { get; set; }

    /// <summary>比率（0~1，含义随报表而定）。</summary>
    public double Rate { get; set; }
}

/// <summary>远程指令执行报表（#65）：成功率、类型分布、失败去向。</summary>
public sealed class CommandReportDto
{
    public ReportRangeDto Range { get; set; } = new();

    /// <summary>时间范围内的指令总数。</summary>
    public int Total { get; set; }

    /// <summary>执行成功（<c>done</c>）。</summary>
    public int Done { get; set; }

    /// <summary>设备回报失败（<c>failed</c>）。</summary>
    public int Failed { get; set; }

    /// <summary>排队超时作废（<c>expired</c>）。</summary>
    public int Expired { get; set; }

    /// <summary>还没有结果（<c>pending</c> + <c>dispatched</c>）。</summary>
    public int Unfinished { get; set; }

    /// <summary>
    /// 成功率 = 成功 /（成功 + 失败 + 作废）。
    /// <para>分母**不含**未完成的：刚发出去还没回来的指令不该拉低成功率。</para>
    /// </summary>
    public double SuccessRate { get; set; }

    /// <summary>按日分布。</summary>
    public List<ReportBucketDto> Daily { get; set; } = [];

    /// <summary>按指令类型分布。</summary>
    public List<ReportBucketDto> ByKind { get; set; } = [];

    /// <summary>失败 / 作废最多的设备（<c>Extra</c> = 失败数）。</summary>
    public List<ReportBucketDto> ByDevice { get; set; } = [];

    /// <summary>失败原因（对 <c>output</c> 做的摘要归并）。</summary>
    public List<ReportBucketDto> Failures { get; set; } = [];
}

/// <summary>操作热点分析（#67）：谁在什么时候对什么做了什么。</summary>
public sealed class OperationReportDto
{
    public ReportRangeDto Range { get; set; } = new();

    /// <summary>时间范围内的审计条数。</summary>
    public int Total { get; set; }

    /// <summary>参与操作的不同账号数。</summary>
    public int ActorCount { get; set; }

    /// <summary>按日分布。</summary>
    public List<ReportBucketDto> Daily { get; set; } = [];

    /// <summary>按动作类别（<c>device</c> / <c>profile</c> / <c>account</c>…）。</summary>
    public List<ReportBucketDto> ByCategory { get; set; } = [];

    /// <summary>按账号。</summary>
    public List<ReportBucketDto> ByActor { get; set; } = [];

    /// <summary>最频繁的动作码。</summary>
    public List<ReportBucketDto> TopActions { get; set; } = [];

    /// <summary>被操作最多的对象。</summary>
    public List<ReportBucketDto> TopTargets { get; set; } = [];
}

/// <summary>
/// 设备在线率报表（#63）。
/// <para>数据来自每分钟的采样（<c>device_online_samples</c>），不是瞬时状态——
/// 服务没运行的时段不计入分母，所以「按采样轮次算」比「按 1440 分钟算」更贴近事实。</para>
/// </summary>
public sealed class OnlineReportDto
{
    public ReportRangeDto Range { get; set; } = new();

    /// <summary>参与统计的设备数（当前存在的设备）。</summary>
    public int DeviceCount { get; set; }

    /// <summary>时间范围内累计的采样轮次（每分钟一轮）。</summary>
    public int SampleCount { get; set; }

    /// <summary>整体在线率（0~1）。</summary>
    public double OverallRate { get; set; }

    /// <summary>
    /// 最近一次采样所在的日期（<c>yyyy-MM-dd</c>，用来看「统计还在跑吗」）。
    /// <para>刻意用**日期**而不是时刻：采样表里的时间维度本来就是「哪一天」，
    /// 硬凑成 <c>00:00:00</c> 的时刻会让人以为「采样发生在午夜」。</para>
    /// </summary>
    public string? LastSampledDate { get; set; }

    /// <summary>按日在线率。</summary>
    public List<ReportBucketDto> Daily { get; set; } = [];

    /// <summary>每台设备的在线率，**最低的排前面**（要盯的就是这些）。</summary>
    public List<ReportBucketDto> ByDevice { get; set; } = [];

    /// <summary>按分组（楼栋 / 楼层）聚合的在线率。</summary>
    public List<ReportBucketDto> ByGroup { get; set; } = [];
}

/// <summary>
/// 一次配置下发的最终覆盖情况（#64）。
/// <para>「跟上」的判定用**推送世代号**而不是配置版本号：定向推送不改变全局版本号，
/// 只递增目标设备的 <c>push_epoch</c>；只看 revision 的话，定向下发会被误判成「早就同步好了」。</para>
/// </summary>
public sealed class SyncPushReportDto
{
    /// <summary>批次 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>下发时的全局配置版本号。</summary>
    public long Revision { get; set; }

    /// <summary>下发范围（<c>all</c> / <c>group</c> / <c>device</c>）。</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>范围的中文说明（如「全部设备」「2 个分组」）。</summary>
    public string ScopeLabel { get; set; } = string.Empty;

    /// <summary>下发时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>发起人（取不到时为空）。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>随下发的提示消息。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>是否强制覆盖客户端本地内容。</summary>
    public bool Force { get; set; }

    /// <summary>目标设备数（下发时的快照）。</summary>
    public int TargetCount { get; set; }

    /// <summary>已经跟上的设备数。</summary>
    public int Synced { get; set; }

    /// <summary>在线但还没跟上。</summary>
    public int Waiting { get; set; }

    /// <summary>离线（还没拿到）。</summary>
    public int Offline { get; set; }

    /// <summary>下发后被停用 / 已删除的设备（不计入成功率）。</summary>
    public int Excluded { get; set; }

    /// <summary>
    /// 可统计的设备数（= 已跟上 + 在线未跟上 + 离线），也就是成功率的**分母**。
    /// <para>为 0 时说明这一批的目标全被停用或删掉了，成功率**不适用**——
    /// 界面必须显示「—」而不是 0%：那会被读成「全部失败」，与事实正好相反。</para>
    /// </summary>
    public int Countable { get; set; }

    /// <summary>
    /// 覆盖率 = 已跟上 / <see cref="Countable"/>。
    /// <para>分母**不含**下发后被停用或删掉的设备——它们收不到配置是意料之中的，不该拉低成功率。</para>
    /// </summary>
    public double SuccessRate { get; set; }

    /// <summary>已跟上设备的平均耗时（秒，取不到为空）。</summary>
    public double? AverageSeconds { get; set; }
}

/// <summary>配置同步成功率报表（#64）。</summary>
public sealed class SyncReportDto
{
    public ReportRangeDto Range { get; set; } = new();

    /// <summary>范围内的下发批次数。</summary>
    public int PushCount { get; set; }

    /// <summary>这些批次的目标设备总数（按批次累加）。</summary>
    public int TotalTargets { get; set; }

    /// <summary>其中已跟上的总数。</summary>
    public int TotalSynced { get; set; }

    /// <summary>整体覆盖率。</summary>
    public double OverallRate { get; set; }

    /// <summary>100% 覆盖的批次数。</summary>
    public int FullySyncedPushes { get; set; }

    /// <summary>按日下发次数。</summary>
    public List<ReportBucketDto> Daily { get; set; } = [];

    /// <summary>各批次明细（新的在前）。</summary>
    public List<SyncPushReportDto> Pushes { get; set; } = [];

    /// <summary>反复没跟上的设备（<c>Count</c> = 未跟上的批次数）。</summary>
    public List<ReportBucketDto> StuckDevices { get; set; } = [];
}
