namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 全局搜索结果（#41）。
/// <para>一个关键字同时查设备 / 分组 / 档案 / 账号，每项都带一条**可直接跳转的 hash**，
/// 前端不需要知道各类实体分别怎么定位。</para>
/// </summary>
public sealed class SearchResultDto
{
    /// <summary>回显用户输入的关键字（前端可能做高亮）。</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>命中项，已按「前缀命中 &gt; 标题命中 &gt; 其它字段命中」排好序。</summary>
    public List<SearchItemDto> Items { get; set; } = [];
}

/// <summary>一条搜索结果。</summary>
public sealed class SearchItemDto
{
    /// <summary>类别：<c>device</c> / <c>group</c> / <c>profile</c> / <c>account</c>。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>主标题（设备名 / 档案名 / 账号显示名）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>副标题：给人判断「是不是我要找的那个」的信息。</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>跳转地址（形如 <c>#/devices?kw=高一</c>），前端直接赋给 location.hash。</summary>
    public string Hash { get; set; } = string.Empty;
}
