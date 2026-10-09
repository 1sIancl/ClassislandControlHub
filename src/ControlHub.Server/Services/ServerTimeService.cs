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

    /// <summary>「每天漂移几秒」在 settings 表中的键。</summary>
    private const string DailyDriftKey = "timeDailyDriftSeconds";

    /// <summary>每日漂移的累计起点日期（UTC，<c>yyyy-MM-dd</c>）在 settings 表中的键。</summary>
    private const string DriftAnchorKey = "timeDailyDriftAnchor";

    private readonly object _gate = new();
    private TimeSpan _offset = TimeSpan.Zero;
    private TimeSpan _manualOffset = TimeSpan.Zero;

    /// <summary>
    /// 设备时钟的每日漂移（秒/天，正数表示每天快、负数表示每天慢）。
    /// <para>教室终端（尤其虚拟化或老旧的工控机）的系统时钟常常每天稳定快慢几秒。
    /// 一次性偏移修不好它：今天校准了，明天又偏。所以这里按**天数**累计补偿，
    /// 而不是给一个固定值。</para>
    /// </summary>
    private double _dailyDriftSeconds;

    /// <summary>漂移累计的起点日。累计量 = 每日漂移 × 距起点日的整天数。</summary>
    private DateOnly _driftAnchor = DateOnly.FromDateTime(DateTime.UtcNow);

    private DateTimeOffset? _lastSyncAt;
    private string _lastSyncStatus = "尚未授时。";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        // 启动时先加载管理员配置的时间偏移（固定偏移 + 每日漂移）。
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

    /// <summary>从 settings 表加载时间偏移（手动固定偏移 + 每日漂移；失败时保持默认 0）。</summary>
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

        try
        {
            var driftText = await store.GetSettingAsync(DailyDriftKey, null, cancellationToken);
            if (double.TryParse(driftText, NumberStyles.Float, CultureInfo.InvariantCulture, out var drift))
            {
                lock (_gate)
                {
                    _dailyDriftSeconds = drift;
                }
            }

            var anchorText = await store.GetSettingAsync(DriftAnchorKey, null, cancellationToken);
            if (DateOnly.TryParseExact(anchorText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var anchor))
            {
                lock (_gate)
                {
                    _driftAnchor = anchor;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "加载每日时间漂移配置失败。");
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

    /// <summary>获取校正后的当前 UTC 时间（供 <c>/client/time</c> 与内置 NTP 服务器使用）。</summary>
    public DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return DateTimeOffset.UtcNow + _offset + _manualOffset + DriftCompensation();
        }
    }

    /// <summary>
    /// 每日漂移的累计补偿量：每日漂移 × 距基准日的**整天数**。
    ///
    /// <para>只按整天算（不按秒）是刻意的：按秒累计时，同一天内不同时刻授时会得到
    /// 不同的校正量，教室大屏上会看到时间「慢慢爬」——而漂移本身是缓慢的，
    /// 每天一次跳变更符合直觉，也让「今天和昨天差多少」可解释。</para>
    ///
    /// <para>调用方需已持有 <c>_gate</c>。</para>
    /// </summary>
    private TimeSpan DriftCompensation()
    {
        if (_dailyDriftSeconds == 0)
        {
            return TimeSpan.Zero;
        }

        var days = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - _driftAnchor.DayNumber;
        return TimeSpan.FromSeconds(_dailyDriftSeconds * days);
    }

    /// <summary>设置每日漂移（秒/天）；同时把累计起点重置为今天。</summary>
    /// <param name="seconds">正数表示设备每天会变快、需要往回拉；负数反之。</param>
    public void SetDailyDriftSeconds(double seconds)
    {
        lock (_gate)
        {
            _dailyDriftSeconds = seconds;
            // 改配置等于重新开始累计：否则新值会乘上「旧配置运行过的天数」，
            // 得到一个既不是旧值也不是新值的结果。
            _driftAnchor = DateOnly.FromDateTime(DateTime.UtcNow);
        }
    }

    /// <summary>只把累计起点重置为今天（保留每日漂移值）。设备做过一次人工校准后用它清零。</summary>
    public void ResetDriftAnchor()
    {
        lock (_gate)
        {
            _driftAnchor = DateOnly.FromDateTime(DateTime.UtcNow);
        }
    }

    /// <summary>每日漂移配置（秒/天）。</summary>
    public double DailyDriftSeconds
    {
        get
        {
            lock (_gate)
            {
                return _dailyDriftSeconds;
            }
        }
    }

    /// <summary>每日漂移已累计的天数（从基准日到今天）。</summary>
    public int DriftDays
    {
        get
        {
            lock (_gate)
            {
                return DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - _driftAnchor.DayNumber;
            }
        }
    }

    /// <summary>每日漂移当前累计出的补偿量（秒）。</summary>
    public double DriftCompensationSeconds
    {
        get
        {
            lock (_gate)
            {
                return DriftCompensation().TotalSeconds;
            }
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
