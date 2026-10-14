namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 课表模板（#9）：把一套排好的「天 × 节次」网格存下来，之后可套用到别的档案。
/// <para><b>网格里存的是科目名称，不是科目 ID</b>：科目 ID 是档案内的 GUID，跨档案必然对不上，
/// 那样模板就只能在同一个档案里自娱自乐。按名称匹配后，套用时找不到的科目会被跳过并计数，
/// 而不是悄悄塞进一个无效 ID。</para>
/// </summary>
public sealed class TimetableTemplateDto
{
    /// <summary>模板 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>模板名称（唯一，不区分 ASCII 大小写）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>网格：键为星期（<c>"0"</c>=周日 … <c>"6"</c>=周六），值为各节次的科目名称（空串或 null 表示没课）。</summary>
    public Dictionary<string, List<string?>> Grid { get; set; } = [];

    /// <summary>模板包含的节次数（套用时用来提示「目标时间表节数不一致」）。</summary>
    public int PeriodCount { get; set; }

    /// <summary>创建者。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>新建课表模板的请求。</summary>
public sealed class TimetableTemplateRequest
{
    /// <summary>模板名称（必填，最长 32 字）。</summary>
    public string? Name { get; set; }

    /// <summary>网格（键为星期，值为各节次科目名称）。</summary>
    public Dictionary<string, List<string?>>? Grid { get; set; }

    /// <summary>节次数。</summary>
    public int PeriodCount { get; set; }
}

/// <summary>把课表模板套用到某个档案的请求（#9 的「模板套用」）。</summary>
public sealed class TimetableTemplateApplyRequest
{
    /// <summary>目标档案 ID。</summary>
    public string ProfileId { get; set; } = string.Empty;
}

/// <summary>套用结果（界面据此告诉管理员「改了什么、什么没套上、为什么」）。</summary>
public sealed class TimetableTemplateApplyResultDto
{
    /// <summary>目标档案 ID。</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>目标档案名。</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>套用后的新版本号（档案内容变了就会 +1）。</summary>
    public long Revision { get; set; }

    /// <summary>被改写的课表名。</summary>
    public List<string> UpdatedPlans { get; set; } = [];

    /// <summary>被套用的天（0=周日 … 6=周六）。</summary>
    public List<int> UpdatedDays { get; set; } = [];

    /// <summary>因「一张课表覆盖多天、而模板里这几天不一致」被跳过的课表名。</summary>
    public List<string> ConflictedPlans { get; set; } = [];

    /// <summary>模板里有、但目标档案没有同名科目的名称（这几节**保留原科目**）。</summary>
    public List<string> MissingSubjects { get; set; } = [];

    /// <summary>模板里有课、但没有课表覆盖的天。</summary>
    public List<int> UncoveredDays { get; set; } = [];

    /// <summary>节数不一致的提示。</summary>
    public List<string> PeriodMismatches { get; set; } = [];
}
