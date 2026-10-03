using System.Security.Cryptography;
using ControlHub.Server.Services;

namespace ControlHub.Tests;

/// <summary>
/// 静态敏感数据加密（#36，见 ADR 0005）的回归测试。
/// <para>这段逻辑出错是**静默**的：值照样存取，但要么根本没加密、要么解密失败被当成空值继续用。
/// 因此这里钉住四件事：往返正确、加密幂等、明文兼容（老库）、密钥不一致 / 密文被篡改必须失败
/// （绝不能退回明文继续用）。</para>
/// </summary>
public sealed class SecretProtectorTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ch-secret-" + Guid.NewGuid().ToString("N"));

    private SecretProtector NewProtector(string fileName = "secrets.key") => new(Path.Combine(dir, fileName));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [Fact]
    public void 加密后可被同一密钥解密且带统一前缀()
    {
        var protector = NewProtector();

        var stored = protector.Protect("ABCD1234");

        Assert.StartsWith("enc:v1:", stored, StringComparison.Ordinal);
        Assert.True(SecretProtector.IsEncrypted(stored));
        Assert.True(protector.TryUnprotect(stored, out var plain, out var error));
        Assert.Null(error);
        Assert.Equal("ABCD1234", plain);
    }

    [Fact]
    public void 同一明文两次加密结果不同但都能解开()
    {
        // 随机 nonce：相同明文每次密文不同（这是刻意的，避免密文可被关联）。
        var protector = NewProtector();

        var first = protector.Protect("SAME");
        var second = protector.Protect("SAME");

        Assert.NotEqual(first, second);
        Assert.True(protector.TryUnprotect(first, out var a, out _));
        Assert.True(protector.TryUnprotect(second, out var b, out _));
        Assert.Equal("SAME", a);
        Assert.Equal("SAME", b);
    }

    [Fact]
    public void 对已加密的值再次加密保持原样()
    {
        // 幂等：Webhook 每次保存都会走一次 Protect，如果会二次加密，值就再也解不回来了。
        var protector = NewProtector();

        var stored = protector.Protect("SECRET");
        Assert.Equal(stored, protector.Protect(stored));
    }

    [Fact]
    public void 老库里的明文值按原样返回()
    {
        // 兼容路径：升级前写入的明文值必须继续可用，否则老库一升级注册功能就全挂。
        var protector = NewProtector();

        Assert.True(protector.TryUnprotect("PLAIN-LEGACY", out var plain, out var error));
        Assert.Null(error);
        Assert.Equal("PLAIN-LEGACY", plain);
        Assert.False(SecretProtector.IsEncrypted("PLAIN-LEGACY"));
    }

    [Fact]
    public void 空值往返不报错()
    {
        var protector = NewProtector();

        Assert.True(protector.TryUnprotect(string.Empty, out var plain, out _));
        Assert.Equal(string.Empty, plain);
    }

    [Fact]
    public void 换了密钥必须解密失败而不是退回明文()
    {
        var first = NewProtector("key-a.key");
        var second = NewProtector("key-b.key");
        var stored = first.Protect("TOP-SECRET");

        Assert.False(second.TryUnprotect(stored, out var plain, out var error));
        Assert.Null(plain);
        Assert.NotNull(error);
        Assert.Throws<CryptographicException>(() => second.Unprotect(stored));
    }

    [Fact]
    public void 密文被篡改必须解密失败()
    {
        var protector = NewProtector();
        var stored = protector.Protect("TAMPER-ME");

        // 翻转密文段的一个字符（GCM 认证标签会察觉）。
        var parts = stored["enc:v1:".Length..].Split(':');
        var cipher = parts[1];
        var flipped = (cipher[0] == 'A' ? 'B' : 'A') + cipher[1..];
        var tampered = $"enc:v1:{parts[0]}:{flipped}:{parts[2]}";

        Assert.False(protector.TryUnprotect(tampered, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void 格式错误的密文按解密失败处理()
    {
        var protector = NewProtector();

        Assert.False(protector.TryUnprotect("enc:v1:only-one-part", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void 密钥文件损坏时抛错而不是静默新建密钥()
    {
        // 静默换一把新密钥会让所有已加密数据变成「解不开」，而且密钥文件被悄悄覆盖、再也救不回来。
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "secrets.key"), "这不是合法的 Base64！");

        var protector = NewProtector();
        Assert.Throws<FormatException>(() => protector.Protect("X"));
    }

    [Fact]
    public void 指纹哈希是确定性的且换密钥后不同()
    {
        var first = NewProtector("key-a.key");
        var second = NewProtector("key-b.key");

        var hash = first.ComputeLookupHash("ABCD1234");
        Assert.Equal(hash, first.ComputeLookupHash("ABCD1234"));
        Assert.NotEqual(hash, second.ComputeLookupHash("ABCD1234"));
    }

    [Fact]
    public void 密钥文件默认放在指定路径并可自动创建()
    {
        var path = Path.Combine(dir, "nested", "secrets.key");
        var protector = new SecretProtector(path);

        protector.Protect("X");

        Assert.True(File.Exists(path));
        Assert.Equal(path, protector.KeyPath);
        Assert.True(protector.KeyGenerated);
    }
}
