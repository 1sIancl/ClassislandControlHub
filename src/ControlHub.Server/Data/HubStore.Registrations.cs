using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 自助注册申请（需管理员审批）的数据访问。
/// <para>与邀请码注册的区别：申请只是「排队」，批准时才写 users 表。</para>
/// </summary>
public sealed partial class HubStore
{
    // ────────────────────────────── 注册申请 ──────────────────────────────

    private const string RegisterRequestColumns =
        "id, username, display_name, password_hash, note, status, reason, reviewed_by, reviewed_at, created_at";

    private static RegisterRequestRow ReadRegisterRequest(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        Username = GetString(reader, "username"),
        DisplayName = GetString(reader, "display_name"),
        PasswordHash = GetString(reader, "password_hash"),
        Note = GetString(reader, "note"),
        Status = GetString(reader, "status"),
        Reason = GetString(reader, "reason"),
        ReviewedBy = GetString(reader, "reviewed_by"),
        ReviewedAt = GetTimestamp(reader, "reviewed_at"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
    };

    /// <summary>
    /// 查询注册申请。待审批的排在最前，便于管理员直接处理。
    /// </summary>
    /// <param name="status">按状态过滤（pending / approved / rejected）；为空表示全部。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<List<RegisterRequestRow>> GetRegisterRequestsAsync(string? status = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var filter = string.IsNullOrWhiteSpace(status) ? string.Empty : " WHERE status = $status";
        command.CommandText = $"""
            SELECT {RegisterRequestColumns} FROM register_requests{filter}
            ORDER BY CASE status WHEN 'pending' THEN 0 ELSE 1 END, created_at DESC;
            """;
        if (!string.IsNullOrWhiteSpace(status))
        {
            command.Parameters.AddWithValue("$status", status);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RegisterRequestRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadRegisterRequest(reader));
        }

        return result;
    }

    /// <summary>按 ID 查询申请。</summary>
    public async Task<RegisterRequestRow?> GetRegisterRequestAsync(string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RegisterRequestColumns} FROM register_requests WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRegisterRequest(reader) : null;
    }

    /// <summary>同一用户名是否已有待审批的申请（用于阻止重复提交）。</summary>
    public async Task<RegisterRequestRow?> GetPendingRegisterRequestAsync(string username,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {RegisterRequestColumns} FROM register_requests WHERE username = $username AND status = 'pending' LIMIT 1;";
        command.Parameters.AddWithValue("$username", username);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRegisterRequest(reader) : null;
    }

    /// <summary>创建申请（状态固定为 pending）。</summary>
    public async Task CreateRegisterRequestAsync(RegisterRequestRow row,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO register_requests
                (id, username, display_name, password_hash, note, status, reason, reviewed_by, reviewed_at, created_at)
            VALUES
                ($id, $username, $displayName, $passwordHash, $note, 'pending', '', '', NULL, $createdAt);
            """;
        AddParameters(command,
            ("$id", row.Id),
            ("$username", row.Username),
            ("$displayName", row.DisplayName),
            ("$passwordHash", row.PasswordHash),
            ("$note", row.Note),
            ("$createdAt", Ts(row.CreatedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 审批申请：只有仍处于 <c>pending</c> 时才会更新，返回是否真的更新成功。
    /// <para>两个管理员同时点「批准」时，只有一个会拿到 true，避免重复建号。</para>
    /// </summary>
    public async Task<bool> SetRegisterRequestStatusAsync(string id, string status, string reason,
        string reviewedBy, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE register_requests
               SET status = $status,
                   reason = $reason,
                   reviewed_by = $reviewedBy,
                   reviewed_at = $reviewedAt
             WHERE id = $id AND status = 'pending';
            """;
        AddParameters(command,
            ("$status", status),
            ("$reason", reason),
            ("$reviewedBy", reviewedBy),
            ("$reviewedAt", Ts(DateTimeOffset.UtcNow)),
            ("$id", id));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>删除申请记录（已处理的记录可由管理员清理）。</summary>
    public async Task<bool> DeleteRegisterRequestAsync(string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM register_requests WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>待审批申请数量（登录页/管理端概览用）。</summary>
    public async Task<int> CountPendingRegisterRequestsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM register_requests WHERE status = 'pending';";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }
}

/// <summary>注册申请表的一行。</summary>
public sealed class RegisterRequestRow
{
    /// <summary>申请 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>期望的登录用户名（批准前不与 users 表冲突）。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>显示名称。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>申请人自设密码的哈希；批准建号时原样搬过去。</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>申请人填写的说明。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>状态：pending / approved / rejected。</summary>
    public string Status { get; set; } = "pending";

    /// <summary>拒绝理由。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>审批人用户名。</summary>
    public string ReviewedBy { get; set; } = string.Empty;

    /// <summary>审批时间。</summary>
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>提交时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
