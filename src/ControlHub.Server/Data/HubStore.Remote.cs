using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>远程指令的数据访问（A 端下发、B 端回报）。</summary>
public sealed partial class HubStore
{
    /// <summary>单条指令的输出上限。超出部分截断，避免一条命令把数据库撑大。</summary>
    private const int MaxCommandOutputLength = 32 * 1024;

    private const string CommandColumns =
        "id, device_id, kind, payload, status, output, exit_code, issued_at, finished_at, issued_by, "
        + "dispatched_at, expires_at, not_before";

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
        DispatchedAt = GetTimestamp(reader, "dispatched_at"),
        ExpiresAt = GetTimestamp(reader, "expires_at"),
        NotBefore = GetTimestamp(reader, "not_before"),
    };

    /// <summary>创建一条待执行指令。</summary>
    public async Task CreateCommandAsync(RemoteCommandRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO device_commands ({CommandColumns})
            VALUES ($id, $deviceId, $kind, $payload, $status, $output, $exitCode, $issuedAt, $finishedAt,
                    $issuedBy, $dispatchedAt, $expiresAt, $notBefore);
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
            ("$issuedBy", row.IssuedBy),
            ("$dispatchedAt", row.DispatchedAt is null ? null : Ts(row.DispatchedAt.Value)),
            ("$expiresAt", row.ExpiresAt is null ? null : Ts(row.ExpiresAt.Value)),
            ("$notBefore", row.NotBefore is null ? null : Ts(row.NotBefore.Value)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 取出并「领取」某设备的待执行指令。
    /// <para>先作废已过期的指令，再把本次返回的指令标记为已派发。派发是一次性的：
    /// 否则客户端每轮心跳都会重新拿到同一条 pending 指令，导致重复执行（重复重启、重复跑命令）。</para>
    /// </summary>
    public async Task<List<RemoteCommandRow>> DispatchPendingCommandsAsync(string deviceId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1) 过期未执行 → expired，不再下发（避免陈旧命令在设备上线后被突然执行）。
        await using (var expire = connection.CreateCommand())
        {
            expire.Transaction = (SqliteTransaction)transaction;
            expire.CommandText = """
                UPDATE device_commands SET status = 'expired', finished_at = $now
                WHERE device_id = $id AND status = 'pending'
                      AND expires_at IS NOT NULL AND expires_at <= $now;
                """;
            expire.Parameters.AddWithValue("$id", deviceId);
            expire.Parameters.AddWithValue("$now", Ts(now));
            await expire.ExecuteNonQueryAsync(cancellationToken);
        }

        var result = new List<RemoteCommandRow>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)transaction;
            // 「撤销窗口」：not_before 未到点的指令先不派发，管理员可以利用这段时间撤销。
            select.CommandText =
                $"SELECT {CommandColumns} FROM device_commands "
                + "WHERE device_id = $id AND status = 'pending' "
                + "AND (not_before IS NULL OR not_before <= $now) ORDER BY issued_at;";
            select.Parameters.AddWithValue("$id", deviceId);
            select.Parameters.AddWithValue("$now", Ts(now));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(ReadCommand(reader));
            }
        }

        // 2) 本次取走的立即置为已派发，下一轮不会再返回。
        foreach (var row in result)
        {
            await using var mark = connection.CreateCommand();
            mark.Transaction = (SqliteTransaction)transaction;
            mark.CommandText =
                "UPDATE device_commands SET status = 'dispatched', dispatched_at = $now WHERE id = $id;";
            mark.Parameters.AddWithValue("$id", row.Id);
            mark.Parameters.AddWithValue("$now", Ts(now));
            await mark.ExecuteNonQueryAsync(cancellationToken);

            row.Status = "dispatched";
            row.DispatchedAt = now;
        }

        await transaction.CommitAsync(cancellationToken);
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

    /// <summary>该设备最近一次派发后仍未回报的指令数量（用于管理端提示）。</summary>
    public async Task<int> CountInFlightCommandsAsync(string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(1) AS c FROM device_commands WHERE device_id = $id AND status = 'dispatched';";
        command.Parameters.AddWithValue("$id", deviceId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>
    /// 撤销一条尚未派发的指令（撤销窗口内可用）。已派发或已结束的返回 <c>false</c>。
    /// </summary>
    public async Task<bool> CancelPendingCommandAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM device_commands
             WHERE id = $id AND status = 'pending' AND dispatched_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// 回写指令执行结果，返回是否命中。
    /// <para>带上 <paramref name="deviceId"/> 条件，避免任意已认证设备回报/覆盖其它设备的指令。</para>
    /// </summary>
    public async Task<bool> CompleteCommandAsync(string deviceId, string id, bool success, string output,
        int exitCode, string? error, CancellationToken cancellationToken = default)
    {
        var text = string.IsNullOrEmpty(error) ? output : $"{output}\n[错误] {error}";
        if (text.Length > MaxCommandOutputLength)
        {
            text = text[..MaxCommandOutputLength]
                   + $"\n…（输出过长，已截断至 {MaxCommandOutputLength / 1024} KB）";
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE device_commands
            SET status = $status, output = $output, exit_code = $exitCode, finished_at = $now
            WHERE id = $id AND device_id = $deviceId;
            """;
        AddParameters(command,
            ("$status", success ? "done" : "failed"),
            ("$output", text),
            ("$exitCode", exitCode),
            ("$now", Ts(DateTimeOffset.UtcNow)),
            ("$id", id),
            ("$deviceId", deviceId));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
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

    /// <summary>指令被取走下发给客户端的时刻。为空表示还没派发。</summary>
    public DateTimeOffset? DispatchedAt { get; set; }

    /// <summary>超过该时刻仍未执行就作废，避免设备离线很久后突然执行陈旧命令。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// 最早可派发时间：为关机等不可逆操作预留的「撤销窗口」，未到点不会被派发给客户端。
    /// </summary>
    public DateTimeOffset? NotBefore { get; set; }
}
