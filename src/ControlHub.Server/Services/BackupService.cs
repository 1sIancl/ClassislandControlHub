using System.IO.Compression;
using System.Text.Json;
using ControlHub.Protocol.Dtos;
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

    /// <summary>备份内含的文件数（#59）。</summary>
    public int FileCount { get; set; }

    /// <summary>是否包含加密密钥 <c>secrets.key</c>（#59）。</summary>
    public bool IncludesSecretsKey { get; set; }
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

    /// <summary>
    /// 创建一个备份（把数据目录中的文件按选项复制到备份目录，并记录实际内容）。
    /// </summary>
    /// <param name="type">备份类型：manual / auto / update。</param>
    /// <param name="note">备注。</param>
    /// <param name="options">
    /// 可选内容（#59）。省略时按默认：**数据库 + 配置文件，不含加密密钥与本机令牌**——
    /// 密钥本身就是凭据，把它一起放进备份，等于谁拿到备份谁就拿到了注册码 / Webhook 密钥。
    /// 换机器恢复时才需要显式勾上。
    /// </param>
    public BackupEntry CreateBackup(string type = "manual", string note = "", BackupOptionsDto? options = null)
    {
        var opts = options ?? new BackupOptionsDto();

        // 精确到毫秒，并做一次存在性检查兜底：**同一秒内连点两次「新建备份」不能落到同一个目录**——
        // 那样后一次会覆盖前一次，甚至把两份内容混在一个目录里（复制是覆盖写、不删旧文件），
        // 恢复出来的东西就说不清是什么了。本轮实测正是靠这个才发现了旧写法的问题。
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fff");
        var prefix = type switch
        {
            "auto" => "Auto_Backup",
            "update" => "Update_Backup",
            _ => "Backup",
        };

        var id = $"{prefix}_{stamp}";
        if (Directory.Exists(Path.Combine(Root, id)))
        {
            id = $"{prefix}_{stamp}_{Guid.NewGuid().ToString("N")[..4]}";
        }

        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);

        var dataDir = _options.ResolveDataDirectory(AppContext.BaseDirectory);
        var copied = new List<string>();

        // ① 数据库：没有它这份备份没有意义，不接受关闭。
        foreach (var f in Directory.Exists(dataDir)
                     ? Directory.GetFiles(dataDir, "controlhub.db*", SearchOption.TopDirectoryOnly)
                     : [])
        {
            var name = Path.GetFileName(f);
            File.Copy(f, Path.Combine(dir, name), overwrite: true);
            copied.Add(name);
        }

        // ② 配置文件（默认包含）。
        if (opts.IncludeConfigFiles && Directory.Exists(dataDir))
        {
            foreach (var f in Directory.GetFiles(dataDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(f);
                File.Copy(f, Path.Combine(dir, name), overwrite: true);
                copied.Add(name);
            }
        }

        // ③ 加密密钥（默认不含；勾上就是「这份备份能直接接管全部凭据」，界面必须警示）。
        var includesSecretsKey = false;
        var keyPath = Path.Combine(dataDir, "secrets.key");
        if (opts.IncludeSecretsKey && File.Exists(keyPath))
        {
            File.Copy(keyPath, Path.Combine(dir, "secrets.key"), overwrite: true);
            copied.Add("secrets.key");
            includesSecretsKey = true;
        }

        // ④ 本机免登录令牌（默认不含：它只对「本机回环 + 该令牌」有效，换机器带着也没用，还多一份凭据在外面）。
        var tokenPath = Path.Combine(dataDir, "local-shell.token");
        if (opts.IncludeLocalShellToken && File.Exists(tokenPath))
        {
            File.Copy(tokenPath, Path.Combine(dir, "local-shell.token"), overwrite: true);
            copied.Add("local-shell.token");
        }

        // 备份元数据：把「这份备份里到底有什么」记下来，界面据此提示能不能直接拿去换机器。
        var meta = new BackupEntry
        {
            Id = id,
            Type = type,
            CreatedAt = DateTimeOffset.UtcNow,
            Note = note,
            FileCount = copied.Count,
            IncludesSecretsKey = includesSecretsKey,
        };
        File.WriteAllText(Path.Combine(dir, "backup.json"), JsonSerializer.Serialize(meta));

        var size = Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length);

        logger.LogInformation("已创建备份 {Id}（{Count} 个文件，{Size} 字节，含密钥 {Key}）。",
            id, copied.Count, size, includesSecretsKey);

        PruneAutoBackups();
        return new BackupEntry
        {
            Id = id,
            Type = type,
            CreatedAt = meta.CreatedAt,
            SizeBytes = size,
            Note = note,
            FileCount = copied.Count,
            IncludesSecretsKey = includesSecretsKey,
        };
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

        // 恢复前先为当前数据做一次保护性备份。
        // **必须包含密钥**：否则这次恢复一旦把 secrets.key 换掉，旧密钥就再也回不来，
        // 「回滚」也就失去了意义（数据还在，但全解不开）。
        CreateBackup("update", $"恢复 {id} 前的自动备份", new BackupOptionsDto
        {
            IncludeSecretsKey = true,
            IncludeLocalShellToken = true,
        });

        foreach (var f in source)
        {
            File.Copy(f, Path.Combine(dataDir, Path.GetFileName(f)), overwrite: true);
        }

        // 备份里带了密钥就一并恢复（#59）：否则恢复出来的库解不开注册码 / Webhook 密钥，
        // 现场表现就是「恢复之后注册码全部不可用」。
        var keyFile = Path.Combine(dir, "secrets.key");
        if (File.Exists(keyFile))
        {
            File.Copy(keyFile, Path.Combine(dataDir, "secrets.key"), overwrite: true);
            logger.LogInformation("备份 {Id} 包含加密密钥，已一并恢复。", id);
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
