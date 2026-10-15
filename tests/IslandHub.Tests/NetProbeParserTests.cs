using IslandHub.Protocol;

namespace IslandHub.Tests;

/// <summary>
/// 网络探测 / 磁盘信息的解析（#107）。
/// <para>为什么值得单独测：这些解析要面对**不同语言的 Windows 输出**（中文系统是「时间=12ms」、
/// 英文是「time=12ms」），而教室里真机跑一次的成本远高于写几个断言。这里把常见形态都钉住，
/// 免得改完解析器要一台台机器去试。</para>
/// </summary>
public sealed class NetProbeParserTests
{
    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("school.example.edu", true)]
    [InlineData("my-host_1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("[fe80::1]", true)]
    [InlineData("10.0.0.1 && shutdown /r", false)]
    [InlineData("10.0.0.1; rm -rf /", false)]
    [InlineData("10.0.0.1 | more", false)]
    [InlineData("10.0.0.1 > c:\\x.txt", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 主机名只放行安全字符(string? host, bool expected)
    {
        // 这个值最终会成为系统命令的参数——虽然执行侧用 ArgumentList 不走 shell，
        // 但把特殊符号挡在门外才是根本。
        Assert.Equal(expected, NetProbeParser.IsSafeHost(host));
    }

    [Fact]
    public void 解析中文Windows的Ping输出()
    {
        const string output = """
            正在 Ping 10.0.0.5 具有 32 字节的数据:
            来自 10.0.0.5 的回复: 字节=32 时间=1ms TTL=64
            来自 10.0.0.5 的回复: 字节=32 时间=3ms TTL=64
            来自 10.0.0.5 的回复: 字节=32 时间<1ms TTL=64
            来自 10.0.0.5 的回复: 字节=32 时间=5ms TTL=64

            10.0.0.5 的 Ping 统计信息:
                数据包: 已发送 = 4，已接收 = 4，丢失 = 0 (0% 丢失)，
            往返行程的估计时间(以毫秒为单位):
                最短 = 1ms，最长 = 5ms，平均 = 2ms
            """;

        var summary = NetProbeParser.ParsePing(output, 4);

        Assert.Equal(4, summary.Sent);
        Assert.Equal(4, summary.Received);
        Assert.Equal(0, summary.LossPercent);
        Assert.False(summary.AllLost);
        // `时间<1ms` 按 1ms 记（比记 0 更诚实），所以最短是 1 而不是 0。
        Assert.Equal(1, summary.MinMs);
        Assert.Equal(5, summary.MaxMs);
        Assert.NotNull(summary.AvgMs);
        Assert.Contains("收到 4 个", summary.Describe());
    }

    [Fact]
    public void 解析英文Windows的Ping输出与丢包()
    {
        const string output = """
            Pinging 10.0.0.9 with 32 bytes of data:
            Reply from 10.0.0.9: bytes=32 time=12ms TTL=64
            Request timed out.
            Reply from 10.0.0.9: bytes=32 time=14ms TTL=64
            Request timed out.
            """;

        var summary = NetProbeParser.ParsePing(output, 4);

        Assert.Equal(4, summary.Sent);
        Assert.Equal(2, summary.Received);
        Assert.Equal(50, summary.LossPercent);
        Assert.Equal(12, summary.MinMs);
        Assert.Equal(14, summary.MaxMs);
        Assert.Contains("丢包 50%", summary.Describe());
    }

    [Fact]
    public void 全部丢包时给出可操作的结论()
    {
        var summary = NetProbeParser.ParsePing("Request timed out.\nRequest timed out.", 2);

        Assert.True(summary.AllLost);
        Assert.Equal(100, summary.LossPercent);
        Assert.Null(summary.AvgMs);
        // 文案要能直接照着查，而不是只说「无数据」。
        Assert.Contains("100% 丢包", summary.Describe());
    }

    [Fact]
    public void 数出路由追踪的跳数而忽略表头()
    {
        const string output = """
            通过最多 16 个跃点跟踪到 8.8.8.8 的路由

              1     1 ms     1 ms     1 ms  192.168.1.1
              2     4 ms     3 ms     3 ms  10.10.0.1
              3     *        *        *     请求超时。
              4    22 ms    21 ms    22 ms  8.8.8.8

            跟踪完成。
            """;

        // 表头与「跟踪完成」都不算跳数；`*` 那一跳仍然算一跳（它确实占了一行）。
        Assert.Equal(4, NetProbeParser.CountTracerouteHops(output));
        Assert.Equal(0, NetProbeParser.CountTracerouteHops(string.Empty));
        Assert.Equal(0, NetProbeParser.CountTracerouteHops(null));
    }

    [Fact]
    public void 磁盘空间与字节数的格式化()
    {
        var line = NetProbeParser.DescribeDisk("C:", 256L * 1024 * 1024 * 1024, 64L * 1024 * 1024 * 1024);

        Assert.Contains("C:", line);
        Assert.Contains("可用 64 GB", line);
        Assert.Contains("共 256 GB", line);
        Assert.Contains("25%", line);

        Assert.Equal("1 KB", NetProbeParser.FormatBytes(1024));
        Assert.Equal("1.5 GB", NetProbeParser.FormatBytes((long)(1.5 * 1024 * 1024 * 1024)));
        // 容量读不到时不该抛，也不该显示成 0。
        Assert.Contains("容量未知", NetProbeParser.DescribeDisk("D:", 0, 0));
    }
}
