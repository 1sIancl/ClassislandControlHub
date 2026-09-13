using System.IO.Compression;
using System.Text.Json;
using ControlHub.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Services;

/// <summary>一个备份条目。</summary>
public sealed class BackupEntry
{
    /// <summary>备份目录名（唯一标识）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>备份类型：<c>manual</c> / <c>auto</c> / <c>update</c>。</summary>
    public string Type { get; set; } = "manual";

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>大小（字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>备注。</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// A 端应用数据备份服务。
/// <para>参照 ClassIsland 备份设计：把数据库与配置文件复制到 <c>data/Backups/&lt;类型&gt;_Backup_&lt;时间&gt;</c>，
/// 支持手动/自动/更新前备份，自动备份仅保留最近 <see cref="ServerOptions.BackupRetention"/> 份。</para>
/// </summary>
public sealed class BackupService(
    IOptions<ServerOptions> options,
    ILogger<BackupService> logger)
{
    private readonly ServerOptions _options = options.Value;

    private string Root => Path.Combine(_options.ResolveDataDirectory(AppContext.BaseDirectory), "Backups");

    /// <summary>创建一个备份（把数据目录中的关键文件复制到备份目录）。</summary>
    public BackupEntry CreateBackup(string type = "manual", string note = "")
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        var prefix = type switch
        {
            "auto" => "Auto_Backup",
            "update" => "Update_Backup",
            _ => "Backup",
        };
        var id = $"{prefix}_{stamp}";
        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);

        var dataDir = _options.ResolveDataDirectory(AppContext.BaseDirectory);
        var copied = new List<string>();

        // 关键文件 / 目录
        var files = Directory.Exists(dataDir)
            ? Directory.GetFiles(dataDir, "*", SearchOption.TopDirectoryOnly)
            : [];
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith("controlhub.db", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(f, Path.Combine(dir, name), overwrite: true);
                copied.Add(name);
            }
        }

        // 备份元数据
        var meta = new BackupEntry
        {
            Id = id,
            Type = type,
            CreatedAt = DateTimeOffset.UtcNow,
            Note = note,
        };
        File.WriteAllText(Path.Combine(dir, "backup.json"), JsonSerializer.Serialize(meta));

        var size = Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length);

        logger.LogInformation("已创建备份 {Id}（{Count} 个文件，{Size} 字节）。", id, copied.Count, size);

        PruneAutoBackups();
        return new BackupEntry { Id = id, Type = type, CreatedAt = meta.CreatedAt, SizeBytes = size, Note = note };
    }

    /// <summary>列出全部备份（按时间倒序）。</summary>
    public List<BackupEntry> ListBackups()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var result = new List<BackupEntry>();
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var id = Path.GetFileName(dir);
            var metaPath = Path.Combine(dir, "backup.json");
            BackupEntry entry;
            if (File.Exists(metaPath))
            {
                try
                {
                    entry = JsonSerializer.Deserialize<BackupEntry>(File.ReadAllText(metaPath)) ?? new BackupEntry();
                }
                catch
                {
                    entry = new BackupEntry();
                }
            }
            else
            {
                entry = new BackupEntry();
            }

            entry.Id = id;
            entry.SizeBytes = Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length);
            if (entry.CreatedAt == default)
            {
                entry.CreatedAt = Directory.GetCreationTimeUtc(dir);
            }

            result.Add(entry);
        }

        return result.OrderByDescending(e => e.CreatedAt).ToList();
    }

    /// <summary>删除一个备份。</summary>
    public bool DeleteBackup(string id)
    {
        var dir = SafeDir(id);
        if (dir is null || !Directory.Exists(dir))
        {
            return false;
        }

        Directory.Delete(dir, recursive: true);
        logger.LogInformation("已删除备份 {Id}。", id);
        return true;
    }

    /// <summary>把指定备份打包为 zip 字节（用于下载）。</summary>
    public byte[]? ExportBackupZip(string id)
    {
        var dir = SafeDir(id);
        if (dir is null || !Directory.Exists(dir))
        {
            return null;
        }

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                zip.CreateEntryFromFile(file, Path.GetFileName(file));
            }
        }

        return ms.ToArray();
    }

    /// <summary>从备份恢复数据库（覆盖当前 controlhub.db，需重启服务生效）。</summary>
    public bool RestoreBackup(string id)
    {
        var dir = SafeDir(id);
        if (dir is null || !Directory.Exists(dir))
        {
            return false;
        }

        var dataDir = _options.ResolveDataDirectory(AppContext.BaseDirectory);
        var source = Directory.GetFiles(dir, "controlhub.db*", SearchOption.TopDirectoryOnly);
        if (source.Length == 0)
        {
            logger.LogWarning("备份 {Id} 中不包含数据库文件。", id);
            return false;
        }

        // 恢复前先为当前数据做一次保护性备份
        CreateBackup("update", $"恢复 {id} 前的自动备份");

        foreach (var f in source)
        {
            File.Copy(f, Path.Combine(dataDir, Path.GetFileName(f)), overwrite: true);
        }

        logger.LogInformation("已从备份 {Id} 恢复数据库，需重启服务生效。", id);
        return true;
    }

    /// <summary>仅保留最近 N 份自动备份。</summary>
    public void PruneAutoBackups()
    {
        var keep = Math.Max(1, _options.BackupRetention);
        var autos = ListBackups()
            .Where(b => b.Type == "auto")
            .OrderByDescending(b => b.CreatedAt)
            .Skip(keep)
            .ToList();

        foreach (var b in autos)
        {
            DeleteBackup(b.Id);
        }
    }

    private string? SafeDir(string id)
    {
        // 防目录穿越：只允许文件名部分
        if (string.IsNullOrWhiteSpace(id) || id.Contains("..") || id.Contains('/') || id.Contains('\\'))
        {
            return null;
        }

        return Path.Combine(Root, id);
    }
}

/// <summary>自动备份后台服务（默认每 7 天一次，参照 ClassIsland）。</summary>
public sealed class AutoBackupService(
    BackupService backups,
    IOptions<ServerOptions> options,
    ILogger<AutoBackupService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.EnableAutoBackup)
        {
            return;
        }

        await Task.Yield();
        var interval = TimeSpan.FromHours(Math.Max(1, opts.AutoBackupIntervalHours));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                backups.CreateBackup("auto", "自动备份");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "自动备份失败。");
            }
        }
    }
}
