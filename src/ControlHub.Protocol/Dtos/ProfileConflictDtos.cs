namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 配置档案的冲突检测结果（#44）。
/// <para>课表本身不会报错——它只是一堆 ID 引用与时间；排错了要到上课那天才会发现。
/// 这份报告把「引用坏了」「时间撞了」「同一个老师被排到两个班」提前暴露出来。</para>
/// </summary>
public sealed class ProfileConflictReportDto
{
    /// <summary>检查时间。</summary>
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>参与检查的档案数（界面据此说明「到底检查了什么」）。</summary>
    public int ProfileCount { get; set; }

    /// <summary>参与检查的课表数（只算启用的）。</summary>
    public int ClassPlanCount { get; set; }

    /// <summary>发现的问题，阻塞项在前。</summary>
    public List<ProfileConflictDto> Conflicts { get; set; } = [];

    /// <summary>问题过多被截断（异常数据不该把响应撑爆）。</summary>
    public bool Truncated { get; set; }
}

/// <summary>一条冲突。</summary>
public sealed class ProfileConflictDto
{
    /// <summary>
    /// 问题类型：<c>missing-subject</c>（课表引用了不存在的科目）、
    /// <c>missing-timelayout</c>（引用了不存在的时间表）、
    /// <c>slot-out-of-range</c>（节次超出时间表范围）、
    /// <c>duplicate-classplan</c>（同一时间有多个课表同时生效）、
    /// <c>timelayout-overlap</c>（时间表内两节时间重叠）、
    /// <c>teacher-overlap</c>（同一教师同一时间被排到多个档案）。
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// 是否**必然**导致课表不对。<c>true</c> = 引用坏了这类硬错误；
    /// <c>false</c> = 需要人工确认的提醒（教师冲突可能是同名老师，也可能是合班上课）。
    /// </summary>
    public bool Blocking { get; set; }

    /// <summary>人话描述，含具体位置（如「周三 第 2 节（08:55-09:40）」）。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>主要涉及的档案（界面据此提供跳转）。</summary>
    public string? ProfileId { get; set; }

    /// <summary>主要涉及的档案名称。</summary>
    public string? ProfileName { get; set; }
}
