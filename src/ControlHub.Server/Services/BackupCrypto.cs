using System.Security.Cryptography;
using System.Text;

namespace ControlHub.Server.Services;

/// <summary>
/// 备份文件加密（#60）。
///
/// 为什么需要：备份是「服务器全部数据的副本」，放在U盘、网盘、或发给别人做迁移时，
/// 明文 zip 等于把整台服务器的数据摊开给任何拿到文件的人。这里用一个**口令**把它加密。
///
/// 格式（自描述，便于以后换算法）：
/// <code>
///   "CHB1" | salt(16) | nonce(12) | tag(16) | ciphertext(...)
/// </code>
/// 密钥用 PBKDF2-SHA256（20 万次）从口令派生；加密用 AES-256-GCM（带认证标签，
/// 密码错、文件被改都会在解密时直接失败，而不是解出一堆乱码）。
///
/// <b>口令不进日志、不进 URL</b>：导出走 POST body，解密走命令行交互输入。
/// 口令丢失则备份无法恢复，这一点必须让用户明确知道。
/// </summary>
public static class BackupCrypto
{
    /// <summary>文件头魔数：既是格式版本，也用于判断「这是不是加密备份」。</summary>
    private static readonly byte[] Magic = "CHB1"u8.ToArray();

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    /// <summary>PBKDF2 迭代次数。20 万次在现代 CPU 上约几十毫秒，够用且不至于让人等到烦。</summary>
    private const int Iterations = 200_000;

    /// <summary>数据是否是本格式的加密备份。</summary>
    public static bool IsEncrypted(ReadOnlySpan<byte> data) =>
        data.Length > Magic.Length && data[..Magic.Length].SequenceEqual(Magic);

    /// <summary>加密（口令为空时直接抛错——静默降级成明文是最危险的行为）。</summary>
    public static byte[] Encrypt(byte[] plaintext, string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new ArgumentException("加密口令不能为空。", nameof(passphrase));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(passphrase, salt);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var result = new byte[Magic.Length + SaltSize + NonceSize + TagSize + ciphertext.Length];
        var offset = 0;
        Magic.CopyTo(result, offset);
        offset += Magic.Length;
        salt.CopyTo(result, offset);
        offset += SaltSize;
        nonce.CopyTo(result, offset);
        offset += NonceSize;
        tag.CopyTo(result, offset);
        offset += TagSize;
        ciphertext.CopyTo(result, offset);
        return result;
    }

    /// <summary>
    /// 解密。口令错误或文件被改动都会抛 <see cref="CryptographicException"/>——
    /// GCM 的认证标签保证了「解不出来就是解不出来」，不会给出看起来正常的错误数据。
    /// </summary>
    public static byte[] Decrypt(byte[] payload, string passphrase)
    {
        if (!IsEncrypted(payload))
        {
            throw new CryptographicException("这不是加密备份文件（缺少 CHB1 文件头）。");
        }

        var offset = Magic.Length;
        var salt = payload.AsSpan(offset, SaltSize).ToArray();
        offset += SaltSize;
        var nonce = payload.AsSpan(offset, NonceSize).ToArray();
        offset += NonceSize;
        var tag = payload.AsSpan(offset, TagSize).ToArray();
        offset += TagSize;

        var ciphertext = payload.AsSpan(offset).ToArray();
        var plaintext = new byte[ciphertext.Length];
        var key = DeriveKey(passphrase, salt);

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return plaintext;
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>生成加密备份的下载文件名：<c>&lt;备份ID&gt;.zip.enc</c>。</summary>
    public static string EncryptedFileName(string backupId) => $"{backupId}.zip.enc";
}
