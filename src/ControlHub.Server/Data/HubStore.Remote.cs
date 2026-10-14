using ControlHub.Protocol;
using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>远程指令的数据访问（A 端下发、B 端回报）。</summary>
public sealed partial class HubStore
{
    /// <summary>单条指令的输出上限。超出部分截断，避免一条命令把数据库撑大。</summary>
    private const int MaxCommandOutputLength = 32 * 1024;

    /// <summary>指令错误码字段的上限。分类是枚举式的短码，超长的只可能是异常实现或恶意上报。</summary>
    private const int MaxCommandErrorCodeLength = 64;

    private const string CommandColumns =
        "id, device_id, kind, payload, status, output, exit_code, issued_at, finished_at, issued_by, "
        + "dispatched_at, expires_at, not_before, error_code, deployment_id";

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
        ErrorCode = GetString(reader, "error_code"),
        DeploymentId = GetString(reader, "deployment_id") is { Length: > 0 } deployment ? deployment : null,
    };

    /// <summary>创建一条待执行指令。</summary>
    public async Task CreateCommandAsync(RemoteCommandRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO device_commands ({CommandColumns})
            VALUES ($id, $deviceId, $kind, $payload, $status, $output, $exitCode, $issuedAt, $finishedAt,
                    $issuedBy, $dispatchedAt, $expiresAt, $notBefore, $errorCode, $deploymentId);
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
            ("$notBefore", row.NotBefore is null ? null : Ts(row.NotBefore.Value)),
            ("$errorCode", row.ErrorCode),
            ("$deploymentId", row.DeploymentId));
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

    /// <summary>
    /// 按 ID 取一条指令（限定属于这台设备）。
    /// <para>指令**回报**要用它拿到类型：「完成了一条自动化指令」与「完成了一条音量指令」
    /// 对事件触发的提醒意义完全不同（#12 的第三种事件靠这个区分）。刻意不写成「在最近 50 条里找」——
    /// 那种写法在指令多的时候会静默漏掉，而静默失效正是这一版踩过的最痛的坑。</para>
    /// </summary>
    public async Task<RemoteCommandRow?> GetCommandAsync(string deviceId, string commandId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {CommandColumns} FROM device_commands WHERE device_id = $device AND id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$id", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCommand(reader) : null;
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

    /// <summary>
    /// 作废某设备已过期的待执行指令，返回作废条数（#3）。
    /// <para>派发流程里本来会清理（见 <see cref="DispatchPendingCommandsAsync"/>），但那一步只在**设备来取指令时**发生——
    /// 一台停用了一学期的教室永远不会触发，队列里就会一直堆着早已无意义的指令。管理端查队列前顺手清一次，
    /// 既让界面干净，也避免管理员误以为「还有一堆命令在等着执行」。</para>
    /// </summary>
    public async Task<int> ExpirePendingCommandsAsync(string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE device_commands SET status = 'expired', finished_at = $now
             WHERE device_id = $id AND status = 'pending' AND dispatched_at IS NULL
                   AND expires_at IS NOT NULL AND expires_at <= $now;
            """;
        command.Parameters.AddWithValue("$id", deviceId);
        command.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 查询某设备「待执行指令队列」：尚未被取走的 pending 指令，按生效时间与下发时间排序。
    /// <para>与指令历史的区别：历史按时间倒序取最近若干条，而队列只关心还没执行的，不受历史条数上限影响。</para>
    /// </summary>
    public async Task<List<RemoteCommandRow>> GetPendingCommandsAsync(string deviceId, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {CommandColumns} FROM device_commands
             WHERE device_id = $id AND status = 'pending' AND dispatched_at IS NULL
             ORDER BY COALESCE(not_before, issued_at) ASC, issued_at ASC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$id", deviceId);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<RemoteCommandRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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
    /// <para><paramref name="errorCode"/> 是失败原因的**结构化分类**（见 <see cref="CommandErrorCodes"/>）。
    /// 文案仍然整体存进 <c>output</c>（含 <c>[错误]</c> 前缀）：分类只用来帮界面给出「怎么办」，
    /// **不替代原文** —— 原文里有「哪个字段不对」「宿主报了什么」这类只有它才有的信息。</para>
    /// </summary>
    public async Task<bool> CompleteCommandAsync(string deviceId, string id, bool success, string output,
        int exitCode, string? error, string? errorCode, CancellationToken cancellationToken = default)
    {
        var text = string.IsNullOrEmpty(error) ? output : $"{output}\n[错误] {error}";
        if (text.Length > MaxCommandOutputLength)
        {
            text = text[..MaxCommandOutputLength]
                   + $"\n…（输出过长，已截断至 {MaxCommandOutputLength / 1024} KB）";
        }

        // 分类**不做白名单校验**：老服务端遇到新插件的新码时，丢掉反而更糟（前端只会显示不出建议，
        // 但原文还在；丢掉则连分类都查不到）。只做长度收敛，避免设备塞进来一大段内容。
        var code = string.IsNullOrWhiteSpace(errorCode) ? string.Empty : errorCode.Trim();
        if (code.Length > MaxCommandErrorCodeLength)
        {
            code = code[..MaxCommandErrorCodeLength];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE device_commands
            SET status = $status, output = $output, exit_code = $exitCode, error_code = $errorCode,
                finished_at = $now
            WHERE id = $id AND device_id = $deviceId;
            """;
        AddParameters(command,
            ("$status", success ? "done" : "failed"),
            ("$output", text),
            ("$exitCode", exitCode),
            // 成功时**一律清空分类**：教室端若在成功路径上带了码（例如实现有 bug），
            // 留着会让界面把一条成功指令标成「失败原因：…」。
            ("$errorCode", success ? string.Empty : code),
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

    /// <summary>失败原因分类（取值见 <see cref="CommandErrorCodes"/>）；空串表示教室端没上报分类。</summary>
    public string ErrorCode { get; set; } = string.Empty;

    /// <summary>
    /// 这条指令属于哪一批插件下发（<c>plugin_deployments.id</c>）。
    /// <para>普通远程指令为空；插件下发的逐台进度就是按这一列归拢的。</para>
    /// </summary>
    public string? DeploymentId { get; set; }

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
