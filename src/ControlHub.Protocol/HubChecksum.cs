using System.Security.Cryptography;
using System.Text;

namespace ControlHub.Protocol;

/// <summary>
/// 内容校验和工具。用于下发内容包的完整性校验。
/// </summary>
public static class HubChecksum
{
    /// <summary>校验和前缀。</summary>
    public const string Prefix = "sha256:";

    /// <summary>计算字符串的 SHA-256 校验和，返回 <c>sha256:&lt;hex&gt;</c>。</summary>
    public static string Compute(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Prefix + Convert.ToHexStringLower(hash);
    }

    /// <summary>基于对象的规范化 JSON 计算校验和。</summary>
    public static string ComputeOf<T>(T value) => Compute(HubJson.Serialize(value));

    /// <summary>
    /// 校验内容是否与校验和匹配。
    /// </summary>
    public static bool Verify(string content, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        var actual = Compute(content);
        return string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>生成随机令牌（URL 安全 Base64）。</summary>
    public static string NewToken(int byteLength = 32)
    {
        var buffer = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToBase64String(buffer)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// 生成注册码。字符集去除了易混淆的 0/O/1/I，便于口头/手写传达。
    /// </summary>
    public static string NewEnrollCode(int length = HubProtocol.EnrollCodeLength)
    {
        var alphabet = HubProtocol.EnrollCodeAlphabet;
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>生成新的稳定标识（GUID 字符串）。</summary>
    public static string NewId() => Guid.NewGuid().ToString("D");
}
