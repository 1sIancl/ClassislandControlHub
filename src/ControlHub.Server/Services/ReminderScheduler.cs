using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ControlHub.Server.Services;

/// <summary>
/// 提醒调度器：每 15 秒检查一次到点的提醒并触发。
/// <para>
/// 时间取自 <see cref="ServerTimeService"/>（含 NTP 与管理员手动偏移校正），
/// 因此「设定 8:00 触发」与教室大屏上看到的时钟一致；15 秒轮询保证触发误差在 15 秒内。
/// </para>
/// </summary>
public sealed class ReminderScheduler(
    ReminderService reminders,
    ILogger<ReminderScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 启动后稍等片刻再开始，避开数据库初始化与首次授时。
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    var fired = await reminders.FireDueRemindersAsync(stoppingToken);
                    if (fired > 0)
                    {
                        logger.LogInformation("本轮触发了 {Count} 条定时提醒。", fired);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 单轮失败不能拖垮后台服务，记日志后继续下一轮。
                    logger.LogError(ex, "提醒调度本轮执行失败。");
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
