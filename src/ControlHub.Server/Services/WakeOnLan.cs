using System.Net;
using System.Net.Sockets;

namespace ControlHub.Server.Services;

/// <summary>
/// 远程唤醒（Wake-on-LAN，路线图 #20）。
/// </summary>
/// <remarks>
/// <para>为什么「开机」必须由 A 端做：**教室机断电时插件也停了**，它叫不醒自己。
/// 只能由外部往局域网发一帧「魔术包」，让网卡在低功耗状态下认出自己的 MAC 并唤醒主机。</para>
/// <para>⚠️ 魔术包只在**同一个广播域**内有效。学校里服务器和教室常在不同的 VLAN，
/// 那种情况下要么在网络设备上开 WoL 中继，要么把服务器放到教室所在网段——
/// 这件事不能靠软件绕过，所以失败时会把原因说清楚，而不是让管理员反复点。</para>
/// </remarks>
public static class WakeOnLan
{
    /// <summary>魔术包长度：6 字节 <c>0xFF</c> + 目标 MAC 重复 16 次。</summary>
    public const int PacketLength = 6 + (6 * 16);

    /// <summary>
    /// 发送端口。9 是标准端口，但**部分网卡只监听 7**，两个都发一次的成本可忽略。
    /// </summary>
    private static readonly int[] Ports = [9, 7];

    /// <summary>解析 MAC（接受 <c>AA-BB-CC-DD-EE-FF</c>、<c>AA:BB:..</c> 或连写形式）。</summary>
    public static bool TryParseMac(string? text, out byte[] mac)
    {
        mac = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hex = new string([.. text.Where(char.IsAsciiHexDigit)]);
        if (hex.Length != 12)
        {
            return false;
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
            {
                return false;
            }
        }

        // 全零地址发出去必然是无效包，早报错比"发了但没反应"强。
        if (bytes.All(b => b == 0))
        {
            return false;
        }

        mac = bytes;
        return true;
    }

    /// <summary>构造魔术包。</summary>
    public static byte[] Build(byte[] mac)
    {
        if (mac.Length != 6)
        {
            throw new ArgumentException("MAC 必须是 6 字节。", nameof(mac));
        }

        var packet = new byte[PacketLength];
        for (var i = 0; i < 6; i++)
        {
            packet[i] = 0xFF;
        }

        for (var repeat = 0; repeat < 16; repeat++)
        {
            Array.Copy(mac, 0, packet, 6 + (repeat * 6), 6);
        }

        return packet;
    }

    /// <summary>
    /// 给一台机器发魔术包；返回失败原因（成功时为 <c>null</c>）。
    /// </summary>
    /// <param name="macText">目标网卡地址（设备心跳上报的那个）。</param>
    /// <param name="ipAddress">设备上报的 IP，用于推算**定向广播地址**（比 255.255.255.255 更不容易被设备丢弃）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<string?> SendAsync(string? macText, string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseMac(macText, out var mac))
        {
            return "网卡地址不可用。";
        }

        var packet = Build(mac);
        var targets = BroadcastTargets(ipAddress);
        string? lastError = null;
        var sent = 0;

        foreach (var target in targets)
        {
            foreach (var port in Ports)
            {
                try
                {
                    using var client = new UdpClient { EnableBroadcast = true };
                    await client.SendAsync(packet, packet.Length, new IPEndPoint(target, port));
                    sent++;
                }
                catch (Exception ex)
                {
                    // 一个目标失败不代表全失败（例如没有到 255.255.255.255 的路由），
                    // 记下原因继续试下一个，最后只要有一个成功就算成功。
                    lastError = $"{target}:{port} → {ex.Message}";
                }
            }
        }

        return sent > 0 ? null : lastError ?? "没有可用的广播目标。";
    }

    /// <summary>
    /// 广播目标：受限广播地址 <c>255.255.255.255</c>，外加按设备 IP 推算的 /24 定向广播。
    /// </summary>
    /// <remarks>
    /// <para>为什么要两个：<c>255.255.255.255</c> 不保证被所有网卡/交换机转发，
    /// 而按设备 IP 推算的 <c>x.y.z.255</c> 在绝大多数校园网里都能直达。</para>
    /// <para>用 /24 是**刻意的近似**：真实掩码服务端看不到（心跳只报 IP）。
    /// 猜错时多发的那个包不会有害，而猜对能显著提高叫醒成功率。</para>
    /// </remarks>
    private static List<IPAddress> BroadcastTargets(string? ipAddress)
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };

        if (IPAddress.TryParse(ipAddress, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            targets.Add(new IPAddress([bytes[0], bytes[1], bytes[2], 255]));
        }

        return targets;
    }
}
