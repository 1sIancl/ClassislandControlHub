using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ControlHub.Server.Services;

/// <summary>
/// 设备在线状态哨兵：定期比对在线状态，发现「刚刚掉线」的设备时推送 Webhook，
/// 恢复上线时再推一条「已恢复」。
/// <para>
/// 只关心状态变化（在线 ↔ 离线），不会反复刷屏；恢复通知里带上离线时长，
/// 便于判断是「闪断一下」还是「断了一节课」。
/// </para>
/// </summary>
public sealed class DeviceWatchService(
    HubStore store,
    SyncService sync,
    WebhookService webhooks,
    ILogger<DeviceWatchService> logger) : BackgroundService
{
    // 周期越短，「设备实际掉线」与「收到通知」之间的间隔越短。
    // 20 秒是在通知及时性与数据库压力之间的折中（掉线判定本身由在线超时阈值决定）。
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private readonly HashSet<string> _alerted = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    var devices = await store.GetDevicesAsync(stoppingToken);
                    foreach (var device in devices.Where(d => !d.Revoked))
                    {
                        if (sync.IsOnline(device))
                        {
                            // 恢复上线：与掉线配对通知一次——出问题要知道，恢复了同样要知道。
                            if (_alerted.Remove(device.Id))
                            {
                                var minutes = SyncService.OfflineMinutes(device, online: true) ?? 0;
                                var elapsed = device.LastSeenAt is null
                                    ? "（未记录最后心跳）"
                                    : $"本次离线约 {Math.Max(1, Math.Round((DateTimeOffset.UtcNow - device.LastSeenAt.Value).TotalMinutes)):0} 分钟";

                                await webhooks.NotifyAsync(WebhookEvents.DeviceOnline,
                                    $"设备恢复：{device.Name}",
                                    $"「{device.Name}」已恢复上报心跳，{elapsed}。",
                                    new Dictionary<string, object?>
                                    {
                                        ["deviceId"] = device.Id,
                                        ["name"] = device.Name,
                                        ["ip"] = device.IpAddress,
                                        ["lastSeenAt"] = device.LastSeenAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                                        ["offlineMinutes"] = minutes,
                                    }, stoppingToken);
                            }

                            continue;
                        }

                        if (_alerted.Add(device.Id))
                        {
                            var offlineMinutes = SyncService.OfflineMinutes(device, online: false) ?? 0;
                            await webhooks.NotifyAsync(WebhookEvents.DeviceOffline,
                                $"设备掉线：{device.Name}",
                                $"「{device.Name}」已超过在线超时阈值未上报心跳，请检查教室终端是否关机或断网。",
                                new Dictionary<string, object?>
                                {
                                    ["deviceId"] = device.Id,
                                    ["name"] = device.Name,
                                    ["ip"] = device.IpAddress,
                                    ["lastSeenAt"] = device.LastSeenAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                                    ["offlineMinutes"] = Math.Round(offlineMinutes),
                                }, stoppingToken);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "设备哨兵本轮执行失败。");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // 正常停机。
        }
    }
}
