using System.Security.Cryptography;

namespace ControlHub.Server.Services;

/// <summary>
/// 管理员密码哈希工具。采用 PBKDF2-SHA256，每个密码独立随机盐。
/// 存储格式：<c>pbkdf2$sha256$&lt;迭代次数&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;</c>。
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const string Prefix = "pbkdf2$sha256";

    /// <summary>生成密码哈希。</summary>
    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>校验密码。使用固定时间比较以避免时序侧信道。</summary>
    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored))
        {
            return false;
        }

        var parts = stored.Split('$');
        if (parts.Length != 5 || parts[0] != "pbkdf2" || parts[1] != "sha256")
        {
            return false;
        }

        if (!int.TryParse(parts[2], out var iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>校验密码强度。返回 <c>null</c> 表示通过，否则返回不通过原因。</summary>
    public static string? ValidateStrength(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return "密码不能为空。";
        }

        if (password.Length < 8)
        {
            return "密码长度至少为 8 位。";
        }

        var hasLetter = password.Any(char.IsLetter);
        var hasDigit = password.Any(char.IsDigit);
        if (!hasLetter || !hasDigit)
        {
            return "密码需同时包含字母和数字。";
        }

        return null;
    }
}
