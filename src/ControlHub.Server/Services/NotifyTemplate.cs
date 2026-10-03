using ControlHub.Server.Data;

namespace ControlHub.Server.Services;

/// <summary>
/// 通知正文里的模板变量（#43）：下达通知时**按设备**替换，让一条通知面向多间教室也不出错。
/// <para>支持的变量：<c>{教室名}</c> / <c>{设备名}</c>、<c>{机器名}</c>、<c>{分组}</c>、<c>{IP}</c>、
/// <c>{时间}</c>、<c>{日期}</c>。</para>
/// <para>认不出的花括号**原样保留**：宁可正文里留一个可见的 <c>{班级}</c> 让人发现写错了，
/// 也不要因为替换规则去猜而把内容吃掉。</para>
/// </summary>
public static class NotifyTemplate
{
    /// <summary>把变量替换成这台设备的实际值。</summary>
    /// <param name="text">原始文本（标题或正文）。</param>
    /// <param name="device">目标设备。</param>
    /// <param name="groupName">设备所属分组名（可为空）。</param>
    /// <param name="now">发送时刻（用于 <c>{时间}</c> 与 <c>{日期}</c>，按服务器本地时区呈现）。</param>
    public static string Apply(string? text, DeviceRow device, string? groupName, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var local = now.ToLocalTime();
        return text
            .Replace("{教室名}", device.Name, StringComparison.Ordinal)
            .Replace("{设备名}", device.Name, StringComparison.Ordinal)
            .Replace("{机器名}", device.MachineName ?? string.Empty, StringComparison.Ordinal)
            .Replace("{分组}", groupName ?? string.Empty, StringComparison.Ordinal)
            .Replace("{IP}", device.IpAddress ?? string.Empty, StringComparison.Ordinal)
            .Replace("{时间}", local.ToString("HH:mm"), StringComparison.Ordinal)
            .Replace("{日期}", local.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
    }

    /// <summary>给界面与文档用的变量清单（一处维护，避免两边写得不一样）。</summary>
    public static IReadOnlyList<(string Token, string Description)> Variables { get; } =
    [
        ("{教室名}", "设备显示名（等同 {设备名}）"),
        ("{机器名}", "教室机的机器名"),
        ("{分组}", "设备所属分组（楼栋 / 楼层）"),
        ("{IP}", "设备最近一次上报的内网 IP"),
        ("{时间}", "发送时的时分（HH:mm）"),
        ("{日期}", "发送时的日期（yyyy-MM-dd）"),
    ];
}
