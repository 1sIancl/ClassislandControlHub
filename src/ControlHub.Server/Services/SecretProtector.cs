using System.Security.Cryptography;
using System.Text;

namespace ControlHub.Server.Services;

/// <summary>
/// 静态敏感数据加密（见 <c>docs/adr/0005-encrypt-secrets-at-rest.md</c>，路线图 #36）。
/// <para>保护对象：注册邀请码、Webhook 加签密钥等**必须能还原**的值（密码与 API 密钥是哈希，不在范围内）。</para>
/// <para><b>存储格式</b>：<c>enc:v1:&lt;base64(nonce)&gt;:&lt;base64(ciphertext)&gt;:&lt;base64(tag)&gt;</c>。
/// 读取时按前缀判断，因此**老库里的明文值可以直接继续使用**，不需要停机迁移；
/// 启动迁移会把明文就地升级成密文（幂等）。</para>
/// <para><b>能力边界（务必知悉）</b>：密钥文件与数据库同处一台机器，攻击者若同时拿到
/// <c>hub.db</c> 与 <c>secrets.key</c>，本方案不提供额外保护。它的目标是让「只拷走数据库/备份」
/// 这一最常见的事故不再等于泄露可用凭据。因此部署时**不要把密钥文件放进会被外发的备份**里。</para>
/// </summary>
public sealed class SecretProtector
{
    /// <summary>密文前缀与版本号。升级算法时新增 <c>v2</c>，读取侧同时支持。</summary>
    private const string Prefix = "enc:v1:";

    private const int KeySize = 32;   // AES-256
    private const int NonceSize = 12; // GCM 推荐 96 位
    private const int TagSize = 16;

    private readonly object gate = new();
    private readonly string keyPath;
    private byte[]? key;

    /// <summary>创建实例。</summary>
    /// <param name="keyPath">
    /// 密钥文件路径。为空时取 <c>data/secrets.key</c>（与数据库同目录）。
    /// 可通过 <c>ControlHub:SecretsKeyPath</c> 指到数据库目录之外的位置。
    /// </param>
    public SecretProtector(string? keyPath = null)
    {
        this.keyPath = string.IsNullOrWhiteSpace(keyPath)
            ? Path.Combine(AppContext.BaseDirectory, "data", "secrets.key")
            : keyPath.Trim();
    }

    /// <summary>实际使用的密钥文件路径（日志与诊断用）。</summary>
    public string KeyPath => keyPath;

    /// <summary>密钥文件是否是本次启动新建的（首次部署时为 true，便于启动日志提示「请妥善保管/排除出备份」）。</summary>
    public bool KeyGenerated { get; private set; }

    /// <summary>该值是否为本组件写出的密文（老库里的明文返回 false）。</summary>
    public static bool IsEncrypted(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>加密一个明文值。已经是密文时原样返回（幂等，避免二次加密）。</summary>
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (IsEncrypted(plaintext))
        {
            return plaintext;
        }

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(EnsureKey(), TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        return Prefix + Convert.ToBase64String(nonce) + ':' + Convert.ToBase64String(cipher) + ':'
               + Convert.ToBase64String(tag);
    }

    /// <summary>
    /// 解密一个存储值：密文则解密，明文则原样返回（兼容老库）。
    /// </summary>
    /// <param name="stored">数据库里的值。</param>
    /// <param name="plaintext">解密结果；失败时为 null。</param>
    /// <param name="error">失败原因（用于诊断，不含敏感内容）。</param>
    /// <returns>是否可用。**解密失败返回 false，调用方必须按「该值不可用」处理，
    /// 绝不能退回明文继续用**——那会让加密变成摆设。</returns>
    public bool TryUnprotect(string? stored, out string? plaintext, out string? error)
    {
        plaintext = null;
        error = null;

        if (string.IsNullOrEmpty(stored))
        {
            plaintext = string.Empty;
            return true;
        }

        if (!IsEncrypted(stored))
        {
            // 兼容：老库里的明文值直接可用（启动迁移会把它升级成密文）。
            plaintext = stored;
            return true;
        }

        var parts = stored[Prefix.Length..].Split(':');
        if (parts.Length != 3)
        {
            error = "密文格式不正确（缺少 nonce/密文/标签之一）。";
            return false;
        }

        try
        {
            var nonce = Convert.FromBase64String(parts[0]);
            var cipher = Convert.FromBase64String(parts[1]);
            var tag = Convert.FromBase64String(parts[2]);
            if (nonce.Length != NonceSize || tag.Length != TagSize)
            {
                error = "密文长度异常。";
                return false;
            }

            var plainBytes = new byte[cipher.Length];
            using var aes = new AesGcm(EnsureKey(), TagSize);
            aes.Decrypt(nonce, cipher, tag, plainBytes);
            plaintext = Encoding.UTF8.GetString(plainBytes);
            return true;
        }
        catch (CryptographicException)
        {
            // 认证失败：密钥已更换或密钥文件被替换/丢失。此时**不能**回退明文。
            error = "解密失败：密钥文件已更换或与写入时不一致。";
            return false;
        }
        catch (FormatException)
        {
            error = "密文不是合法的 Base64。";
            return false;
        }
    }

    /// <summary>
    /// 基于密钥文件的**确定性** HMAC-SHA256（Base64）。用于「既要能精确查找、又不能明文落库」的值，
    /// 例如注册码指纹 <c>code_hash</c>（见 ADR 0005「实现期修正」）。
    /// <para>不用裸 SHA-256 的原因：注册码通常只有 8 位左右，裸哈希可被离线爆破；
    /// 加 HMAC 后，只拿到数据库也推不出注册码。</para>
    /// </summary>
    public string ComputeLookupHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var hmac = new HMACSHA256(EnsureKey());
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>解密；失败时抛出（调用方需要区分失败原因时请用 <see cref="TryUnprotect"/>）。</summary>
    public string Unprotect(string? stored)
    {
        if (TryUnprotect(stored, out var plaintext, out var error))
        {
            return plaintext ?? string.Empty;
        }

        throw new CryptographicException(error);
    }

    /// <summary>
    /// 载入密钥；不存在则生成（首次启动）。密钥文件是 32 字节随机数的 Base64，一行。
    /// </summary>
    private byte[] EnsureKey()
    {
        if (key is not null)
        {
            return key;
        }

        lock (gate)
        {
            if (key is not null)
            {
                return key;
            }

            if (File.Exists(keyPath))
            {
                var text = File.ReadAllText(keyPath, Encoding.UTF8).Trim();
                var loaded = Convert.FromBase64String(text);
                if (loaded.Length != KeySize)
                {
                    throw new CryptographicException(
                        $"密钥文件长度不正确（应为 {KeySize} 字节）：{keyPath}");
                }

                key = loaded;
                return key;
            }

            var created = new byte[KeySize];
            RandomNumberGenerator.Fill(created);
            var directory = Path.GetDirectoryName(keyPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(keyPath, Convert.ToBase64String(created), new UTF8Encoding(false));
            TryRestrictPermissions(keyPath);
            KeyGenerated = true;
            key = created;
            return key;
        }
    }

    /// <summary>尽量把密钥文件权限收紧为「仅当前用户」（Linux/macOS）。Windows 上依赖目录 ACL。</summary>
    private static void TryRestrictPermissions(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // 收紧失败不影响功能：只在部署说明里要求自行限制目录权限。
        }
    }
}
