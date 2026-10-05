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
