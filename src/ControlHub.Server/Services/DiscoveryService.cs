using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Services;

/// <summary>
/// 局域网自动发现服务。
/// <para>
/// 监听 UDP 端口，收到 <see cref="DiscoveryProbe"/> 后回送 <see cref="DiscoveryAnnounce"/>，
/// 其中包含服务器名称与可直接访问的 HTTP 地址。客户端插件据此实现「一键发现并连接服务器」，
/// 无需管理员手动抄写 IP。
/// </para>
/// </summary>
public sealed class DiscoveryService(
    IOptions<ServerOptions> options,
    ILogger<DiscoveryService> logger) : BackgroundService
{
    private readonly ServerOptions _options = options.Value;

    /// <summary>单次接收缓冲大小。发现报文很小，4KB 足够。</summary>
    private const int BufferSize = 4096;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableDiscovery)
        {
            logger.LogInformation("局域网自动发现已关闭，跳过启动。");
            return;
        }

        using var client = new UdpClient(new IPEndPoint(IPAddress.Any, _options.DiscoveryPort))
        {
            EnableBroadcast = true,
        };

        logger.LogInformation("局域网自动发现已启动，监听 UDP 端口 {Port}。", _options.DiscoveryPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var received = await client.ReceiveAsync(stoppingToken);
                _ = Task.Run(() => HandleProbeAsync(client, received), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset
                                                 or SocketError.MessageSize or SocketError.NetworkReset)
            {
                // 单次报文异常不应终止整个监听循环。
                logger.LogDebug(ex, "收到异常 UDP 报文，已忽略。");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "局域网发现服务发生未预期错误，1 秒后重试。");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        logger.LogInformation("局域网自动发现已停止。");
    }

    private async Task HandleProbeAsync(UdpClient client, UdpReceiveResult received)
    {
        try
        {
            var text = Encoding.UTF8.GetString(received.Buffer);
            var probe = HubJson.Deserialize<DiscoveryProbe>(text);

            if (probe is null || probe.Magic != HubProtocol.DiscoveryMagic)
            {
                return;
            }

            if (probe.Version != HubProtocol.MajorVersion)
            {
                logger.LogDebug("忽略协议版本不匹配的发现请求：{Version}。", probe.Version);
                return;
            }

            var announce = new DiscoveryAnnounce
            {
                Nonce = probe.Nonce,
                ServerName = _options.ServerName,
                BaseUrl = ResolveBaseUrl(received.RemoteEndPoint.Address),
                ApiPrefix = HubProtocol.ApiPrefix,
                RequiresEnrollCode = _options.RequireEnrollCode,
                Revision = 0,
                ServerTime = DateTimeOffset.UtcNow,
            };

            var payload = Encoding.UTF8.GetBytes(HubJson.Serialize(announce));
            await client.SendAsync(payload, received.RemoteEndPoint);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "处理发现请求失败。");
        }
    }

    /// <summary>
    /// 推断应答中应公布的基础地址。
    /// 优先使用显式配置的 <see cref="ServerOptions.PublicBaseUrl"/>；
    /// 否则从本机网卡中挑选与请求来源处于同一子网的 IPv4 地址。
    /// </summary>
    private string ResolveBaseUrl(IPAddress remoteAddress)
    {
        var scheme = _options.EnableHttps ? "https" : "http";
        var port = _options.EnableHttps ? _options.HttpsPort : _options.HttpPort;

        if (!string.IsNullOrWhiteSpace(_options.PublicBaseUrl))
        {
            return _options.PublicBaseUrl.TrimEnd('/');
        }

        var candidates = GetLocalIPv4Addresses();
        var best = SelectBestAddress(candidates, remoteAddress) ?? candidates.FirstOrDefault();
        return best is null
            ? $"{scheme}://127.0.0.1:{port}"
            : $"{scheme}://{best}:{port}";
    }

    private static List<IPAddress> GetLocalIPv4Addresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address.Address))
                {
                    result.Add(address.Address);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 在与 <paramref name="remote"/> 处于同一 /24 网段的候选中挑选一个；
    /// 找不到时退化为任意私有网段地址。
    /// </summary>
    private static IPAddress? SelectBestAddress(List<IPAddress> candidates, IPAddress remote)
    {
        var remoteBytes = remote.GetAddressBytes();
        if (remoteBytes.Length == 4)
        {
            var sameSubnet = candidates.FirstOrDefault(c =>
            {
                var b = c.GetAddressBytes();
                return b[0] == remoteBytes[0] && b[1] == remoteBytes[1] && b[2] == remoteBytes[2];
            });
            if (sameSubnet is not null)
            {
                return sameSubnet;
            }
        }

        return candidates.FirstOrDefault(IsPrivate);
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4)
        {
            return false;
        }

        return b[0] == 10
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
    }
}
