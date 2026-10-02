namespace ControlHub.Server.Data;

/// <summary>登录尝试记录：用于「连续失败锁定账号」。成功与失败都记，成功时清空该账号的失败计数。</summary>
public sealed partial class HubStore
{
    /// <summary>记录一次登录尝试。</summary>
    public async Task AddLoginAttemptAsync(string username, string? ipAddress, bool success,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO login_attempts (id, username, ip_address, success, created_at)
            VALUES ($id, $username, $ip, $success, $now);
            """;
        AddParameters(command,
            ("$id", Guid.NewGuid().ToString("n")),
            ("$username", username ?? string.Empty),
            ("$ip", TextOrNull(ipAddress)),
            ("$success", Bool(success)),
            ("$now", Ts(DateTimeOffset.UtcNow)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>最近 <paramref name="windowMinutes"/> 分钟内的失败次数。</summary>
    public async Task<int> CountRecentLoginFailuresAsync(string username, int windowMinutes,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1) FROM login_attempts
             WHERE username = $username AND success = 0 AND created_at >= $since;
            """;
        AddParameters(command,
            ("$username", username ?? string.Empty),
            ("$since", Ts(DateTimeOffset.UtcNow.AddMinutes(-Math.Max(1, windowMinutes)))));

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>清空某账号的失败记录（登录成功时调用）。</summary>
    public async Task ClearLoginFailuresAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM login_attempts WHERE username = $username AND success = 0;";
        command.Parameters.AddWithValue("$username", username ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>清理过期的登录尝试记录（维护任务调用）。</summary>
    public async Task<int> PruneLoginAttemptsAsync(int keepDays, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM login_attempts WHERE created_at < $before;";
        command.Parameters.AddWithValue("$before", Ts(DateTimeOffset.UtcNow.AddDays(-Math.Max(1, keepDays))));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
