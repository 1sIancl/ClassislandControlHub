using System.Net;
using System.Net.Sockets;
using ControlHub.Server.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Services;

/// <summary>
/// 集控服务器内置的 NTP 服务器（UDP 123）。
/// <para>
/// 让教室终端的 ClassIsland「精确时间」直接以本服务器为时间源（把其「时间服务器」指向本机即可），
/// 从而做到「修改 ClassIsland 的时钟时间、而非系统时间」。授时的时间来自
/// <see cref="ServerTimeService.GetUtcNow"/>（标准 NTP 校正 + 管理员手动偏移）。
/// </para>
/// <para>实现标准 NTP 单播响应：回显客户端 T1，返回 T2/T3，客户端据此计算 offset。</para>
/// </summary>
public sealed class NtpServer(
    IOptions<ServerOptions> options,
    ServerTimeService serverTime,
    ILogger<NtpServer> logger) : BackgroundService
{
    private UdpClient? _listener;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnableNtpServer)
        {
            logger.LogInformation("NTP 服务器已禁用。");
            return;
        }

        var port = options.Value.NtpPort;
        try
        {
            _listener = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "无法监听 NTP UDP {Port} 端口（可能需要 root/管理员权限）。", port);
            return;
        }

        logger.LogInformation("NTP 服务器已监听 UDP {Port}。", port);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _listener.ReceiveAsync(stoppingToken);
                _ = RespondAsync(result, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "处理 NTP 请求失败。");
            }
        }

        _listener.Dispose();
    }

    private async Task RespondAsync(UdpReceiveResult result, CancellationToken cancellationToken)
    {
        var request = result.Buffer;
        if (request.Length < 48)
        {
            return; // 非法请求，静默丢弃。
        }

        // 解析请求：VN（第 3~5 位）与客户端 T1（Transmit Timestamp）。
        var version = (request[0] >> 3) & 0x07;
        var t1 = NtpTimestamp.ReadUInt64BigEndian(request, 40);

        var nowUtc = serverTime.GetUtcNow().UtcDateTime;
        var nowNtp = NtpTimestamp.ToUInt64(nowUtc);

        // 构造 48 字节响应。
        var response = new byte[48];
        response[0] = (byte)((version << 3) | 4); // LI=0, VN=回显, Mode=4(server)
        response[1] = 2;                          // Stratum 2（次级服务器）
        response[2] = request[2];                 // Poll 回显
        response[3] = 0xF7;                       // Precision ≈ -17（约 8µs）

        NtpTimestamp.WriteUInt64BigEndian(response, 16, nowNtp); // Reference Timestamp
        NtpTimestamp.WriteUInt64BigEndian(response, 24, t1);      // Originate Timestamp = T1 回显
        NtpTimestamp.WriteUInt64BigEndian(response, 32, nowNtp);  // Receive Timestamp = T2
        NtpTimestamp.WriteUInt64BigEndian(response, 40, nowNtp);  // Transmit Timestamp = T3

        try
        {
            await _listener!.SendAsync(response.AsMemory(), result.RemoteEndPoint, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "发送 NTP 响应失败。");
        }
    }
}
