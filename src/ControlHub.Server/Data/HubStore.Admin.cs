using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>管理员账号、会话、注册码与审计日志的数据访问。</summary>
public sealed partial class HubStore
{
    // ────────────────────────────── 管理员账号 ──────────────────────────────

    private const string UserColumns =
        "id, username, password_hash, display_name, role, must_change_password, created_at";

    private static UserRow ReadUser(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        Username = GetString(reader, "username"),
        PasswordHash = GetString(reader, "password_hash"),
        DisplayName = GetString(reader, "display_name"),
        Role = GetString(reader, "role"),
        MustChangePassword = GetBool(reader, "must_change_password"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
    };

    /// <summary>按用户名查询账号。</summary>
    public async Task<UserRow?> GetUserByUsernameAsync(string username,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {UserColumns} FROM users WHERE username = $username LIMIT 1;";
        command.Parameters.AddWithValue("$username", username);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    /// <summary>按 ID 查询账号。</summary>
    public async Task<UserRow?> GetUserAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {UserColumns} FROM users WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    /// <summary>查询全部账号。</summary>
    public async Task<List<UserRow>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {UserColumns} FROM users ORDER BY created_at;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<UserRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadUser(reader));
        }

        return result;
    }

    /// <summary>创建账号。</summary>
    public async Task CreateUserAsync(UserRow user, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users (id, username, password_hash, display_name, role, must_change_password, created_at)
            VALUES ($id, $username, $hash, $displayName, $role, $mustChange, $createdAt);
            """;
        AddParameters(command,
            ("$id", user.Id),
            ("$username", user.Username),
            ("$hash", user.PasswordHash),
            ("$displayName", user.DisplayName),
            ("$role", user.Role),
            ("$mustChange", Bool(user.MustChangePassword)),
            ("$createdAt", Ts(user.CreatedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>更新账号密码。</summary>
    public async Task UpdatePasswordAsync(string userId, string passwordHash, bool mustChangePassword,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE users SET password_hash = $hash, must_change_password = $mustChange WHERE id = $id;
            """;
        AddParameters(command,
            ("$hash", passwordHash),
            ("$mustChange", Bool(mustChangePassword)),
            ("$id", userId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>统计账号数量。</summary>
    public async Task<int> CountUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM users;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    // ────────────────────────────── 会话 ──────────────────────────────

    /// <summary>创建管理员会话。</summary>
    public async Task CreateSessionAsync(string token, string userId, DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (token, user_id, created_at, expires_at)
            VALUES ($token, $userId, $now, $expiresAt);
            """;
        AddParameters(command,
            ("$token", token),
            ("$userId", userId),
            ("$now", Ts(DateTimeOffset.UtcNow)),
            ("$expiresAt", Ts(expiresAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>查询有效会话。已过期时返回 <c>null</c>。</summary>
    public async Task<SessionRow?> GetSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.token, s.user_id, u.username, u.display_name, u.role, s.expires_at
            FROM sessions s
            JOIN users u ON u.id = s.user_id
            WHERE s.token = $token AND s.expires_at > $now
            LIMIT 1;
            """;
        AddParameters(command, ("$token", token), ("$now", Ts(DateTimeOffset.UtcNow)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SessionRow
        {
            Token = GetString(reader, "token"),
            UserId = GetString(reader, "user_id"),
            Username = GetString(reader, "username"),
            DisplayName = GetString(reader, "display_name"),
            Role = GetString(reader, "role"),
            ExpiresAt = GetTimestampOrNow(reader, "expires_at"),
        };
    }

    /// <summary>删除会话（登出）。</summary>
    public async Task DeleteSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE token = $token;";
        command.Parameters.AddWithValue("$token", token);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>删除某个账号的全部会话（修改密码后强制重新登录）。</summary>
    public async Task DeleteUserSessionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE user_id = $userId;";
        command.Parameters.AddWithValue("$userId", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ────────────────────────────── 注册码 ──────────────────────────────

    private static EnrollCodeRow ReadEnrollCode(SqliteDataReader reader) => new()
    {
        Code = GetString(reader, "code"),
        Note = GetString(reader, "note"),
        MaxUses = GetInt32(reader, "max_uses"),
        UsedCount = GetInt32(reader, "used_count"),
        ExpiresAt = GetTimestamp(reader, "expires_at"),
        Enabled = GetBool(reader, "enabled"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
    };

    /// <summary>查询全部注册码。</summary>
    public async Task<List<EnrollCodeRow>> GetEnrollCodesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT code, note, max_uses, used_count, expires_at, enabled, created_at
            FROM enroll_codes ORDER BY created_at DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<EnrollCodeRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadEnrollCode(reader));
        }

        return result;
    }

    /// <summary>按注册码查询。</summary>
    public async Task<EnrollCodeRow?> GetEnrollCodeAsync(string code,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT code, note, max_uses, used_count, expires_at, enabled, created_at
            FROM enroll_codes WHERE code = $code LIMIT 1;
            """;
        command.Parameters.AddWithValue("$code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEnrollCode(reader) : null;
    }

    /// <summary>新增注册码。</summary>
    public async Task CreateEnrollCodeAsync(EnrollCodeRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO enroll_codes (code, note, max_uses, used_count, expires_at, enabled, created_at)
            VALUES ($code, $note, $maxUses, $usedCount, $expiresAt, $enabled, $createdAt);
            """;
        AddParameters(command,
            ("$code", row.Code),
            ("$note", row.Note),
            ("$maxUses", row.MaxUses),
            ("$usedCount", row.UsedCount),
            ("$expiresAt", TsOrNull(row.ExpiresAt)),
            ("$enabled", Bool(row.Enabled)),
            ("$createdAt", Ts(row.CreatedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>更新注册码的可注册次数与状态。</summary>
    public async Task UpdateEnrollCodeAsync(string code, string note, int maxUses, DateTimeOffset? expiresAt,
        bool enabled, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE enroll_codes SET note = $note, max_uses = $maxUses, expires_at = $expiresAt, enabled = $enabled
            WHERE code = $code;
            """;
        AddParameters(command,
            ("$note", note),
            ("$maxUses", maxUses),
            ("$expiresAt", TsOrNull(expiresAt)),
            ("$enabled", Bool(enabled)),
            ("$code", code));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>删除注册码。</summary>
    public async Task DeleteEnrollCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM enroll_codes WHERE code = $code;";
        command.Parameters.AddWithValue("$code", code);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>原子地占用一次注册码名额。返回是否成功。</summary>
    public async Task<bool> ConsumeEnrollCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE enroll_codes SET used_count = used_count + 1
            WHERE code = $code
              AND enabled = 1
              AND (expires_at IS NULL OR expires_at > $now)
              AND (max_uses <= 0 OR used_count < max_uses);
            """;
        AddParameters(command, ("$code", code), ("$now", Ts(DateTimeOffset.UtcNow)));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    // ────────────────────────────── 审计日志 ──────────────────────────────

    /// <summary>写入一条审计日志。</summary>
    public async Task AddAuditAsync(string actor, string action, string target, string detail,
        string? ipAddress, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_logs (ts, actor, action, target, detail, ip)
            VALUES ($ts, $actor, $action, $target, $detail, $ip);
            """;
        AddParameters(command,
            ("$ts", Ts(DateTimeOffset.UtcNow)),
            ("$actor", actor),
            ("$action", action),
            ("$target", target),
            ("$detail", detail),
            ("$ip", TextOrNull(ipAddress)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>分页查询审计日志。</summary>
    public async Task<(List<AuditLogRow> Items, int Total)> GetAuditLogsAsync(int page, int pageSize,
        string? search, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var offset = (page - 1) * pageSize;
        var filter = string.IsNullOrWhiteSpace(search)
            ? string.Empty
            : " WHERE actor LIKE $search OR action LIKE $search OR target LIKE $search OR detail LIKE $search";

        await using var connection = await OpenAsync(cancellationToken);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(1) FROM audit_logs{filter};";
        if (filter.Length > 0)
        {
            countCommand.Parameters.AddWithValue("$search", $"%{search}%");
        }

        var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, ts, actor, action, target, detail, ip FROM audit_logs{filter}
            ORDER BY id DESC LIMIT $limit OFFSET $offset;
            """;
        if (filter.Length > 0)
        {
            command.Parameters.AddWithValue("$search", $"%{search}%");
        }

        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<AuditLogRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AuditLogRow
            {
                Id = GetInt64(reader, "id"),
                Timestamp = GetTimestampOrNow(reader, "ts"),
                Actor = GetString(reader, "actor"),
                Action = GetString(reader, "action"),
                Target = GetString(reader, "target"),
                Detail = GetString(reader, "detail"),
                IpAddress = GetNullableString(reader, "ip"),
            });
        }

        return (items, total);
    }

    /// <summary>查询最近的审计日志。</summary>
    public async Task<List<AuditLogRow>> GetRecentAuditAsync(int limit = 10,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, ts, actor, action, target, detail, ip FROM audit_logs
            ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<AuditLogRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AuditLogRow
            {
                Id = GetInt64(reader, "id"),
                Timestamp = GetTimestampOrNow(reader, "ts"),
                Actor = GetString(reader, "actor"),
                Action = GetString(reader, "action"),
                Target = GetString(reader, "target"),
                Detail = GetString(reader, "detail"),
                IpAddress = GetNullableString(reader, "ip"),
            });
        }

        return items;
    }
}
