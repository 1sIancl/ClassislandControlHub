using System.Net;
using System.Net.Sockets;

namespace ControlHub.Server.Services;

/// <summary>
/// 轻量 SNTP 客户端（RFC 4330 单播模式），用标准 NTP 协议（UDP 123）向时间服务器授时。
/// <para>
/// 采用与 B 端相同的四时间戳算法抵消网络往返时延：
/// <c>offset = ((T2 − T1) + (T3 − T4)) / 2</c>，其中
/// T1 客户端发送时刻、T2 服务器接收时刻、T3 服务器发送时刻、T4 客户端接收时刻。
/// </para>
/// </summary>
public sealed class NtpClient
{
    /// <summary>NTP 服务器默认端口。</summary>
    private const int NtpPort = 123;

    /// <summary>单次查询超时。</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 查询 NTP 服务器，返回「服务器时间 − 本机系统时间」的偏移（正值表示本机偏慢）。
    /// </summary>
    /// <param name="server">NTP 服务器主机名或 IP。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SocketException">网络不可达或超时。</exception>
    /// <exception cref="InvalidDataException">服务器返回了无效响应。</exception>
    public async Task<TimeSpan> QueryOffsetAsync(string server, CancellationToken cancellationToken = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(server, NtpPort);

        // 构造 48 字节 NTP 请求报文。
        var request = new byte[48];
        request[0] = 0x1B; // LI=0, VN=3, Mode=3（client）

        var t1 = DateTime.UtcNow;
        NtpTimestamp.WriteUInt64BigEndian(request, 40, NtpTimestamp.ToUInt64(t1)); // Transmit Timestamp = T1

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QueryTimeout);

        await udp.SendAsync(request, request.Length);
        var result = await udp.ReceiveAsync(timeout.Token);
        var t4 = DateTime.UtcNow;

        if (result.Buffer.Length < 48)
        {
            throw new InvalidDataException($"NTP 响应过短（{result.Buffer.Length} 字节）。");
        }

        // 校验：Mode 应为 server(4)/broadcast(5)，Stratum 应为 1~15（0 表示 kiss-of-death）。
        var mode = result.Buffer[0] & 0x07;
        var stratum = result.Buffer[1];
        if (mode is not (4 or 5))
        {
            throw new InvalidDataException($"NTP 响应 Mode 异常（{mode}）。");
        }

        if (stratum is 0 or > 15)
        {
            throw new InvalidDataException($"NTP 服务器返回无效 Stratum（{stratum}）。");
        }

        var t2 = NtpTimestamp.FromUInt64(NtpTimestamp.ReadUInt64BigEndian(result.Buffer, 32)); // Receive Timestamp
        var t3 = NtpTimestamp.FromUInt64(NtpTimestamp.ReadUInt64BigEndian(result.Buffer, 40)); // Transmit Timestamp

        // offset = ((T2 − T1) + (T3 − T4)) / 2
        return TimeSpan.FromTicks(((t2 - t1) + (t3 - t4)).Ticks / 2);
    }
}
