using System.Globalization;
using System.Text.RegularExpressions;

namespace IslandHub.Protocol;

/// <summary>
/// 网络探测结果与磁盘信息的**纯解析**（#107）。
/// <para><b>为什么把解析放在 Protocol 里</b>：解析逻辑是整条诊断链路里最值得测的一段
/// （它要面对不同语言的 Windows 输出），而它本身不需要任何宿主 / IO；放在共享工程里，
/// 单元测试就能直接钉住它——教室里真机跑一次的成本远高于写几个断言。</para>
/// <para><b>为什么按「每行回复」而不是「汇总行」解析 Ping</b>：汇总行的措辞随系统语言变
/// （「平均 = 12ms」/「Average = 12ms」），而每行回复里的 <c>time=12ms</c> /
/// <c>时间=12ms</c> 里的数字是稳定的。所以自己数回复条数、自己算最小 / 平均 / 最大。</para>
/// </summary>
public static class NetProbeParser
{
    /// <summary>主机名允许的字符集：字母数字、点、短横、下划线、冒号（IPv6）与方括号。</summary>
    private static readonly Regex HostPattern = new(@"^[A-Za-z0-9._:\-\[\]]{1,253}$", RegexOptions.Compiled);

    /// <summary>每行回复里的耗时（中英文通吃）：<c>time=12ms</c> / <c>时间&lt;1ms</c>。</summary>
    private static readonly Regex ReplyTimePattern =
        new(@"time\s*[=<]\s*(\d+)\s*ms|时间\s*[=<]\s*(\d+)\s*ms", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>路由追踪的一行：行首是跃点序号（<c>  1     1 ms ...</c>）。</summary>
    private static readonly Regex HopPattern = new(@"^\s*(\d{1,3})\s+\S", RegexOptions.Compiled);

    /// <summary>
    /// 主机是否可安全地当成命令参数。
    /// <para>挡住空格、引号、<c>&amp;</c>、<c>|</c>、<c>;</c>、重定向符等——即便执行侧已经
    /// 用 <c>ArgumentList</c> 不走 shell，也不该让这类字符串进到命令行。</para>
    /// </summary>
    public static bool IsSafeHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && HostPattern.IsMatch(host.Trim());

    /// <summary>Ping 的解析结果。</summary>
    public sealed class PingSummary
    {
        /// <summary>发出的次数。</summary>
        public int Sent { get; init; }

        /// <summary>收到回复的次数（按回复行数）。</summary>
        public int Received { get; init; }

        /// <summary>丢包率（0~100）。</summary>
        public int LossPercent { get; init; }

        /// <summary>最小 / 平均 / 最大往返时延（毫秒）；一次都没收到时为 <c>null</c>。</summary>
        public double? MinMs { get; init; }

        /// <summary>平均往返时延（毫秒）。</summary>
        public double? AvgMs { get; init; }

        /// <summary>最大往返时延（毫秒）。</summary>
        public double? MaxMs { get; init; }

        /// <summary>是否全部丢失（这样报出来比「平均 null」清楚）。</summary>
        public bool AllLost => Received == 0;

        /// <summary>给管理员看的一行结论。</summary>
        public string Describe()
        {
            if (AllLost)
            {
                return $"发出 {Sent} 个包，一个都没回来（100% 丢包）——先确认这台机器是否在线、网线 / 网络是否通。";
            }

            var rtt = MinMs == MaxMs
                ? $"约 {AvgMs:0.#} ms"
                : $"{MinMs:0.#}~{MaxMs:0.#} ms（平均 {AvgMs:0.#} ms）";
            return $"发出 {Sent} 个包，收到 {Received} 个（丢包 {LossPercent}%），往返 {rtt}。";
        }
    }

    /// <summary>解析 Ping 输出。<paramref name="sent"/> 是**我们要求发几个**（汇总行不可靠）。</summary>
    public static PingSummary ParsePing(string? output, int sent)
    {
        var text = output ?? string.Empty;
        var times = new List<double>();
        foreach (Match match in ReplyTimePattern.Matches(text))
        {
            // 两组交替出现（英文 / 中文各一组），取中的那一组。
            var raw = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (double.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            {
                // `time<1ms` 这类：按 1ms 记，比记 0 更诚实。
                times.Add(ms <= 0 ? 1 : ms);
            }
        }

        var safeSent = Math.Max(1, sent);
        var received = Math.Min(safeSent, times.Count);
        return new PingSummary
        {
            Sent = safeSent,
            Received = received,
            LossPercent = (int)Math.Round((safeSent - received) * 100.0 / safeSent),
            MinMs = times.Count > 0 ? Math.Round(times.Min(), 1) : null,
            AvgMs = times.Count > 0 ? Math.Round(times.Average(), 1) : null,
            MaxMs = times.Count > 0 ? Math.Round(times.Max(), 1) : null,
        };
    }

    /// <summary>
    /// 数出路由追踪走了几跳。
    /// <para>只认「行首是数字」的行——这样「通过最多 30 个跃点跟踪…」这类表头与各语言的提示都不会被算进去。</para>
    /// </summary>
    public static int CountTracerouteHops(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return 0;
        }

        var hops = 0;
        foreach (var line in output.Split('\n'))
        {
            if (HopPattern.IsMatch(line.TrimEnd('\r')))
            {
                hops++;
            }
        }

        return hops;
    }

    /// <summary>把一个分区格式化成一行（磁盘空间，单位自适应）。</summary>
    public static string DescribeDisk(string name, long totalBytes, long freeBytes)
    {
        if (totalBytes <= 0)
        {
            return $"{name}：容量未知";
        }

        var freePercent = (int)Math.Round(freeBytes * 100.0 / totalBytes);
        return $"{name}：可用 {FormatBytes(freeBytes)} / 共 {FormatBytes(totalBytes)}（剩余 {freePercent}%）";
    }

    /// <summary>把字节数格式化成人看的单位。</summary>
    public static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
