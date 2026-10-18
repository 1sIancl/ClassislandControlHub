namespace IslandHub.Protocol.Dtos;

/// <summary>
/// 天气配置。
///
/// <para>
/// 为什么用「经纬度 + 城市名」两个字段：城市名给人看、经纬度给接口用。
/// 只存城市名就得每次抓取都先查一次地理编码；只存经纬度则界面上认不出是哪儿。
/// 保存时用城市名反查一次经纬度，之后抓取只用经纬度。
/// </para>
/// </summary>
public sealed class WeatherConfigDto
{
    /// <summary>是否启用（关闭后不再抓取、也不下发给教室端）。</summary>
    public bool Enabled { get; set; }

    /// <summary>城市名（例如「杭州」）。保存时用它反查经纬度。</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>纬度；0 表示还没解析过。</summary>
    public double Latitude { get; set; }

    /// <summary>经度；0 表示还没解析过。</summary>
    public double Longitude { get; set; }

    /// <summary>抓取间隔（分钟，最小 10）。</summary>
    public int RefreshMinutes { get; set; } = 30;

    /// <summary>温度单位：<c>celsius</c> / <c>fahrenheit</c>。</summary>
    public string TemperatureUnit { get; set; } = "celsius";
}

/// <summary>
/// 天气状态（配置 + 最近一次抓取结果）。
/// <para>抓取失败时保留上一次成功的数据，同时把原因写进 <see cref="Error"/>——教室里看到的宁可旧一点，
/// 也不要突然变成空白。</para>
/// </summary>
public sealed class WeatherStateDto
{
    /// <summary>配置。</summary>
    public WeatherConfigDto Config { get; set; } = new();

    /// <summary>是否已有可用的天气数据。</summary>
    public bool HasData { get; set; }

    /// <summary>WMO 天气代码（前端据此选图标与文案）。</summary>
    public int? WeatherCode { get; set; }

    /// <summary>温度（单位见配置）。</summary>
    public double? Temperature { get; set; }

    /// <summary>温度单位（<c>°C</c> / <c>°F</c>）。</summary>
    public string TemperatureText { get; set; } = "°C";

    /// <summary>相对湿度（百分比）。</summary>
    public int? Humidity { get; set; }

    /// <summary>风速（km/h）。</summary>
    public double? WindSpeed { get; set; }

    /// <summary>数据时刻（服务端换算成服务器本地时间）。</summary>
    public string? ObservedAt { get; set; }

    /// <summary>最近一次抓取成功的时刻。</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>最近一次抓取失败的原因；成功时为空。</summary>
    public string? Error { get; set; }

    /// <summary>数据来源（便于排查「谁的天气」）。</summary>
    public string Source { get; set; } = "Open-Meteo";

    /// <summary>教室端是否应当显示（启用 + 有数据）。</summary>
    public bool Available => Config.Enabled && HasData;

    /// <summary>一行式的天气摘要，供教室端大屏直接显示（服务端拼好，插件不必再懂天气代码）。</summary>
    public string Summary { get; set; } = string.Empty;
}
