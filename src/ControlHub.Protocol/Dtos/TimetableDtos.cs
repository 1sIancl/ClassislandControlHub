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
