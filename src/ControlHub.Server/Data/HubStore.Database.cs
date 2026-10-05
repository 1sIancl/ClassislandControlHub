using ControlHub.Protocol.Dtos;
using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 数据库健康检查与空间整理（#62）。
///
/// 为什么要它：SQLite 删数据**不归还磁盘空间**（留成空闲页），长期运行后 <c>controlhub.db</c> 只涨不缩；
/// WAL 文件在大量写入后也可能比主库还大。现场能看到的现象是「磁盘越来越少」，但没有任何入口能查、能整理。
/// </summary>
public sealed partial class HubStore
{
    /// <summary>检查数据库完整性与空间占用。</summary>
    public async Task<DatabaseHealthDto> GetDatabaseHealthAsync(CancellationToken cancellationToken = default)
    {
        var result = new DatabaseHealthDto { CheckedAt = DateTimeOffset.UtcNow };

        await using var connection = await OpenAsync(cancellationToken);

        // 完整性检查：正常返回一行 "ok"，异常时返回具体损坏位置（可能多行）。
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check;";
            var lines = new List<string>();
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(reader.GetString(0));
            }

            result.IntegrityOk = lines.Count == 1
                                 && string.Equals(lines[0], "ok", StringComparison.OrdinalIgnoreCase);
            result.IntegrityMessage = lines.Count == 0 ? "（无返回）" : string.Join("；", lines);
        }

        result.PageSize = await ScalarAsync(connection, "PRAGMA page_size;", cancellationToken);
        result.PageCount = await ScalarAsync(connection, "PRAGMA page_count;", cancellationToken);
        result.FreePageCount = await ScalarAsync(connection, "PRAGMA freelist_count;", cancellationToken);
        result.ReclaimableBytes = result.PageSize * result.FreePageCount;

        var (db, wal, shm) = MeasureFiles();
        result.DatabaseBytes = db;
        result.WalBytes = wal;
        result.SharedMemoryBytes = shm;
        result.TotalBytes = db + wal + shm;

        return result;
    }

    /// <summary>执行 <c>VACUUM</c> 整理数据库，返回回收量。</summary>
    public async Task<DatabaseVacuumResultDto> VacuumAsync(CancellationToken cancellationToken = default)
    {
        var before = MeasureFiles();
        var started = DateTimeOffset.UtcNow;
        var beforeTotal = before.Db + before.Wal + before.Shm;

        await using (var connection = await OpenAsync(cancellationToken))
        {
            await using var command = connection.CreateCommand();
            // VACUUM 会重写整库：执行期间需要额外磁盘空间（约等于当前库大小），耗时与库大小成正比。
            command.CommandText = "VACUUM;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // 让 SQLite 把 WAL 合并回主库，否则整理完看体积可能没变化（数据还在 WAL 里）。
        await using (var connection = await OpenAsync(cancellationToken))
        {
            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        }

        var after = MeasureFiles();
        var afterTotal = after.Db + after.Wal + after.Shm;

        return new DatabaseVacuumResultDto
        {
            BeforeBytes = beforeTotal,
            AfterBytes = afterTotal,
            ReclaimedBytes = Math.Max(0, beforeTotal - afterTotal),
            DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
        };
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>当前库与 WAL / SHM 文件体积。</summary>
    private (long Db, long Wal, long Shm) MeasureFiles()
    {
        static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        return (Size(DatabasePath), Size(DatabasePath + "-wal"), Size(DatabasePath + "-shm"));
    }
}
