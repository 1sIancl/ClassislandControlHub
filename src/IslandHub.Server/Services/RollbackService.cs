using System.IO.Compression;
using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Options;
using Microsoft.Extensions.Options;

namespace IslandHub.Server.Services;

/// <summary>
/// 程序文件快照与回滚（路线图 #89）。
///
/// <para>
/// 自动更新把程序文件整体换掉、**不保留旧版本**，于是「更新完发现新版有问题」只能重装。
/// 这里在更新前把当前程序文件打成一个 zip（**不含数据目录**——数据本来就不动），
/// 需要时一键换回去；**回滚前还会再拍一份**，所以「回滚」本身也能撤销。
/// </para>
///
/// <para>
/// ⚠️ 两个真机限制写在实现里：① Windows 上正在运行的 dll 无法覆盖，这些文件会被跳过并在
/// 结果里报 <see cref="RollbackResultDto.Skipped"/>（Linux 上 systemd 重启后即可完成）；
/// ② 只留最近 <see cref="KeepSnapshots"/> 份，避免快照把磁盘吃满。
/// </para>
/// </summary>
public sealed class RollbackService(
    IOptionsMonitor<ServerOptions> options,
    IHostEnvironment environment,
    ILogger<RollbackService> logger)
{
    /// <summary>保留的快照份数（更新前 / 回滚前都会拍，留 3 份够覆盖一次折腾）。</summary>
    private const int KeepSnapshots = 3;

    /// <summary>程序目录（部署目录）。</summary>
    private string AppDirectory => Path.GetFullPath(environment.ContentRootPath);

    /// <summary>快照目录：放在数据目录下的 <c>rollback</c>（不随程序文件被覆盖）。</summary>
    private string SnapshotDirectory =>
        Path.Combine(Path.GetFullPath(options.CurrentValue.ResolveDataDirectory(AppDirectory)), "rollback");

    /// <summary>列出可用快照（新的在前）。</summary>
    public IReadOnlyList<RollbackSnapshotDto> List()
    {
        if (!Directory.Exists(SnapshotDirectory))
        {
            return [];
        }

        var items = new List<RollbackSnapshotDto>();
        foreach (var zip in Directory.EnumerateFiles(SnapshotDirectory, "*.zip"))
        {
            var dto = ReadMeta(Path.GetFileNameWithoutExtension(zip));
            if (dto is null)
            {
                continue;
            }

            items.Add(dto);
        }

        return items.OrderByDescending(i => i.CreatedAt).ToList();
    }

    /// <summary>
    /// 给当前程序文件拍一份快照。
    /// <para>失败**不抛异常**：快照是「保险」，保险没买上不该让更新本身失败（但要留日志）。</para>
    /// </summary>
    public RollbackSnapshotDto? Create(string reason)
    {
        try
        {
            Directory.CreateDirectory(SnapshotDirectory);

            var now = DateTimeOffset.Now;
            // 带一段随机后缀：同一秒内连拍两份（更新前 + 回滚前，或连续点两次）会撞名互相覆盖。
            var id = $"rollback-{HubProtocol.ProductVersion}-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            var zipPath = Path.Combine(SnapshotDirectory, id + ".zip");
            var files = CollectAppFiles();

            using (var stream = File.Create(zipPath))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    // 用相对路径当条目名，解压回来才能原样铺回程序目录。
                    zip.CreateEntryFromFile(file, Path.GetRelativePath(AppDirectory, file), CompressionLevel.Fastest);
                }
            }

            var dto = new RollbackSnapshotDto
            {
                Id = id,
                Version = HubProtocol.ProductVersion,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
                SizeBytes = new FileInfo(zipPath).Length,
                FileCount = files.Count,
            };
            File.WriteAllText(Path.Combine(SnapshotDirectory, id + ".json"), HubJson.Serialize(dto));
            Prune();

            logger.LogInformation("已创建程序文件快照 {Id}（{Reason}，{Files} 个文件，{Size:F1} MB）。",
                id, reason, dto.FileCount, dto.SizeBytes / 1024d / 1024d);
            return dto;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "创建程序文件快照失败（{Reason}）——不影响本次操作。", reason);
            return null;
        }
    }

    /// <summary>把程序文件换回指定快照。</summary>
    public RollbackResultDto Restore(string id)
    {
        var zipPath = Path.Combine(SnapshotDirectory, id + ".zip");
        if (!File.Exists(zipPath))
        {
            return new RollbackResultDto
            {
                Ok = false,
                SnapshotId = id,
                Message = "找不到这份快照（可能已被清理）。",
            };
        }

        // 回滚前先拍一份当前状态：这样「回滚」本身也能撤销（踩过的现实是——回滚到的是更旧的版本，
        // 有时候反而是不该回的那一份）。
        Create(RollbackReasons.PreRollback);

        var temp = Path.Combine(Path.GetTempPath(), $"islandhub-rollback-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temp);
            ZipFile.ExtractToDirectory(zipPath, temp);

            var (replaced, skipped) = CopyIntoApp(temp);
            var ok = replaced > 0;
            logger.LogInformation("回滚到快照 {Id}：替换 {Replaced} 个文件，跳过 {Skipped} 个（被占用）。",
                id, replaced, skipped);

            return new RollbackResultDto
            {
                Ok = ok,
                SnapshotId = id,
                Replaced = replaced,
                Skipped = skipped,
                // 只要有文件被替换就建议重启：进程里跑的还是旧的内存镜像，
                // 不重启的话「看到的版本号」和磁盘上的文件会对不上。
                RestartRequired = ok,
                Message = skipped > 0
                    ? $"已换回 {replaced} 个文件，另有 {skipped} 个正在使用被跳过；重启服务后完成回滚。"
                    : $"已换回 {replaced} 个文件，重启服务后生效。",
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "回滚到快照 {Id} 失败。", id);
            return new RollbackResultDto { Ok = false, SnapshotId = id, Message = $"回滚失败：{ex.Message}" };
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* 忽略 */ }
        }
    }

    // ────────────────────────────── 内部 ──────────────────────────────

    /// <summary>要打包的程序文件：整个程序目录，**排除数据目录**。</summary>
    private List<string> CollectAppFiles()
    {
        var dataDirectory = Path.GetFullPath(options.CurrentValue.ResolveDataDirectory(AppDirectory));
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppDirectory, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFullPath(file).StartsWith(dataDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add(file);
        }

        return files;
    }

    /// <summary>把解压出来的文件铺回程序目录，跳过数据目录；被占用的文件计数而不是中断。</summary>
    private (int Replaced, int Skipped) CopyIntoApp(string sourceDirectory)
    {
        var dataDirectory = Path.GetFullPath(options.CurrentValue.ResolveDataDirectory(AppDirectory));
        var replaced = 0;
        var skipped = 0;

        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDirectory, source);
            var target = Path.GetFullPath(Path.Combine(AppDirectory, rel));
            if (target.StartsWith(dataDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                replaced++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                logger.LogDebug(ex, "回滚时跳过被占用的文件：{File}", target);
            }
        }

        return (replaced, skipped);
    }

    /// <summary>读快照的元信息；zip 在但元信息丢了就按文件名兜底解析。</summary>
    private RollbackSnapshotDto? ReadMeta(string id)
    {
        var metaPath = Path.Combine(SnapshotDirectory, id + ".json");
        var zipPath = Path.Combine(SnapshotDirectory, id + ".zip");
        if (!File.Exists(zipPath))
        {
            return null;
        }

        if (File.Exists(metaPath))
        {
            var dto = HubJson.DeserializeOrDefault<RollbackSnapshotDto?>(File.ReadAllText(metaPath), null);
            if (dto is not null)
            {
                return dto;
            }
        }

        var info = new FileInfo(zipPath);
        return new RollbackSnapshotDto
        {
            Id = id,
            Version = id.StartsWith("rollback-", StringComparison.Ordinal)
                ? id["rollback-".Length..].Split('-')[0]
                : "?",
            Reason = RollbackReasons.Manual,
            CreatedAt = info.LastWriteTimeUtc,
            SizeBytes = info.Length,
            FileCount = 0,
        };
    }

    /// <summary>只留最近若干份快照（按创建时间倒序删多余的）。</summary>
    private void Prune()
    {
        var snapshots = List();
        foreach (var stale in snapshots.Skip(KeepSnapshots))
        {
            foreach (var path in new[]
            {
                Path.Combine(SnapshotDirectory, stale.Id + ".zip"),
                Path.Combine(SnapshotDirectory, stale.Id + ".json"),
            })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "清理旧快照失败：{File}", path);
                }
            }
        }
    }
}
