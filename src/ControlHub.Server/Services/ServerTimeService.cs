using System.Globalization;
using System.Net.Sockets;
using ControlHub.Protocol;
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

    /// <summary>
    /// 分组（校区）偏移在 settings 表中的键：JSON 对象 <c>{"&lt;groupId&gt;": 秒数}</c>（#26）。
    /// <para>整份配置存在一个设置项里：分组数量是个位数，逐个建键反而让「读全量」变成 N 次查询。</para>
    /// </summary>
    public const string GroupOffsetsKey = "timeGroupOffsets";

    /// <summary>「来源地址 → 该地址叠加的偏移」映射的缓存有效期。</summary>
    /// <remarks>
    /// NTP 报文里没有设备身份，只有来源地址，所以每个授时请求都要把地址换算成设备所属分组。
    /// 每次请求都查库太重，纯内存又会让「刚改完分组 / 设备换了 IP」长期不生效——折中是这份短 TTL 缓存。
    /// </remarks>
    private static readonly TimeSpan AddressMapTtl = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();

    /// <summary>保证同一时刻只有一次「重算地址映射」，避免并发授时把库查爆。</summary>
    private readonly SemaphoreSlim _mapGate = new(1, 1);

    private TimeSpan _offset = TimeSpan.Zero;
    private TimeSpan _manualOffset = TimeSpan.Zero;

    /// <summary>分组自身配置的偏移（分组 ID → 秒数，值已夹取）。</summary>
    private Dictionary<string, double> _groupOffsets = new(StringComparer.Ordinal);

    /// <summary>分组层级（分组 ID → 上级 ID），用于「没配就继承上级」。</summary>
    private Dictionary<string, string?> _groupParents = new(StringComparer.Ordinal);

    /// <summary>来源地址 → 应当叠加的偏移秒数（由设备所属分组解析而来）。</summary>
    private Dictionary<string, double> _ipOffsets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>地址映射的生成时刻；<see cref="DateTimeOffset.MinValue"/> 表示必须重算。</summary>
    private DateTimeOffset _addressMapAt = DateTimeOffset.MinValue;

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

        // 再把「分组偏移 + 来源地址 → 偏移」的映射建好：第一台教室端来授时时就能拿到正确的偏移，
        // 不必等第一次 60 秒的懒刷新。
        await EnsureAddressOffsetMapAsync(force: true, stoppingToken);

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
            return GetUtcNowLocked(TimeSpan.Zero);
        }
    }

    /// <summary>
    /// 按分组（校区）叠加偏移后的当前 UTC 时间（#26）。设备的 HTTP 请求带着设备令牌时用它。
    /// </summary>
    public DateTimeOffset GetUtcNowForGroup(string? groupId)
    {
        lock (_gate)
        {
            return GetUtcNowLocked(TimeSpan.FromSeconds(
                TimeGroupOffset.Resolve(groupId, _groupOffsets, _groupParents)));
        }
    }

    /// <summary>
    /// 按请求来源地址叠加偏移后的当前 UTC 时间（#26）。
    /// <para>NTP 报文里没有设备身份，只能靠来源地址；映射由
    /// <see cref="EnsureAddressOffsetMapAsync"/> 维护，取不到时退化为「只叠加全局偏移」。</para>
    /// </summary>
    public DateTimeOffset GetUtcNowForIp(string? ip)
    {
        lock (_gate)
        {
            var seconds = ip is not null && _ipOffsets.TryGetValue(ip, out var found) ? found : 0;
            return GetUtcNowLocked(TimeSpan.FromSeconds(seconds));
        }
    }

    /// <summary>调用方需已持有 <c>_gate</c>。</summary>
    private DateTimeOffset GetUtcNowLocked(TimeSpan groupOffset) =>
        DateTimeOffset.UtcNow + _offset + _manualOffset + DriftCompensation() + groupOffset;

    /// <summary>
    /// 分组自身配置的偏移（秒）。没配过返回 <c>null</c>——语义是「未设置，沿上级分组继承」，
    /// 与「显式配置了 0 秒」必须能区分开。
    /// </summary>
    public double? GroupOffsetSeconds(string groupId)
    {
        lock (_gate)
        {
            return _groupOffsets.TryGetValue(groupId, out var seconds) ? seconds : null;
        }
    }

    /// <summary>某分组实际生效的偏移（含沿上级继承的结果）。</summary>
    public double EffectiveGroupOffsetSeconds(string? groupId)
    {
        lock (_gate)
        {
            return TimeGroupOffset.Resolve(groupId, _groupOffsets, _groupParents);
        }
    }

    /// <summary>当前全部分组偏移配置的快照（管理端在它之上改一项再整份写回）。</summary>
    public IReadOnlyDictionary<string, double> GroupOffsets
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, double>(_groupOffsets, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>管理端保存分组偏移后调用：从 settings 表重新加载，并强制重算来源地址映射。</summary>
    public Task ReloadGroupOffsetsAsync(CancellationToken cancellationToken = default) =>
        EnsureAddressOffsetMapAsync(force: true, cancellationToken);

    /// <summary>
    /// 按 TTL 保证「分组偏移 + 来源地址 → 偏移」映射是最新的。
    /// <para>NTP 授时热路径会调用它：不强制时命中缓存就直接返回，一次查询都不发。</para>
    /// <para>刷新失败**不抛异常**：授时服务不能因为一次查库失败而不应答，继续用上一次的映射
    /// （最多偏移值陈旧，时间本身仍然正确）并记警告。</para>
    /// </summary>
    public async Task EnsureAddressOffsetMapAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && !IsAddressMapStale())
        {
            return;
        }

        // 已经有一次刷新在跑：本次直接用现有映射，排队等反而会让授时应答变慢。
        if (!await _mapGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            if (!force && !IsAddressMapStale())
            {
                return;
            }

            var offsets = await LoadGroupOffsetsFromSettingsAsync(cancellationToken);
            var groups = await store.GetGroupsAsync(cancellationToken);
            var devices = await store.GetDevicesAsync(cancellationToken);

            var parents = groups
                .GroupBy(g => g.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().ParentId, StringComparer.Ordinal);

            // 一个来源地址对应多台设备（同一 NAT 出口 / 本机联调）时，若它们算出的偏移**不一致**，
            // 就无法判断这台机器属于哪个校区——此时宁可不叠加（退化为全局偏移），也不要张冠李戴。
            var resolved = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in devices)
            {
                if (string.IsNullOrWhiteSpace(device.IpAddress))
                {
                    continue;
                }

                var ip = device.IpAddress.Trim();
                var seconds = TimeGroupOffset.Resolve(device.GroupId, offsets, parents);

                if (ambiguous.Contains(ip))
                {
                    continue;
                }

                if (resolved.TryGetValue(ip, out var existing))
                {
                    if (Math.Abs(existing - seconds) > 0.0001)
                    {
                        resolved.Remove(ip);
                        ambiguous.Add(ip);
                        logger.LogWarning("来源地址 {Ip} 上有多台设备属于不同偏移的分组，已按全局偏移处理。", ip);
                    }

                    continue;
                }

                resolved[ip] = seconds;
            }

            lock (_gate)
            {
                _groupOffsets = offsets;
                _groupParents = parents;
                _ipOffsets = resolved;
                _addressMapAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "刷新分组授时偏移映射失败，继续沿用上一次的映射。");
        }
        finally
        {
            _mapGate.Release();
        }
    }

    /// <summary>地址映射是否已过期（读 <c>_addressMapAt</c> 一律走这里，避免在锁外读到撕裂的日期值）。</summary>
    private bool IsAddressMapStale()
    {
        DateTimeOffset at;
        lock (_gate)
        {
            at = _addressMapAt;
        }

        return DateTimeOffset.UtcNow - at >= AddressMapTtl;
    }

    /// <summary>读取「分组 → 偏移」配置；内容坏掉时按空处理（不能让一个坏设置项把授时打停）。</summary>
    private async Task<Dictionary<string, double>> LoadGroupOffsetsFromSettingsAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            var text = await store.GetSettingAsync(GroupOffsetsKey, null, cancellationToken);
            var parsed = HubJson.DeserializeOrDefault(text, new Dictionary<string, double>());
            foreach (var (key, value) in parsed)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    result[key.Trim()] = TimeGroupOffset.Clamp(value);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "读取分组授时偏移配置失败，按「未配置」处理。");
        }

        return result;
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
