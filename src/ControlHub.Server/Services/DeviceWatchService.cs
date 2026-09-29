using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ControlHub.Server.Services;

/// <summary>
/// 设备在线状态哨兵：定期比对在线状态，发现「刚刚掉线」的设备时推送 Webhook。
/// <para>
/// 只关心状态变化（在线 → 离线），不会反复刷屏；设备恢复在线后清除标记，
/// 因此下一次掉线仍会正常告警。
/// </para>
/// </summary>
public sealed class DeviceWatchService(
    HubStore store,
    SyncService sync,
    WebhookService webhooks,
    ILogger<DeviceWatchService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly HashSet<string> _alerted = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

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
                            _alerted.Remove(device.Id);
                            continue;
                        }

                        if (_alerted.Add(device.Id))
                        {
                            await webhooks.NotifyAsync(WebhookEvents.DeviceOffline,
                                $"设备掉线：{device.Name}",
                                $"「{device.Name}」已超过在线超时阈值未上报心跳，请检查教室终端是否关机或断网。",
                                new Dictionary<string, object?>
                                {
                                    ["deviceId"] = device.Id,
                                    ["name"] = device.Name,
                                    ["ip"] = device.IpAddress,
                                    ["lastSeenAt"] = device.LastSeenAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
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
