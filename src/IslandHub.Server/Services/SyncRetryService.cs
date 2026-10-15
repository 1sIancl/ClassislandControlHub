using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;
using IslandHub.Server.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IslandHub.Server.Services;

/// <summary>
/// 配置下发失败自动重试（#17）。
/// <para>教室端回报 <c>failed</c> / <c>partial</c> 之后，原先只把设备标成「异常」等管理员自己发现。
/// 而实际最常见的原因是**一时的问题**（配置目录被占用、磁盘临时不可写、ClassIsland 正在退出）——
/// 这个哨兵隔一段时间把「回报过失败且仍在线」的设备**定向重推一次**，让它有机会自愈。</para>
/// <para><b>克制是设计的一部分</b>：只重推回报过失败的设备（不碰「只是还没轮到拉」的正常落后设备）、
/// 每台有冷却时间、最多重试配置里的次数就停手交给人工。无冷却地反复重推会不停抬升推送世代号，
/// 让全校教室机跟着反复拉配置——那比不重试更糟。</para>
/// <para>设备回报成功、或再也不是异常状态之后，它的重试记账会被清掉；
/// 所以「A 端重启」只会让计数从头再算，不会永远重试下去。离线的失败设备也不推：它下次上线
/// 本来就会拉到最新配置（世代号已经比它高），推了只是白占一次冷却。</para>
/// </summary>
public sealed class SyncRetryService(
    HubStore store,
    SyncService sync,
    WebhookService webhooks,
    IOptions<ServerOptions> options,
    ILogger<SyncRetryService> logger) : BackgroundService
{
    /// <summary>每台设备的重试记账（放内存即可：重启后重新计数是安全的方向）。</summary>
    private sealed class Attempt
    {
        public int Count { get; set; }

        public DateTimeOffset LastAt { get; set; }
    }

    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.SyncRetryEnabled)
        {
            logger.LogInformation("配置下发失败自动重试已关闭（SyncRetryEnabled=false）。");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, settings.SyncRetryIntervalSeconds));
        var cooldown = TimeSpan.FromMinutes(Math.Max(1, settings.SyncRetryCooldownMinutes));

        try
        {
            // 启动先等一会儿：让设备把上一轮的回报发完再判断，别一启动就重推。
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            using var timer = new PeriodicTimer(interval);
            do
            {
                try
                {
                    await RetryFailedDevicesAsync(cooldown, stoppingToken);
                }
                catch (Exception ex)
                {
                    // 单轮出错不该让哨兵停摆（与其它后台服务一致）。
                    logger.LogError(ex, "扫描配置下发失败的设备时出错。");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
    }

    /// <summary>把回报过失败、仍在线、且没超过重试上限的设备各重推一次。</summary>
    private async Task RetryFailedDevicesAsync(TimeSpan cooldown, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, options.Value.SyncRetryMaxAttempts);
        var devices = await store.GetDevicesAsync(cancellationToken);

        var failed = devices
            .Where(d => !d.Revoked
                        && string.Equals(d.State, DeviceStates.Error, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 已经不是异常的设备（自愈了 / 被停用 / 被删掉）不该继续占着重试名额。
        var failedIds = failed.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _attempts.Keys.Where(id => !failedIds.Contains(id)).ToList())
        {
            _attempts.Remove(id);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var device in failed)
        {
            if (!sync.IsOnline(device))
            {
                continue;
            }

            if (!_attempts.TryGetValue(device.Id, out var attempt))
            {
                attempt = new Attempt();
                _attempts[device.Id] = attempt;
            }

            if (attempt.Count >= maxAttempts)
            {
                // 到上限就交给人工：前几次的尝试已经在审计与日志里，管理员能看出「重试没用」。
                continue;
            }

            if (attempt.LastAt != default && now - attempt.LastAt < cooldown)
            {
                continue;
            }

            attempt.Count++;
            attempt.LastAt = now;

            var result = await sync.PushAsync(new PushRequest
            {
                Scope = "device",
                TargetIds = [device.Id],
                Force = false,
                Message = $"自动重试第 {attempt.Count} 次（上次应用失败）",
            }, "system:sync-retry", cancellationToken);

            await store.AddAuditAsync("系统", "device.sync.retry", device.Name,
                $"配置下发失败自动重推（第 {attempt.Count}/{maxAttempts} 次，影响 {result.Affected} 台）。",
                null, cancellationToken);

            // 外部通知（#75）：自动重试是「系统自己动过手」，订阅方要能看见——
            // 标题带设备名，避免多台设备同时重试时被同标题抑制窗口合并成一条。
            await webhooks.NotifyAsync(WebhookEvents.SyncRetry,
                $"配置下发自动重试：{device.Name}",
                $"「{device.Name}」上次应用配置失败，已自动重推（第 {attempt.Count}/{maxAttempts} 次）。",
                new Dictionary<string, object?>
                {
                    ["deviceId"] = device.Id,
                    ["deviceName"] = device.Name,
                    ["attempts"] = attempt.Count,
                    ["maxAttempts"] = maxAttempts,
                }, cancellationToken);

            logger.LogInformation("设备「{Name}」上次应用配置失败，已自动重推（第 {Count}/{Max} 次）。",
                device.Name, attempt.Count, maxAttempts);
        }
    }
}
