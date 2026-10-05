using ControlHub.Server.Data;
using ControlHub.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Services;

/// <summary>
/// 在线率采样服务（#63）。
///
/// 为什么必须单独跑一个后台服务：<c>devices.last_seen_at</c> 是**瞬时**值——设备每次上报心跳都会被覆盖，
/// 历史不留痕。不采样的话，事后就没法回答「这台教室上周在线率多少」。
///
/// 间隔 1 分钟：分辨率足够看清「上午 10 点掉线」这类情况；一天的写入量是
/// 「设备数 × 1440」次 upsert，落在同一批行上，对 SQLite 没有压力。
/// </summary>
public sealed class StatsService(
    HubStore store,
    IOptions<ServerOptions> options,
    ILogger<StatsService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先等一会儿：让设备把心跳喂上来，否则刚启动那一轮会把整批设备记成离线。
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                // 与 SyncService.IsOnline 用同一个超时值，保证报表和仪表盘的在线数不是一个口径。
                var timeout = TimeSpan.FromSeconds(Math.Max(10, options.Value.OnlineTimeoutSeconds));
                var online = await store.RecordOnlineSampleAsync(timeout, stoppingToken);
                logger.LogDebug("在线采样完成：本轮 {Online} 台设备在线。", online);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 采样失败不该让服务退出：下一分钟补上即可（报表分母按实际采样轮次算，不会因此失真）。
                logger.LogWarning(ex, "在线采样失败，将在下一分钟重试。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
