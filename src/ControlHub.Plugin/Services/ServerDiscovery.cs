using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using Microsoft.Extensions.Logging;

namespace ControlHub.Plugin.Services;

/// <summary>
/// 局域网服务器自动发现。
/// <para>
/// 向广播地址发送 <see cref="DiscoveryProbe"/>，收集数秒内回应的
/// <see cref="DiscoveryAnnounce"/>，从而让用户无需手动输入服务器 IP。
/// </para>
/// </summary>
public sealed class ServerDiscovery(ILogger<ServerDiscovery> logger)
{
    /// <summary>发现报文最大长度。</summary>
    private const int BufferSize = 4096;

    /// <summary>执行一次发现，返回去重后的服务器应答列表。</summary>
    /// <param name="timeout">等待时长。</param>
    /// <param name="port">发现端口。</param>
    public async Task<List<DiscoveryAnnounce>> DiscoverAsync(TimeSpan? timeout = null, int port = HubProtocol.DefaultDiscoveryPort)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(3);
        var results = new List<DiscoveryAnnounce>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nonce = Guid.NewGuid().ToString("N")[..12];

        using var client = new UdpClient { EnableBroadcast = true };
        client.Client.ReceiveTimeout = (int)wait.TotalMilliseconds;

        var probe = Encoding.UTF8.GetBytes(HubJson.Serialize(new DiscoveryProbe { Nonce = nonce }));
        await client.SendAsync(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, port));

        var deadline = DateTimeOffset.UtcNow + wait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var received = await client.ReceiveAsync();
                var announce = HubJson.Deserialize<DiscoveryAnnounce>(
                    Encoding.UTF8.GetString(received.Buffer));

                if (announce is null
                    || announce.Magic != HubProtocol.DiscoveryMagic
                    || announce.Nonce != nonce)
                {
                    continue;
                }

                if (seen.Add(announce.BaseUrl))
                {
                    results.Add(announce);
                }
            }
            catch (SocketException)
            {
                // 超时或单次报文异常，结束收集。
                break;
            }
        }

        logger.LogInformation("局域网发现完成，找到 {Count} 台服务器。", results.Count);
        return results;
    }
}
