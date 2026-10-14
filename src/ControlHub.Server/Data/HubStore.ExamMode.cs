using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 考试模式的持久化（`#21`）。
/// <para><b>为什么要把「原来的档案」存下来</b>：考试模式是一次**临时**改动——它把选定教室的
/// 档案换成考试档案，考完必须能还原。而设备的档案可能本来就为空（那时它用的是分组默认档案），
/// 所以「还原」必须还原成**当时那一行的原值**（可能是 NULL），不能统一塞一个默认值。</para>
/// <para>只允许一个进行中的考试模式：同一批教室同时挂两个「当前该用哪个档案」的说法没有意义。</para>
/// </summary>
public partial class HubStore
{
    /// <summary>建表（幂等；由 <see cref="InitializeAsync"/> 调用）。</summary>
    internal async Task EnsureExamModeSchemaAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS exam_modes (
                id          TEXT PRIMARY KEY,
                profile_id  TEXT NOT NULL,
                message     TEXT NOT NULL DEFAULT '',
                started_at  TEXT NOT NULL,
                started_by  TEXT NOT NULL DEFAULT '',
                ended_at    TEXT
            );
            CREATE TABLE IF NOT EXISTS exam_mode_targets (
                exam_mode_id        TEXT NOT NULL,
                device_id           TEXT NOT NULL,
                device_name         TEXT NOT NULL DEFAULT '',
                previous_profile_id TEXT,
                PRIMARY KEY (exam_mode_id, device_id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>取当前进行中的考试模式（含目标教室与各自的原档案）；没有则返回 <c>null</c>。</summary>
    public async Task<(ExamModeRow Mode, List<ExamModeTargetRow> Targets)?> GetActiveExamModeAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        ExamModeRow? mode = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, profile_id, message, started_at, started_by, ended_at
                  FROM exam_modes WHERE ended_at IS NULL ORDER BY started_at DESC LIMIT 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                mode = new ExamModeRow
                {
                    Id = reader.GetString(0),
                    ProfileId = reader.GetString(1),
                    Message = reader.GetString(2),
                    StartedAt = GetTimestamp(reader, "started_at") ?? DateTimeOffset.UtcNow,
                    StartedBy = reader.GetString(4),
                    EndedAt = reader.IsDBNull(5) ? null : GetTimestamp(reader, "ended_at"),
                };
            }
        }

        if (mode is null)
        {
            return null;
        }

        var targets = new List<ExamModeTargetRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT exam_mode_id, device_id, device_name, previous_profile_id
                  FROM exam_mode_targets WHERE exam_mode_id = $id ORDER BY device_name;
                """;
            command.Parameters.AddWithValue("$id", mode.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                targets.Add(new ExamModeTargetRow
                {
                    ExamModeId = reader.GetString(0),
                    DeviceId = reader.GetString(1),
                    DeviceName = reader.GetString(2),
                    PreviousProfileId = reader.IsDBNull(3) ? null : reader.GetString(3),
                });
            }
        }

        return (mode, targets);
    }

    /// <summary>开始一次考试模式（记下每个目标教室的原档案）。</summary>
    public async Task CreateExamModeAsync(ExamModeRow mode, IReadOnlyList<ExamModeTargetRow> targets,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO exam_modes (id, profile_id, message, started_at, started_by, ended_at)
                VALUES ($id, $profileId, $message, $startedAt, $startedBy, NULL);
                """;
            command.Parameters.AddWithValue("$id", mode.Id);
            command.Parameters.AddWithValue("$profileId", mode.ProfileId);
            command.Parameters.AddWithValue("$message", mode.Message);
            command.Parameters.AddWithValue("$startedAt", Ts(mode.StartedAt));
            command.Parameters.AddWithValue("$startedBy", mode.StartedBy);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var target in targets)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO exam_mode_targets (exam_mode_id, device_id, device_name, previous_profile_id)
                VALUES ($modeId, $deviceId, $deviceName, $previous);
                """;
            command.Parameters.AddWithValue("$modeId", target.ExamModeId);
            command.Parameters.AddWithValue("$deviceId", target.DeviceId);
            command.Parameters.AddWithValue("$deviceName", target.DeviceName);
            command.Parameters.AddWithValue("$previous", TextOrNull(target.PreviousProfileId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>结束考试模式（只改状态；档案还原由服务层按记录的原值逐个写回）。</summary>
    public async Task<bool> EndExamModeAsync(string id, DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE exam_modes SET ended_at = $endedAt WHERE id = $id AND ended_at IS NULL;";
        command.Parameters.AddWithValue("$endedAt", Ts(endedAt));
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>一次考试模式。</summary>
    public sealed class ExamModeRow
    {
        /// <summary>记录 ID。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>切换到哪个档案（考试档案）。</summary>
        public string ProfileId { get; set; } = string.Empty;

        /// <summary>随通知下发的说明文案。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>开始时间。</summary>
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>是谁开始的。</summary>
        public string StartedBy { get; set; } = string.Empty;

        /// <summary>结束时间；<c>null</c> 表示仍在进行。</summary>
        public DateTimeOffset? EndedAt { get; set; }
    }

    /// <summary>一次考试模式里的一个目标教室（含它原来的档案）。</summary>
    public sealed class ExamModeTargetRow
    {
        /// <summary>所属考试模式。</summary>
        public string ExamModeId { get; set; } = string.Empty;

        /// <summary>教室 ID。</summary>
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>教室名（存一份快照：还原时即使设备被改名也能对上）。</summary>
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>
        /// 这台教室**原来的**档案；<c>null</c> 表示它当时没有单独指定档案
        /// （用的是分组默认档案）——还原时要把这一列写回 NULL，而不是塞一个默认值。
        /// </summary>
        public string? PreviousProfileId { get; set; }
    }
}
