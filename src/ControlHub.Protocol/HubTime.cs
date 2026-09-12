using System.Globalization;

namespace ControlHub.Protocol;

/// <summary>
/// 时间字符串与 <see cref="TimeSpan"/> 之间的转换工具。
/// 协议中所有时刻统一使用 <c>HH:mm:ss</c> 格式，避免时区与序列化差异。
/// </summary>
public static class HubTime
{
    /// <summary>协议的规范时间格式。</summary>
    public const string Format = @"hh\:mm\:ss";

    /// <summary>把 <see cref="TimeSpan"/> 格式化为 <c>HH:mm:ss</c>。</summary>
    public static string ToText(TimeSpan value) =>
        value.ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>把 <see cref="TimeOnly"/> 格式化为 <c>HH:mm:ss</c>。</summary>
    public static string ToText(TimeOnly value) =>
        value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// 解析时间字符串。接受 <c>H:mm</c>、<c>HH:mm</c>、<c>HH:mm:ss</c> 等形式，
    /// 解析失败时返回 <see cref="TimeSpan.Zero"/>。
    /// </summary>
    public static TimeSpan Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TimeSpan.Zero;
        }

        var t = text.Trim();
        if (TimeSpan.TryParseExact(t, Format, CultureInfo.InvariantCulture, out var exact))
        {
            return Normalize(exact);
        }

        if (TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var loose))
        {
            return Normalize(loose);
        }

        if (TimeOnly.TryParse(t, CultureInfo.InvariantCulture, out var only))
        {
            return only.ToTimeSpan();
        }

        return TimeSpan.Zero;
    }

    /// <summary>尝试解析时间字符串，失败返回 <c>false</c>。</summary>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = TimeSpan.Zero;
            return false;
        }

        var t = text.Trim();
        if (TimeSpan.TryParseExact(t, Format, CultureInfo.InvariantCulture, out var exact))
        {
            value = Normalize(exact);
            return true;
        }

        if (TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var loose))
        {
            value = Normalize(loose);
            return true;
        }

        if (TimeOnly.TryParse(t, CultureInfo.InvariantCulture, out var only))
        {
            value = only.ToTimeSpan();
            return true;
        }

        value = TimeSpan.Zero;
        return false;
    }

    /// <summary>规范化为 <c>00:00:00</c>~<c>23:59:59</c> 区间内的一天内时刻。</summary>
    public static TimeSpan Normalize(TimeSpan value)
    {
        var seconds = (long)value.TotalSeconds % 86400;
        if (seconds < 0)
        {
            seconds += 86400;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>校验字符串是否是可解析的合法时刻。</summary>
    public static bool IsValid(string? text) =>
        !string.IsNullOrWhiteSpace(text) && TryParse(text, out _);
}
