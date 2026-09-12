using ControlHub.Plugin.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ControlHub.Plugin.Services;

/// <summary>
/// 时间同步服务：让教室终端（B 端）的 ClassIsland 大屏时钟以集控服务器（A 端）为时间源。
/// <para>
/// 实现方式是调用 <see cref="ClassIslandClockService.ConfigureNtpSync"/>，把 ClassIsland 的
/// 「精确时间服务器」指向 A 端并启用「使用精确时间」，随后触发立即同步。此后 ClassIsland
/// 会周期性（内置每 10 分钟）从 A 端 NTP 服务器同步精确时间——修改的是 ClassIsland 的时钟，
/// 而非 Windows 系统时间，也无需管理员权限。
/// </para>
/// </summary>
public sealed class TimeSyncService(
    HubSettingsStore settings,
    HubState state,
    ClassIslandClockService clock,
    ILogger<TimeSyncService> logger) : BackgroundService
{
    /// <summary>后台维护 ClassIsland 精确时间配置的间隔。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = settings.Load();

            if (cfg.EnableTimeSync && !string.IsNullOrWhiteSpace(cfg.ServerUrl))
            {
                try
                {
                    await SyncOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    state.LastTimeSyncMessage = "时间同步失败：" + ex.Message;
                    logger.LogWarning(ex, "后台时间同步失败。");
                }
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>立即把 ClassIsland 的精确时间服务器指向 A 端并同步一次。</summary>
    public async Task<string> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        var cfg = settings.Load();
        if (string.IsNullOrWhiteSpace(cfg.ServerUrl))
        {
            throw new InvalidOperationException("请先填写服务器地址。");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var ntpHost = ExtractHost(cfg.ServerUrl);
            var message = clock.ConfigureNtpSync(ntpHost);

            state.LastTimeSyncAt = DateTimeOffset.Now;
            state.LastTimeSyncMessage = message;
            state.Log("info", message);
            return message;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>从服务器地址中提取主机名（NTP 用标准 123 端口，去掉 http 前缀与端口）。</summary>
    private static string ExtractHost(string serverUrl)
    {
        return Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ? uri.Host : serverUrl;
    }
}
