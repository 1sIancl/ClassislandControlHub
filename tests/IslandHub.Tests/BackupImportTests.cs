using System.IO.Compression;

using IslandHub.Protocol;
using IslandHub.Server.Options;
using IslandHub.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IslandHub.Tests;

/// <summary>
/// 导入备份文件（#125）。
///
/// <para>
/// 为什么要有单测：这条路径**吃的是外部文件**（用户上传的 zip / 加密包），
/// 最容易出事的恰好是"看起来没问题"的地方——zip 里塞 `../` 覆盖到别的目录（Zip Slip）、
/// 没有数据库的包被当成备份收下（列表里多一条点了没用的东西）、
/// 校验失败后把半截文件留在备份目录里。这些都能用临时目录逐个钉住。
/// </para>
/// </summary>
public sealed class BackupImportTests
{
    [Fact]
    public void Import_WithDatabase_BecomesAnImportedBackup()
    {
        using var fixture = new BackupFixture();
        var zip = Zip(("controlhub.db", "db"));

        var entry = fixture.Service.ImportBackupZip(zip, "来自旧服务器");

        Assert.StartsWith("Imported_", entry.Id);
        Assert.Equal("imported", entry.Type);
        Assert.Equal("来自旧服务器", entry.Note);
        Assert.Equal(1, entry.FileCount);
        Assert.False(entry.IncludesSecretsKey);

        var dir = Path.Combine(fixture.BackupsRoot, entry.Id);
        Assert.True(File.Exists(Path.Combine(dir, "controlhub.db")));
        Assert.True(File.Exists(Path.Combine(dir, "backup.json")));
        // 列表能读到它（元数据写对了，重启后也还在）。
        Assert.Contains(fixture.Service.ListBackups(), b => b.Id == entry.Id && b.Type == "imported");
    }

    [Fact]
    public void Import_MarksWhenSecretsKeyIsIncluded()
    {
        using var fixture = new BackupFixture();
        var zip = Zip(("controlhub.db", "db"), ("secrets.key", "k"));

        var entry = fixture.Service.ImportBackupZip(zip);

        // 含密钥的备份等于「能接管全部凭据」，界面上要靠这个标记给警告。
        Assert.True(entry.IncludesSecretsKey);
        Assert.Equal(2, entry.FileCount);
        Assert.False(string.IsNullOrWhiteSpace(entry.Note), "没给备注时也该有个默认说明，别在列表里留空");
    }

    [Fact]
    public void Import_RejectsZipWithoutDatabase_AndCleansUp()
    {
        using var fixture = new BackupFixture();
        var zip = Zip(("readme.txt", "not a backup"));
        var before = fixture.ExistingDirectories();

        var error = Assert.Throws<HubException>(() => fixture.Service.ImportBackupZip(zip));

        Assert.Contains("controlhub.db", error.Message);
        // 失败后不能留半截目录：否则列表里会出现一条"看着有、点了没用"的备份。
        Assert.Equal(before, fixture.ExistingDirectories());
    }

    [Fact]
    public void Import_RejectsPathTraversal()
    {
        using var fixture = new BackupFixture();
        // 经典 Zip Slip：解压时若直接拼路径，会写到备份目录外面去。
        var zip = Zip(("controlhub.db", "db"), ("../escaped.txt", "pwned"));
        var before = fixture.ExistingDirectories();

        var error = Assert.Throws<HubException>(() => fixture.Service.ImportBackupZip(zip));

        Assert.Contains("非法路径", error.Message);
        Assert.Equal(before, fixture.ExistingDirectories());
        Assert.False(File.Exists(Path.Combine(fixture.BackupsRoot, "escaped.txt")));
    }

    [Fact]
    public void Import_RejectsNonZipBytes()
    {
        using var fixture = new BackupFixture();

        var error = Assert.Throws<HubException>(
            () => fixture.Service.ImportBackupZip("这不是 zip"u8.ToArray()));

        Assert.Contains("有效的备份包", error.Message);
    }

    [Fact]
    public void EncryptedPackage_RoundTripsThroughDecryptThenImport()
    {
        using var fixture = new BackupFixture();
        var zip = Zip(("controlhub.db", "db"));

        // 端点的顺序就是：先认加密头 → 解密 → 交给 ImportBackupZip。
        var encrypted = BackupCrypto.Encrypt(zip, "correct-horse");
        Assert.True(BackupCrypto.IsEncrypted(encrypted));

        var decrypted = BackupCrypto.Decrypt(encrypted, "correct-horse");
        var entry = fixture.Service.ImportBackupZip(decrypted);

        Assert.Equal("imported", entry.Type);
        Assert.Equal(zip, decrypted);
        // 口令错了必须抛（AES-GCM 的认证标签会拦下），不能"解出一堆乱码"往下走。
        Assert.ThrowsAny<Exception>(() => BackupCrypto.Decrypt(encrypted, "wrong-pass"));
    }

    /// <summary>把若干「文件名 → 内容」打成内存 zip。</summary>
    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                var bytes = System.Text.Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        return ms.ToArray();
    }

    /// <summary>临时数据目录 + 一个真 BackupService。</summary>
    private sealed class BackupFixture : IDisposable
    {
        private readonly string _root;

        public BackupFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "ch-backup-import-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_root);
            var options = new ServerOptions { DataDirectory = _root };
            Service = new BackupService(new StaticOptionsMonitor<ServerOptions>(options),
                NullLogger<BackupService>.Instance);
        }

        public BackupService Service { get; }

        public string BackupsRoot => Path.Combine(_root, "Backups");

        /// <summary>当前备份目录下的条目（用来断言"失败后没留垃圾"）。</summary>
        public List<string> ExistingDirectories() =>
            Directory.Exists(BackupsRoot)
                ? Directory.GetDirectories(BackupsRoot)
                    .Select(d => Path.GetFileName(d)!)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList()
                : [];

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响结论。
            }
        }
    }
}
