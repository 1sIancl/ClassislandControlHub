namespace IslandHub.Protocol;

/// <summary>
/// 「哪些宿主配置文件允许远程读写」的唯一判定处。
///
/// <para>
/// 为什么单独抽出来：主界面组件布局、自动化定义、主题、插件配置这些模块没有跨进程 API
/// （组件是 Avalonia 控件、规则集是宿主内部对象），所以走**文件级管理**——读原文、改、写回、重启生效。
/// 一旦放开写文件，路径就成了信任边界：指令可以从 API 直接发，界面层限制等于没有。
/// </para>
///
/// <para>
/// 因此这里做三件事，顺序不能变：**规范化 → 比前缀 → 查白名单**。
/// 只做字符串黑名单（查 <c>..</c>）是挡不住 <c>ComponentLayouts/../../x</c> 的。
/// </para>
/// </summary>
public static class ConfigPathPolicy
{
    /// <summary>
    /// 允许远程管理的配置目录（相对宿主配置目录）。
    ///
    /// <para>
    /// 刻意**不含**：<c>PluginsIndex</c>（几十 MB 的市场缓存，改了没意义）、
    /// <c>Management/Credentials.json</c>（凭据不该走远程通道）、<c>.bak</c>（回滚余地）、
    /// <c>Tutorial</c> / <c>ReadAnnouncements</c>（引导与公告状态）。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> WritableRoots =
        ["ComponentLayouts", "Automations", "Plugins", "Management", "Themes"];

    /// <summary>单份配置的大小上限（超过基本可以判定不是正常配置）。</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 把相对路径解析成真实路径，并校验它落在白名单内。
    /// </summary>
    /// <param name="configRoot">宿主配置目录（绝对路径）。</param>
    /// <param name="relative">请求里的相对路径，例如 <c>ComponentLayouts/Default.json</c>。</param>
    /// <param name="fullPath">解析成功时的绝对路径。</param>
    /// <param name="reason">解析失败时的中文原因（直接给管理员看）。</param>
    /// <returns>是否允许访问。</returns>
    public static bool TryResolve(string? configRoot, string? relative, out string fullPath, out string reason)
    {
        fullPath = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(configRoot))
        {
            reason = "拿不到宿主配置目录（宿主版本与该接口不匹配）。";
            return false;
        }

        var cleaned = (relative ?? string.Empty).Replace('\\', '/').Trim();
        if (cleaned.Length == 0)
        {
            reason = "载荷缺少 path 字段。";
            return false;
        }

        // 绝对路径与盘符一律不收：它们绕过下面的前缀校验。
        if (cleaned.StartsWith('/') || cleaned.Contains(':'))
        {
            reason = "只接受相对宿主配置目录的路径，不接受绝对路径。";
            return false;
        }

        // 备份文件不给碰：写坏了自己的回滚余地就没了。
        if (cleaned.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            reason = "备份文件（.bak）不允许远程读写。";
            return false;
        }

        string rootFull;
        string candidate;
        try
        {
            rootFull = Path.GetFullPath(configRoot);
            candidate = Path.GetFullPath(Path.Combine(rootFull, cleaned));
        }
        catch (Exception)
        {
            // 非法字符、超长路径等：一律当成非法请求。
            reason = "路径不合法。";
            return false;
        }

        // 规范化之后再比前缀：`ComponentLayouts/../../xxx` 在这一步会被挡下。
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            reason = "路径解析后落在配置目录之外，已拒绝。";
            return false;
        }

        var relativeToRoot = candidate[prefix.Length..].Replace('\\', '/');
        var firstSegment = relativeToRoot.Split('/')[0];

        var allowed = WritableRoots.Contains(firstSegment, StringComparer.OrdinalIgnoreCase)
            // 启用的主题是配置根目录下的单个文件，单独放行。
            || string.Equals(relativeToRoot, "EnabledThemes.json", StringComparison.OrdinalIgnoreCase);

        if (!allowed)
        {
            reason = $"「{firstSegment}」不在可远程管理的范围内（允许："
                + $"{string.Join('、', WritableRoots)}、EnabledThemes.json）。";
            return false;
        }

        // 凭据文件即使在白名单目录里也要单独拦下。
        if (relativeToRoot.EndsWith("Credentials.json", StringComparison.OrdinalIgnoreCase))
        {
            reason = "凭据文件不通过远程通道读写。";
            return false;
        }

        fullPath = candidate;
        return true;
    }
}
