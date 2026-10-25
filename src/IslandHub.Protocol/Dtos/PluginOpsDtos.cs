namespace IslandHub.Protocol.Dtos;

/// <summary>
/// 插件的运营标记（#104）。
/// <para>
/// 与市场索引分开：索引每次同步整表替换，而这些是**人写的**——"这个插件我们推荐"、
/// "这个已经弃用，别再用"、"上次验证还能跑是几号"。同步不该把这些冲掉。
/// </para>
/// </summary>
public sealed class PluginOpsDto
{
    /// <summary>插件 ID。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>运营状态：<c>normal</c> / <c>recommended</c> / <c>deprecated</c>。</summary>
    public string Status { get; set; } = "normal";

    /// <summary>运营备注。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>上次人工验证时间。</summary>
    public string VerifiedAt { get; set; } = string.Empty;

    /// <summary>最后改动人。</summary>
    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>保存运营标记的请求体。</summary>
public sealed class PluginOpsRequest
{
    /// <summary>运营状态；传 <c>normal</c> 且无备注即清除标记。</summary>
    public string Status { get; set; } = "normal";

    /// <summary>运营备注。</summary>
    public string? Note { get; set; }
}

/// <summary>
/// 「上架自检」（#104）：把审核清单里能自动判断的部分跑一遍。
/// <para>
/// 目的是让"要提交到官方市场吗"这件事有个客观依据：字段缺哪几个、缓存有没有、README 有没有，
/// 一次说清，而不是提交被打回才知道。
/// </para>
/// </summary>
public sealed class PluginSubmissionCheckDto
{
    /// <summary>插件 ID。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>是否已具备提交条件（所有必填项都通过）。</summary>
    public bool Ready { get; set; }

    /// <summary>逐项检查结果。</summary>
    public List<PluginSubmissionItemDto> Items { get; set; } = [];
}

/// <summary>自检的单项结果。</summary>
public sealed class PluginSubmissionItemDto
{
    /// <summary>检查项名称（直接给界面显示）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>是否通过。</summary>
    public bool Ok { get; set; }

    /// <summary>说明：缺什么、当前值是什么。</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>是否必填（不通过就不能提交）。</summary>
    public bool Required { get; set; } = true;
}
