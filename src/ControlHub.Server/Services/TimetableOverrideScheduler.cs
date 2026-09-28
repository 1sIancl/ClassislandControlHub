using ControlHub.Server.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ControlHub.Server.Services;

/// <summary>
/// 临时换课的跨天调度：日期变化时递增一次全局配置版本号，
/// 让所有教室重新拉取配置——这样「今天该换的课」会自动生效，昨天的覆盖也会自动还原。
/// <para>
/// 管理员新增 / 撤销换课时由接口直接唤醒设备，不依赖本任务；
/// 这里只负责「跨天」这个唯一的定时场景，且仅当确实存在换课时才递增版本号。
/// </para>
/// </summary>
public sealed class TimetableOverrideScheduler(
    SyncService sync,
    HubStore store,
    ILogger<TimetableOverrideScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private string _lastDate = string.Empty;

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
                    var today = DateTime.Now.ToString("yyyy-MM-dd");
                    if (_lastDate.Length == 0)
                    {
                        _lastDate = today;
                        continue;
                    }

                    if (today != _lastDate)
                    {
                        if (await store.HasActiveOrUpcomingTimetableOverridesAsync(today, stoppingToken))
                        {
                            var revision = await sync.BumpRevisionAsync(stoppingToken);
                            logger.LogInformation(
                                "日期变更（{From} → {To}），存在临时换课，已递增配置版本到 {Revision} 触发设备重新同步。",
                                _lastDate, today, revision);
                        }

                        _lastDate = today;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 单轮失败不能拖垮后台服务，记日志后继续下一轮。
                    logger.LogError(ex, "临时换课调度本轮执行失败。");
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
