using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IslandHub.Server.Services;

/// <summary>
/// 天气管理：配置城市 → 定时抓取 → 缓存 → 供教室端读取（路线图外的新功能）。
///
/// <para>
/// 数据源选 <b>Open-Meteo</b>：**不需要 API Key**、允许非商业使用、支持中文城市名的地理编码。
/// 学校的 A 端大多在内网，没有外网时抓取会失败——所以这里**不丢旧数据**：失败只记 <c>Error</c>，
/// 上一次成功的温度仍然下发给教室端（教室里看到旧一点的天气，也好过突然空白）。
/// </para>
///
/// <para>
/// 只做「一个城市」：这是校园集控，全校一套天气就够；多校区各有天气属于另一个量级的需求。
/// </para>
/// </summary>
public sealed class WeatherService(
    HubStore store,
    ILogger<WeatherService> logger) : BackgroundService
{
    /// <summary>配置在设置表里的键。</summary>
    public const string ConfigKey = "weather.config";

    /// <summary>缓存（最近一次成功抓取）在设置表里的键——重启后不必等下一次抓取。</summary>
    public const string CacheKey = "weather.cache";

    /// <summary>抓取间隔下限：天气变化没那么快，抓太勤只会让别人封我们的 IP。</summary>
    private const int MinRefreshMinutes = 10;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// 中国气象局预警列表。**不是正式 API**（无文档、字段可能变），
    /// 所以只做"能解析就用"的容错读取，绝不依赖它。
    /// </summary>
    private const string NmcAlarmUrl = "http://www.nmc.cn/rest/findAlarm?pageNo=1&pageSize=10";

    /// <summary>一次最多展示几条预警：再多滚动条会跑到看不清。</summary>
    private const int MaxAlerts = 5;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WeatherStateDto _state = new();

    static WeatherService()
    {
        // Open-Meteo 没有强制要求 UA，但带上便于对方定位流量来源。
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"IslandHub/{HubProtocol.ProductVersion}");
    }

    /// <summary>当前状态（含配置与最近一次数据）。</summary>
    public WeatherStateDto State => _state;

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先给 20 秒让服务把别的事忙完，再抓第一次。
        await SafeDelayAsync(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_state.Config.Enabled)
                {
                    await RefreshAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "天气抓取失败。");
            }

            var minutes = Math.Max(MinRefreshMinutes, _state.Config.RefreshMinutes);
            await SafeDelayAsync(TimeSpan.FromMinutes(minutes), stoppingToken);
        }
    }

    /// <summary>从设置表重新装载配置与缓存（改完配置后调用）。</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var configJson = await store.GetSettingAsync(ConfigKey, null, cancellationToken);
        var config = configJson is null
            ? new WeatherConfigDto()
            : HubJson.DeserializeOrDefault(configJson, new WeatherConfigDto());

        var cacheJson = await store.GetSettingAsync(CacheKey, null, cancellationToken);
        var cached = cacheJson is null
            ? null
            : HubJson.DeserializeOrDefault<WeatherStateDto?>(cacheJson, null);

        _state = cached ?? new WeatherStateDto();
        _state.Config = config;
        RebuildSummary();
    }

    /// <summary>
    /// 保存配置。城市名变化时顺手反查一次经纬度；查不到就报错（而不是存一个抓不到天气的城市）。
    /// </summary>
    public async Task<WeatherStateDto> SaveConfigAsync(WeatherConfigDto config, CancellationToken cancellationToken = default)
    {
        var city = (config.City ?? string.Empty).Trim();
        config.RefreshMinutes = Math.Max(MinRefreshMinutes, config.RefreshMinutes);
        config.TemperatureUnit = config.TemperatureUnit == "fahrenheit" ? "fahrenheit" : "celsius";

        if (config.Enabled && city.Length == 0)
        {
            throw HubException.Validation("启用天气必须填写城市名（例如「杭州」）。");
        }

        // 只有**缺经纬度**时才去反查：显式给了坐标就直接用（内网没外网时，可以从别的机器上抄一份坐标过来，
        // 这样保存不需要访问地理编码接口）。城市只是显示名，改了它不会触发一次网络往返。
        if (city.Length > 0 && (config.Latitude == 0 || config.Longitude == 0))
        {
            var place = await GeocodeAsync(city, cancellationToken);
            if (place is null)
            {
                throw HubException.Validation($"没找到城市「{city}」，换个写法（例如「杭州」而不是「杭州市西湖区」）再试。");
            }

            // 可空元组取值要走 `.Value`——直接 `.Name` 编译器会报「元组没有该成员」。
            config.City = place.Value.Name;
            config.Latitude = place.Value.Latitude;
            config.Longitude = place.Value.Longitude;
        }

        await store.SetSettingAsync(ConfigKey, HubJson.Serialize(config), cancellationToken);
        _state.Config = config;
        RebuildSummary();

        if (config.Enabled)
        {
            await RefreshAsync(cancellationToken);
        }

        return _state;
    }

    /// <summary>立即抓取一次。失败不抛异常（把原因记进状态），调用方看 <c>Error</c> 即可。</summary>
    public async Task<WeatherStateDto> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_state.Config.Enabled || _state.Config.Latitude == 0 || _state.Config.Longitude == 0)
            {
                _state.Error = _state.Config.Enabled ? "还没解析出经纬度，请重新保存城市。" : null;
                return _state;
            }

            var unit = _state.Config.TemperatureUnit == "fahrenheit" ? "fahrenheit" : "celsius";
            var url = "https://api.open-meteo.com/v1/forecast"
                      + $"?latitude={_state.Config.Latitude.ToString(CultureInfo.InvariantCulture)}"
                      + $"&longitude={_state.Config.Longitude.ToString(CultureInfo.InvariantCulture)}"
                      + "&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m"
                      + $"&temperature_unit={unit}&timezone=auto";

            using var response = await Http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _state.Error = $"天气服务返回 {(int)response.StatusCode}，稍后会自动重试。";
                logger.LogWarning("天气接口返回 {Status}。", (int)response.StatusCode);
                return _state;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("current", out var current))
            {
                _state.Error = "天气服务返回的数据看不懂（缺 current 字段）。";
                return _state;
            }

            _state.HasData = true;
            _state.Temperature = ReadDouble(current, "temperature_2m");
            _state.Humidity = ReadInt(current, "relative_humidity_2m");
            _state.WindSpeed = ReadDouble(current, "wind_speed_10m");
            _state.WeatherCode = ReadInt(current, "weather_code");
            _state.ObservedAt = ReadString(current, "time");
            _state.TemperatureText = unit == "fahrenheit" ? "°F" : "°C";
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            _state.Error = null;
            RebuildSummary();

            // 气象预警（#142）：独立抓、独立失败。
            // 中国气象局的公开接口不是正式 API，随时可能变；抓不到就留空数组，
            // 绝不能让"没有预警"变成"没有天气"。
            await RefreshAlertsAsync(cancellationToken);

            // 缓存写回设置表：重启后立刻有数据，不必等第一次抓取。
            await store.SetSettingAsync(CacheKey, HubJson.Serialize(_state), cancellationToken);
            logger.LogInformation("天气已更新：{City} {Temp}{Unit}（code {Code}）。",
                _state.Config.City, _state.Temperature, _state.TemperatureText, _state.WeatherCode);
            return _state;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 内网没有外网时走到这里：**保留旧数据**，只记原因。
            _state.Error = "抓取失败（服务器可能没有外网，或天气服务暂时不可用）。";
            logger.LogWarning(ex, "天气抓取失败（保留上一次数据）。");
            return _state;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ────────────────────────────── 内部 ──────────────────────────────

    /// <summary>城市名 → 经纬度（Open-Meteo 的地理编码接口，免 Key）。</summary>
    private static async Task<(string Name, double Latitude, double Longitude)?> GeocodeAsync(
        string city, CancellationToken cancellationToken)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search"
                  + $"?name={Uri.EscapeDataString(city)}&count=1&language=zh&format=json";
        try
        {
            using var response = await Http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                return null;
            }

            var first = results[0];
            var name = first.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? city : city;
            var latitude = ReadDouble(first, "latitude") ?? 0;
            var longitude = ReadDouble(first, "longitude") ?? 0;
            return latitude == 0 && longitude == 0 ? null : (name, latitude, longitude);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 抓中国气象局（NMC）的预警列表（#142）。
    ///
    /// <para>
    /// 这个接口**不是正式 API**：没有文档、可能改结构、可能限流。所以整段都在 try 里，
    /// 任何失败都只留空数组——预警是"有就用"的增强信息，缺了不该让天气整块消失。
    /// </para>
    /// </summary>
    private async Task RefreshAlertsAsync(CancellationToken cancellationToken)
    {
        var alerts = new List<WeatherAlertDto>();

        try
        {
            using var response = await Http.GetAsync(NmcAlarmUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);

                // 结构：{ code, data: { page: { list: [ { title, signaltype, signallevel, issuetime, content } ] } } }
                if (JsonNode.Parse(text) is JsonObject root
                    && root["data"]?["page"]?["list"] is JsonArray list)
                {
                    foreach (var item in list)
                    {
                        var title = item?["title"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            continue;
                        }

                        alerts.Add(new WeatherAlertDto
                        {
                            Title = title.Trim(),
                            Type = item?["signaltype"]?.GetValue<string>()?.Trim() ?? string.Empty,
                            Level = item?["signallevel"]?.GetValue<string>()?.Trim() ?? string.Empty,
                            Text = item?["content"]?.GetValue<string>()?.Trim() ?? string.Empty,
                            IssuedAt = item?["issuetime"]?.GetValue<string>()?.Trim() ?? string.Empty,
                        });

                        // 大屏只放得下几条：再多滚动条会跑到看不清。
                        if (alerts.Count >= MaxAlerts)
                        {
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogDebug(ex, "预警抓取失败（不影响天气主数据）。");
        }

        _state.Alerts = alerts;
    }

    /// <summary>拼一行摘要给教室端直接显示（插件不必懂 WMO 代码）。</summary>
    private void RebuildSummary()
    {
        if (!_state.HasData)
        {
            _state.Summary = string.Empty;
            return;
        }

        var temperature = _state.Temperature?.ToString("0.#", CultureInfo.InvariantCulture) ?? "--";
        var text = DescribeCode(_state.WeatherCode);
        _state.Summary = _state.Config.TemperatureUnit == "fahrenheit"
            ? $"{_state.Config.City} {text} {temperature}{_state.TemperatureText}"
            : $"{_state.Config.City} {text} {temperature}{_state.TemperatureText}";
    }

    /// <summary>
    /// WMO 天气代码 → 中文短描述（+ 英文），教室端按 <c>weatherCode</c> 自己再本地化也行。
    /// </summary>
    private static string DescribeCode(int? code) => code switch
    {
        0 => "晴",
        1 => "晴间多云",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻雨",
        61 or 63 or 65 => "雨",
        66 or 67 => "冻雨",
        71 or 73 or 75 or 77 => "雪",
        80 or 81 or 82 => "阵雨",
        85 or 86 => "阵雪",
        95 => "雷阵雨",
        96 or 99 => "雷阵雨伴冰雹",
        _ => "未知",
    };

    private static double? ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(value.GetDouble())
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 正常停机。
        }
    }
}
