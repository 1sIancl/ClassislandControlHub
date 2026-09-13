using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>远程指令的数据访问（A 端下发、B 端回报）。</summary>
public sealed partial class HubStore
{
    private const string CommandColumns =
        "id, device_id, kind, payload, status, output, exit_code, issued_at, finished_at, issued_by";

    private static RemoteCommandRow ReadCommand(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        DeviceId = GetString(reader, "device_id"),
        Kind = GetString(reader, "kind"),
        Payload = GetString(reader, "payload"),
        Status = GetString(reader, "status"),
        Output = GetString(reader, "output"),
        ExitCode = GetInt32(reader, "exit_code"),
        IssuedAt = GetTimestampOrNow(reader, "issued_at"),
        FinishedAt = GetTimestamp(reader, "finished_at"),
        IssuedBy = GetString(reader, "issued_by"),
    };

    /// <summary>创建一条待执行指令。</summary>
    public async Task CreateCommandAsync(RemoteCommandRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO device_commands ({CommandColumns})
            VALUES ($id, $deviceId, $kind, $payload, $status, $output, $exitCode, $issuedAt, $finishedAt, $issuedBy);
            """;
        AddParameters(command,
            ("$id", row.Id),
            ("$deviceId", row.DeviceId),
            ("$kind", row.Kind),
            ("$payload", row.Payload),
            ("$status", row.Status),
            ("$output", row.Output),
            ("$exitCode", row.ExitCode),
            ("$issuedAt", Ts(row.IssuedAt)),
            ("$finishedAt", row.FinishedAt is null ? null : Ts(row.FinishedAt.Value)),
            ("$issuedBy", row.IssuedBy));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>取出某设备所有待执行指令（按时间升序）。</summary>
    public async Task<List<RemoteCommandRow>> GetPendingCommandsAsync(string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {CommandColumns} FROM device_commands WHERE device_id = $id AND status = 'pending' ORDER BY issued_at;";
        command.Parameters.AddWithValue("$id", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RemoteCommandRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadCommand(reader));
        }

        return result;
    }

    /// <summary>查询某设备的指令历史（倒序）。</summary>
    public async Task<List<RemoteCommandRow>> GetCommandsAsync(string deviceId, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {CommandColumns} FROM device_commands WHERE device_id = $id ORDER BY issued_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$id", deviceId);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RemoteCommandRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadCommand(reader));
        }

        return result;
    }

    /// <summary>回写指令执行结果。</summary>
    public async Task CompleteCommandAsync(string id, bool success, string output, int exitCode,
        string? error, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE device_commands
            SET status = $status, output = $output, exit_code = $exitCode, finished_at = $now
            WHERE id = $id;
            """;
        var text = string.IsNullOrEmpty(error) ? output : $"{output}\n[错误] {error}";
        AddParameters(command,
            ("$status", success ? "done" : "failed"),
            ("$output", text),
            ("$exitCode", exitCode),
            ("$now", Ts(DateTimeOffset.UtcNow)),
            ("$id", id));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>清理过期指令（保留最近 N 天）。</summary>
    public async Task<int> PruneCommandsAsync(int keepDays, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM device_commands WHERE issued_at < $before;";
        command.Parameters.AddWithValue("$before", Ts(DateTimeOffset.UtcNow.AddDays(-keepDays)));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>远程指令记录。</summary>
public sealed class RemoteCommandRow
{
    public string Id { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string Output { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string IssuedBy { get; set; } = string.Empty;
}
