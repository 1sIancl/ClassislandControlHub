namespace ControlHub.Server.Services;

/// <summary>
/// NTP 64 位定点时间戳（1900 纪元）的编解码，供 NTP 客户端与 NTP 服务器共用。
/// </summary>
internal static class NtpTimestamp
{
    private static readonly DateTime Epoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>把 NTP 64 位定点时间戳解析为 UTC 时间。</summary>
    public static DateTime FromUInt64(ulong ntp)
    {
        var seconds = (uint)(ntp >> 32);
        var fraction = (uint)(ntp & 0xFFFFFFFF);
        return Epoch.AddSeconds(seconds).AddSeconds(fraction / 4294967296.0);
    }

    /// <summary>把 UTC 时间编码为 NTP 64 位定点时间戳。</summary>
    public static ulong ToUInt64(DateTime utc)
    {
        var totalSeconds = (utc - Epoch).TotalSeconds;
        var seconds = (uint)Math.Floor(totalSeconds);
        var fraction = (uint)Math.Round((totalSeconds - seconds) * 4294967296.0);
        return ((ulong)seconds << 32) | fraction;
    }

    public static ulong ReadUInt64BigEndian(byte[] buffer, int offset)
    {
        ulong value = 0;
        for (var i = 0; i < 8; i++)
        {
            value = (value << 8) | buffer[offset + i];
        }

        return value;
    }

    public static void WriteUInt64BigEndian(byte[] buffer, int offset, ulong value)
    {
        for (var i = 7; i >= 0; i--)
        {
            buffer[offset + i] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }
}
