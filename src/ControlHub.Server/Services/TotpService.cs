using System.Security.Cryptography;
using System.Text;

namespace ControlHub.Server.Services;

/// <summary>
/// TOTP（基于时间的一次性密码，RFC 6238）实现，用于管理账号的两步验证。
/// <para>
/// 参数取业界默认：30 秒一步、6 位数字、HMAC-SHA1，
/// 与 Google Authenticator、Microsoft Authenticator、1Password 等常见验证器兼容。
/// </para>
/// <para>不依赖任何第三方库：Base32 与 HMAC 都是标准实现，避免为一个功能引入额外依赖。</para>
/// </summary>
public static class TotpService
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>密钥长度（字节）：160 位，SHA1 的推荐长度。</summary>
    private const int SecretBytes = 20;

    /// <summary>时间步长（秒）。</summary>
    public const int StepSeconds = 30;

    /// <summary>验证码位数。</summary>
    public const int Digits = 6;

    /// <summary>生成一个新的 Base32 密钥。</summary>
    public static string GenerateSecret() => ToBase32(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>
    /// 校验 6 位验证码。
    /// </summary>
    /// <param name="secret">Base32 密钥。</param>
    /// <param name="code">用户输入的验证码（允许含空格）。</param>
    /// <param name="window">允许前后各多少个时间步，用于容忍设备时钟偏差（默认 1，即 ±30 秒）。</param>
    public static bool Verify(string? secret, string? code, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var normalized = new string(code.Where(char.IsDigit).ToArray());
        if (normalized.Length != Digits)
        {
            return false;
        }

        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;
        for (var offset = -window; offset <= window; offset++)
        {
            if (string.Equals(ComputeCode(secret, counter + offset), normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>按时间步计数器计算验证码。</summary>
    public static string ComputeCode(string secret, long counter)
    {
        var key = FromBase32(secret);
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);

        // 动态截断（RFC 4226 §5.3）：取末字节低 4 位作为偏移。
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);

        var value = binary % (int)Math.Pow(10, Digits);
        return value.ToString(new string('0', Digits));
    }

    /// <summary>生成 otpauth:// 链接，验证器 App 可直接扫码或手动录入。</summary>
    public static string BuildOtpAuthUrl(string issuer, string account, string secret)
        => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
           + $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}"
           + $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    private static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bitsLeft - 5)) & 0x1F]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }

        return sb.ToString();
    }

    private static byte[] FromBase32(string text)
    {
        var clean = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(clean.Length * 5 / 8);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var c in clean)
        {
            var index = Base32Alphabet.IndexOf(c);
            if (index < 0)
            {
                continue;   // 忽略非 Base32 字符，避免手工录入时分隔符导致失败
            }

            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }

        return bytes.ToArray();
    }
}
