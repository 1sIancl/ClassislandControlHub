using ControlHub.Server.Data;
using ControlHub.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Services;

/// <summary>
/// 后台维护服务：定期清理过期会话与历史日志，避免长期运行后数据库无限膨胀。
/// </summary>
public sealed class MaintenanceService(
    HubStore store,
    IOptions<ServerOptions> options,
    ILogger<MaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private readonly ServerOptions _options = options.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后稍等片刻再首次执行，避免与启动过程争抢数据库。
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
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
                var affected = await store.PurgeExpiredAsync(
                    _options.ClientLogRetentionDays,
                    _options.AuditLogRetentionDays,
                    stoppingToken);

                if (affected > 0)
                {
                    logger.LogInformation("维护任务清理了 {Count} 条过期记录。", affected);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "维护任务执行失败，将在下个周期重试。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
