using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 设备诊断工件（屏幕截图等二进制内容）的访问方法。
/// <para>
/// 诊断内容体积远大于普通记录，因此每个设备只保留最近 <see cref="DiagnosticsKeepPerDevice"/> 条：
/// 写入时在同一事务内淘汰旧记录，客户端反复抓屏也不会把数据库撑爆。
/// </para>
/// </summary>
public partial class HubStore
{
    /// <summary>每个设备保留的诊断工件数量上限（超出后淘汰最旧的）。</summary>
    public const int DiagnosticsKeepPerDevice = 5;

    /// <summary>写入一条诊断工件，并淘汰该设备超额的旧记录。</summary>
    /// <returns>新记录的 ID。</returns>
    public async Task<string> AddDiagnosticAsync(
        string deviceId,
        string commandId,
        string kind,
        string note,
        string contentType,
        byte[] content,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO device_diagnostics
                    (id, device_id, command_id, kind, note, content_type, size_bytes, content, captured_at, created_at)
                VALUES ($id, $deviceId, $commandId, $kind, $note, $contentType, $size, $content, $capturedAt, $createdAt);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$deviceId", deviceId);
            command.Parameters.AddWithValue("$commandId", commandId ?? string.Empty);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$note", note ?? string.Empty);
            command.Parameters.AddWithValue("$contentType", contentType);
            command.Parameters.AddWithValue("$size", content.LongLength);
            command.Parameters.Add("$content", SqliteType.Blob).Value = content;
            command.Parameters.AddWithValue("$capturedAt", Ts(capturedAt));
            command.Parameters.AddWithValue("$createdAt", Ts(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = (SqliteTransaction)transaction;
            prune.CommandText = """
                DELETE FROM device_diagnostics
                WHERE device_id = $deviceId AND id NOT IN (
                    SELECT id FROM device_diagnostics WHERE device_id = $deviceId
                    ORDER BY captured_at DESC, rowid DESC LIMIT $keep);
                """;
            prune.Parameters.AddWithValue("$deviceId", deviceId);
            prune.Parameters.AddWithValue("$keep", DiagnosticsKeepPerDevice);
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    /// <summary>列出某个设备的诊断工件（新的在前，不含内容本体）。</summary>
    public async Task<List<DiagnosticRow>> GetDiagnosticsAsync(string deviceId, int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, device_id, command_id, kind, note, content_type, size_bytes, captured_at
            FROM device_diagnostics
            WHERE device_id = $deviceId
            ORDER BY captured_at DESC, rowid DESC LIMIT $limit;
            """;
        AddParameters(command, ("$deviceId", deviceId), ("$limit", limit));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DiagnosticRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DiagnosticRow
            {
                Id = GetString(reader, "id"),
                DeviceId = GetString(reader, "device_id"),
                CommandId = GetString(reader, "command_id"),
                Kind = GetString(reader, "kind"),
                Note = GetString(reader, "note"),
                ContentType = GetString(reader, "content_type"),
                SizeBytes = GetInt64(reader, "size_bytes"),
                CapturedAt = GetTimestampOrNow(reader, "captured_at"),
            });
        }

        return result;
    }

    /// <summary>读取一条诊断工件（含内容本体）。</summary>
    public async Task<(DiagnosticRow Row, byte[] Content)?> GetDiagnosticAsync(string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, device_id, command_id, kind, note, content_type, size_bytes, captured_at, content
            FROM device_diagnostics WHERE id = $id;
            """;
        AddParameters(command, ("$id", id));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var row = new DiagnosticRow
        {
            Id = GetString(reader, "id"),
            DeviceId = GetString(reader, "device_id"),
            CommandId = GetString(reader, "command_id"),
            Kind = GetString(reader, "kind"),
            Note = GetString(reader, "note"),
            ContentType = GetString(reader, "content_type"),
            SizeBytes = GetInt64(reader, "size_bytes"),
            CapturedAt = GetTimestampOrNow(reader, "captured_at"),
        };
        var content = reader.GetFieldValue<byte[]>(reader.GetOrdinal("content"));
        return (row, content);
    }

    /// <summary>清空某个设备的全部诊断工件。</summary>
    public async Task<int> DeleteDiagnosticsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM device_diagnostics WHERE device_id = $deviceId;";
        command.Parameters.AddWithValue("$deviceId", deviceId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>删除单条诊断工件。</summary>
    public async Task<bool> DeleteDiagnosticAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM device_diagnostics WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}

/// <summary>一条设备诊断工件记录（不含内容本体）。</summary>
public sealed class DiagnosticRow
{
    public string Id { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>触发采集的指令 ID（可空）。</summary>
    public string CommandId { get; set; } = string.Empty;

    /// <summary>工件类型，目前为 <c>screenshot</c>。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>说明（分辨率、前台窗口标题等）。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>MIME 类型。</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>内容体积（字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>采集时间。</summary>
    public DateTimeOffset CapturedAt { get; set; }
}
