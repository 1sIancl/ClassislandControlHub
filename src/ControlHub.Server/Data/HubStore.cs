using ControlHub.Protocol;
using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// SQLite 数据访问核心：连接管理、建表、全局配置版本号与键值设置。
/// 其余数据访问方法按领域拆分在同名的 partial 文件中，便于维护。
/// </summary>
public sealed partial class HubStore
{
    private readonly string _connectionString;

    /// <summary>创建数据存储。</summary>
    /// <param name="databasePath">SQLite 数据库文件路径。</param>
    public HubStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        DatabasePath = databasePath;
    }

    /// <summary>数据库文件路径。</summary>
    public string DatabasePath { get; }

    /// <summary>打开一个连接（连接池化，调用方负责释放）。</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    /// 初始化数据库结构。幂等，可在每次启动时调用。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 确保全局版本号存在，保证任何一次同步请求都能拿到确定值。
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO settings(key, value) VALUES ('revision', '1')
            ON CONFLICT(key) DO NOTHING;
            INSERT INTO settings(key, value) VALUES ('server_started_at', $now)
            ON CONFLICT(key) DO NOTHING;
            """;
        seed.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await seed.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS users (
            id                   TEXT PRIMARY KEY,
            username             TEXT NOT NULL UNIQUE,
            password_hash        TEXT NOT NULL,
            display_name         TEXT NOT NULL DEFAULT '',
            role                 TEXT NOT NULL DEFAULT 'admin',
            must_change_password INTEGER NOT NULL DEFAULT 0,
            created_at           TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sessions (
            token      TEXT PRIMARY KEY,
            user_id    TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_sessions_expires ON sessions(expires_at);

        CREATE TABLE IF NOT EXISTS groups (
            id                 TEXT PRIMARY KEY,
            name               TEXT NOT NULL,
            description        TEXT NOT NULL DEFAULT '',
            default_profile_id TEXT,
            created_at         TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS profiles (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            description TEXT NOT NULL DEFAULT '',
            revision    INTEGER NOT NULL DEFAULT 1,
            content     TEXT NOT NULL DEFAULT '{}',
            is_default  INTEGER NOT NULL DEFAULT 0,
            created_at  TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS devices (
            id                      TEXT PRIMARY KEY,
            token                   TEXT NOT NULL,
            name                    TEXT NOT NULL DEFAULT '',
            machine_name            TEXT NOT NULL DEFAULT '',
            os_version              TEXT NOT NULL DEFAULT '',
            classisland_version     TEXT NOT NULL DEFAULT '',
            plugin_version          TEXT NOT NULL DEFAULT '',
            group_id                TEXT,
            profile_id              TEXT,
            state                   TEXT NOT NULL DEFAULT 'offline',
            applied_revision        INTEGER NOT NULL DEFAULT 0,
            push_epoch              INTEGER NOT NULL DEFAULT 0,
            applied_push_epoch      INTEGER NOT NULL DEFAULT 0,
            last_seen_at            TEXT,
            last_sync_at            TEXT,
            last_error              TEXT,
            ip_address              TEXT,
            revoked                 INTEGER NOT NULL DEFAULT 0,
            current_class_plan_name TEXT,
            metrics                 TEXT NOT NULL DEFAULT '{}',
            created_at              TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_devices_group ON devices(group_id);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_devices_token ON devices(token);

        CREATE TABLE IF NOT EXISTS enroll_codes (
            code       TEXT PRIMARY KEY,
            note       TEXT NOT NULL DEFAULT '',
            max_uses   INTEGER NOT NULL DEFAULT 1,
            used_count INTEGER NOT NULL DEFAULT 0,
            expires_at TEXT,
            enabled    INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS audit_logs (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            ts       TEXT NOT NULL,
            actor    TEXT NOT NULL DEFAULT '',
            action   TEXT NOT NULL DEFAULT '',
            target   TEXT NOT NULL DEFAULT '',
            detail   TEXT NOT NULL DEFAULT '',
            ip       TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_audit_ts ON audit_logs(ts DESC);

        CREATE TABLE IF NOT EXISTS client_logs (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            device_id TEXT NOT NULL,
            ts        TEXT NOT NULL,
            level     TEXT NOT NULL DEFAULT 'info',
            message   TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_client_logs_device ON client_logs(device_id, ts DESC);
        """;

    // ────────────────────────────── 设置项 ──────────────────────────────

    /// <summary>读取一个设置项，不存在时返回 <paramref name="fallback"/>。</summary>
    public async Task<string?> GetSettingAsync(string key, string? fallback = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await GetSettingAsync(connection, key, fallback, cancellationToken);
    }

    private static async Task<string?> GetSettingAsync(SqliteConnection connection, string key,
        string? fallback, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string ?? fallback;
    }

    /// <summary>写入一个设置项。</summary>
    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 读取当前全局配置版本号。任何会影响下发内容的变更都应调用
    /// <see cref="HubStore.BumpRevisionAsync(System.Threading.CancellationToken)"/> 使其递增。
    /// </summary>
    public async Task<long> GetRevisionAsync(CancellationToken cancellationToken = default)
    {
        var text = await GetSettingAsync("revision", "1", cancellationToken);
        return long.TryParse(text, out var value) ? value : 1;
    }

    /// <summary>递增全局配置版本号并返回新值。</summary>
    public async Task<long> BumpRevisionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await BumpRevisionAsync(connection, cancellationToken);
    }

    private static async Task<long> BumpRevisionAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings(key, value) VALUES ('revision', '2')
            ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
            RETURNING value;
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return long.TryParse(result as string, out var value) ? value : 1;
    }

    /// <summary>清理过期数据（会话、客户端日志、审计日志）。</summary>
    public async Task<int> PurgeExpiredAsync(int clientLogDays, int auditLogDays,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM sessions WHERE expires_at < $now;
            DELETE FROM client_logs WHERE ts < $logCutoff;
            DELETE FROM audit_logs  WHERE ts < $auditCutoff;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$logCutoff", DateTimeOffset.UtcNow.AddDays(-clientLogDays).ToString("O"));
        command.Parameters.AddWithValue("$auditCutoff", DateTimeOffset.UtcNow.AddDays(-auditLogDays).ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ────────────────────────────── 取值辅助 ──────────────────────────────

    internal static string GetString(SqliteDataReader reader, string name)
    {
        var index = reader.GetOrdinal(name);
        return reader.IsDBNull(index) ? string.Empty : reader.GetString(index);
    }

    internal static string? GetNullableString(SqliteDataReader reader, string name)
    {
        var index = reader.GetOrdinal(name);
        return reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    internal static long GetInt64(SqliteDataReader reader, string name)
    {
        var index = reader.GetOrdinal(name);
        return reader.IsDBNull(index) ? 0 : reader.GetInt64(index);
    }

    internal static int GetInt32(SqliteDataReader reader, string name)
    {
        var index = reader.GetOrdinal(name);
        return reader.IsDBNull(index) ? 0 : (int)reader.GetInt64(index);
    }

    internal static bool GetBool(SqliteDataReader reader, string name)
    {
        var index = reader.GetOrdinal(name);
        return !reader.IsDBNull(index) && reader.GetInt64(index) != 0;
    }

    internal static DateTimeOffset? GetTimestamp(SqliteDataReader reader, string name)
    {
        var text = GetNullableString(reader, name);
        return DateTimeOffset.TryParse(text, out var value) ? value : null;
    }

    internal static DateTimeOffset GetTimestampOrNow(SqliteDataReader reader, string name) =>
        GetTimestamp(reader, name) ?? DateTimeOffset.UtcNow;

    /// <summary>把布尔值转换为 SQLite 整数。</summary>
    internal static int Bool(bool value) => value ? 1 : 0;

    /// <summary>把时间戳转换为 SQLite 文本。</summary>
    internal static string Ts(DateTimeOffset value) => value.ToString("O");

    /// <summary>把可空时间戳转换为 SQLite 文本或 DBNull。</summary>
    internal static object TsOrNull(DateTimeOffset? value) =>
        value.HasValue ? value.Value.ToString("O") : DBNull.Value;

    /// <summary>把可空字符串转换为 SQLite 文本或 DBNull。</summary>
    internal static object TextOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? DBNull.Value : value;

    /// <summary>为命令批量添加参数。</summary>
    internal static void AddParameters(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }
}
