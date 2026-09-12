using System.Globalization;
using System.Net.Sockets;
using ControlHub.Server.Data;
using ControlHub.Server.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Services;

/// <summary>
/// 服务端授时服务：让集控服务器（A 端）自身保持准确的时间基准。
/// <para>
/// 周期性地从标准 NTP 服务器（默认 <c>ntp.aliyun.com</c>）授时，计算出
/// 「NTP 时间 − 本机系统时间」的偏移并缓存。<see cref="GetUtcNow"/> 返回的是
/// 「NTP 校正时间 + 管理员手动偏移」后的 UTC 时间，供 <c>GET /client/time</c> 下发，
/// 从而形成「标准 NTP → A 端 → B 端教室终端」的完整授时链。
/// </para>
/// <para>
/// 手动偏移（<see cref="ManualOffsetSeconds"/>）由管理员在 Web 端「系统设置」中配置，
/// 持久化在 settings 表（key=<c>timeOffsetSeconds</c>），语义与 ClassIsland 时钟设置里的
/// 「时间偏移」一致：让所有教室终端整体提前/延后指定秒数。
/// </para>
/// <para>
/// 为避免跨平台改动系统时间带来的权限/复杂度，这里采用「软件授时」：
/// 仅校正对外下发的时间，不改写操作系统时钟。若系统时钟偏差过大，
/// 会记录警告提示运维在操作系统层面配置 NTP。
/// </para>
/// </summary>
public sealed class ServerTimeService(
    IOptions<ServerOptions> options,
    NtpClient ntpClient,
    HubStore store,
    ILogger<ServerTimeService> logger) : BackgroundService
{
    /// <summary>后台授时周期。</summary>
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(10);

    /// <summary>系统时钟偏差超过该值（秒）时记录警告。</summary>
    private const double WarnThresholdSeconds = 5;

    /// <summary>手动时间偏移在 settings 表中的键。</summary>
    private const string ManualOffsetKey = "timeOffsetSeconds";

    private readonly object _gate = new();
    private TimeSpan _offset = TimeSpan.Zero;
    private TimeSpan _manualOffset = TimeSpan.Zero;
    private DateTimeOffset? _lastSyncAt;
    private string _lastSyncStatus = "尚未授时。";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        // 启动时先加载管理员配置的手动时间偏移。
        await LoadManualOffsetAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.Value.EnableNtpSync)
            {
                await SyncOnceAsync(stoppingToken);
            }

            try
            {
                await Task.Delay(SyncInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>从 settings 表加载手动时间偏移（失败时保持默认 0）。</summary>
    private async Task LoadManualOffsetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var text = await store.GetSettingAsync(ManualOffsetKey, null, cancellationToken);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                lock (_gate)
                {
                    _manualOffset = TimeSpan.FromSeconds(seconds);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "加载手动时间偏移失败。");
        }
    }

    /// <summary>立即从 NTP 服务器授时一次，并更新缓存偏移。</summary>
    public async Task SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        var servers = options.Value.NtpServers;

        foreach (var server in servers)
        {
            try
            {
                var offset = await ntpClient.QueryOffsetAsync(server, cancellationToken);

                lock (_gate)
                {
                    _offset = offset;
                    _lastSyncAt = DateTimeOffset.UtcNow;
                    _lastSyncStatus = $"已从 {server} 授时，偏移 {FormatOffset(offset)}。";
                }

                logger.LogInformation("已从 {Server} 授时，时钟偏移 {Offset}s。",
                    server, offset.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));

                if (Math.Abs(offset.TotalSeconds) >= WarnThresholdSeconds)
                {
                    logger.LogWarning("服务器系统时钟偏差 {Offset}s，建议在操作系统层面配置 NTP（如 systemd-timesyncd）。",
                        offset.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture));
                }

                return;
            }
            catch (Exception ex) when (ex is SocketException or InvalidDataException or OperationCanceledException)
            {
                logger.LogWarning("从 {Server} 授时失败：{Message}", server, ex.Message);
            }
        }

        lock (_gate)
        {
            _lastSyncStatus = "授时失败：所有 NTP 服务器均不可达，暂用系统时间。";
        }
    }

    /// <summary>获取校正后的当前 UTC 时间（供 <c>/client/time</c> 下发）。</summary>
    public DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return DateTimeOffset.UtcNow + _offset + _manualOffset;
        }
    }

    /// <summary>设置管理员手动时间偏移（秒），立即生效。</summary>
    public void SetManualOffsetSeconds(double seconds)
    {
        lock (_gate)
        {
            _manualOffset = TimeSpan.FromSeconds(seconds);
        }
    }

    /// <summary>管理员手动时间偏移（秒）。</summary>
    public double ManualOffsetSeconds
    {
        get
        {
            lock (_gate)
            {
                return _manualOffset.TotalSeconds;
            }
        }
    }

    /// <summary>最近一次授时的时间。</summary>
    public DateTimeOffset? LastSyncAt
    {
        get
        {
            lock (_gate)
            {
                return _lastSyncAt;
            }
        }
    }

    /// <summary>当前缓存的 NTP 时钟偏移（秒）。</summary>
    public double OffsetSeconds
    {
        get
        {
            lock (_gate)
            {
                return _offset.TotalSeconds;
            }
        }
    }

    /// <summary>最近一次授时状态描述。</summary>
    public string LastSyncStatus
    {
        get
        {
            lock (_gate)
            {
                return _lastSyncStatus;
            }
        }
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset.TotalSeconds >= 0 ? "+" : "";
        return $"{sign}{offset.TotalSeconds:F2} 秒";
    }
}
