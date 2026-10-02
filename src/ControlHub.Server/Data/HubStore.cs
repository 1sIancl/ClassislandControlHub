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

        // 迁移：老版本 profiles 表可能缺少 code 列（四位识别码），这里按需补上。
        await EnsureColumnAsync(connection, "profiles", "code", "TEXT NOT NULL DEFAULT ''", cancellationToken);

        // 迁移：分组标识色是后加的，老库需要在启动时补列。
        await EnsureColumnAsync(connection, "groups", "color", "TEXT NOT NULL DEFAULT ''", cancellationToken);

        // 迁移：分组树（楼栋 → 楼层）。
        await EnsureColumnAsync(connection, "groups", "parent_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "groups", "kind", "TEXT NOT NULL DEFAULT ''", cancellationToken);

        // 索引必须在 parent_id 补列之后再建（老库直接建会报 no such column）。
        await using (var groupIndex = connection.CreateCommand())
        {
            groupIndex.CommandText = "CREATE INDEX IF NOT EXISTS idx_groups_parent ON groups(parent_id);";
            await groupIndex.ExecuteNonQueryAsync(cancellationToken);
        }

        // 迁移：远程指令的「一次性派发」与「过期」时间戳。
        await EnsureColumnAsync(connection, "device_commands", "dispatched_at", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "device_commands", "expires_at", "TEXT", cancellationToken);

        // 迁移：账号的按模块权限集合（JSON 数组文本）。
        await EnsureColumnAsync(connection, "users", "permissions", "TEXT NOT NULL DEFAULT ''", cancellationToken);

        // 迁移：设备备注（管理员自己标注，例如「三楼东侧」「班主任 张老师」）。
        await EnsureColumnAsync(connection, "devices", "remark", "TEXT NOT NULL DEFAULT ''", cancellationToken);

        // 迁移：指令的「最早可派发时间」，用于给关机等不可逆操作留出撤销窗口。
        await EnsureColumnAsync(connection, "device_commands", "not_before", "TEXT", cancellationToken);

        // 迁移：账号两步验证（TOTP）。密钥在启用前只是「待绑定」，不会影响登录。
        await EnsureColumnAsync(connection, "users", "totp_secret", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "users", "totp_enabled", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        // 迁移：设备上一次成功应用的档案 ID 与该档案当时的版本号。用于「下发前差异预览」——
        // 只记 ID 不够：档案被编辑后，对比双方会指向同一份最新内容，差异永远是空的，
        // 因此必须同时记住「应用时是第几版」，再配合 profile_versions 快照还原当时的内容。
        await EnsureColumnAsync(connection, "devices", "applied_profile_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "devices", "applied_profile_revision", "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);

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

    /// <summary>若表缺少指定列，则补一列（用于老数据库的无损迁移）。</summary>
    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column,
        string definition, CancellationToken cancellationToken)
    {
        var has = false;
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    has = true;
                    break;
                }
            }
        }

        if (has)
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
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
            permissions          TEXT NOT NULL DEFAULT '',
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

        -- 邀请码：凭码自助注册为一个拥有预设权限的账号。
        CREATE TABLE IF NOT EXISTS register_codes (
            code        TEXT PRIMARY KEY,
            note        TEXT NOT NULL DEFAULT '',
            permissions TEXT NOT NULL DEFAULT '',
            max_uses    INTEGER NOT NULL DEFAULT 1,
            used_count  INTEGER NOT NULL DEFAULT 0,
            expires_at  TEXT,
            created_at  TEXT NOT NULL
        );

        -- 自助注册申请：用户自行填写密码并提交，管理员批准后才会真正建号。
        -- 存放的是密码哈希，明文不落库；批准时直接复用该哈希，全程不接触明文。
        CREATE TABLE IF NOT EXISTS register_requests (
            id            TEXT PRIMARY KEY,
            username      TEXT NOT NULL,
            display_name  TEXT NOT NULL DEFAULT '',
            password_hash TEXT NOT NULL,
            note          TEXT NOT NULL DEFAULT '',
            status        TEXT NOT NULL DEFAULT 'pending',
            reason        TEXT NOT NULL DEFAULT '',
            reviewed_by   TEXT NOT NULL DEFAULT '',
            reviewed_at   TEXT,
            created_at    TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_register_requests_status ON register_requests(status, created_at DESC);

        -- 设备诊断工件（屏幕截图等二进制内容）。每个设备只保留最近若干条，避免体积无限增长。
        CREATE TABLE IF NOT EXISTS device_diagnostics (
            id           TEXT PRIMARY KEY,
            device_id    TEXT NOT NULL,
            command_id   TEXT NOT NULL DEFAULT '',
            kind         TEXT NOT NULL DEFAULT 'screenshot',
            note         TEXT NOT NULL DEFAULT '',
            content_type TEXT NOT NULL DEFAULT 'image/png',
            size_bytes   INTEGER NOT NULL DEFAULT 0,
            content      BLOB NOT NULL,
            captured_at  TEXT NOT NULL,
            created_at   TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_device_diagnostics_device
            ON device_diagnostics(device_id, captured_at DESC);

        -- 临时换课（跨天 / 跨周换课）：在指定日期范围内把某节课临时换成别的科目。
        -- 覆盖不改动档案本身，只在每次下发给设备时合成，因此到期后自动还原。
        CREATE TABLE IF NOT EXISTS timetable_overrides (
            id            TEXT PRIMARY KEY,
            profile_id    TEXT NOT NULL,
            class_plan_id TEXT NOT NULL DEFAULT '',
            slot_index    INTEGER NOT NULL DEFAULT 0,
            subject_id    TEXT,
            start_date    TEXT NOT NULL,
            end_date      TEXT,
            weekdays      TEXT NOT NULL DEFAULT '',
            reason        TEXT NOT NULL DEFAULT '',
            created_by    TEXT NOT NULL DEFAULT '',
            created_at    TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_timetable_overrides_profile
            ON timetable_overrides(profile_id, start_date);

        -- 配置档案历史版本：每次保存前存一份快照，改坏了可以一键回滚。
        CREATE TABLE IF NOT EXISTS profile_versions (
            id          TEXT PRIMARY KEY,
            profile_id  TEXT NOT NULL,
            revision    INTEGER NOT NULL DEFAULT 0,
            name        TEXT NOT NULL DEFAULT '',
            description TEXT NOT NULL DEFAULT '',
            content     TEXT NOT NULL DEFAULT '{}',
            reason      TEXT NOT NULL DEFAULT '',
            created_by  TEXT NOT NULL DEFAULT '',
            created_at  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_profile_versions_profile
            ON profile_versions(profile_id, created_at DESC);

        -- Webhook：把关键事件（设备掉线 / 同步失败 / 指令失败）推到企业微信、钉钉、飞书或自定义端点。
        CREATE TABLE IF NOT EXISTS webhooks (
            id         TEXT PRIMARY KEY,
            name       TEXT NOT NULL DEFAULT '',
            url        TEXT NOT NULL,
            kind       TEXT NOT NULL DEFAULT 'generic',
            secret     TEXT NOT NULL DEFAULT '',
            events     TEXT NOT NULL DEFAULT '',
            enabled    INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL
        );

        -- 通知模板：把常用的广播内容存下来，发通知时一键套用。
        CREATE TABLE IF NOT EXISTS notice_templates (
            id         TEXT PRIMARY KEY,
            name       TEXT NOT NULL,
            title      TEXT NOT NULL DEFAULT '',
            content    TEXT NOT NULL DEFAULT '',
            speak      INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        -- 定时提醒：按用户隔离（user_id），目标是共享的教室设备。
        CREATE TABLE IF NOT EXISTS reminders (
            id                TEXT PRIMARY KEY,
            user_id           TEXT NOT NULL,
            title             TEXT NOT NULL DEFAULT '',
            content           TEXT NOT NULL DEFAULT '',
            channel           TEXT NOT NULL DEFAULT 'screen',
            target_type       TEXT NOT NULL DEFAULT 'all',
            target_id         TEXT NOT NULL DEFAULT '',
            speak             INTEGER NOT NULL DEFAULT 0,
            enabled           INTEGER NOT NULL DEFAULT 1,
            repeat_kind       TEXT NOT NULL DEFAULT 'once',
            repeat_interval   INTEGER NOT NULL DEFAULT 1,
            repeat_weekdays   TEXT NOT NULL DEFAULT '',
            repeat_day_of_month INTEGER NOT NULL DEFAULT 0,
            fire_time         TEXT NOT NULL DEFAULT '08:00',
            start_date        TEXT NOT NULL DEFAULT '',
            end_date          TEXT,
            next_fire_at      TEXT,
            last_fired_at     TEXT,
            fire_count        INTEGER NOT NULL DEFAULT 0,
            created_at        TEXT NOT NULL,
            updated_at        TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_reminders_user ON reminders(user_id);

        -- 提醒触发记录：用于核对「是否按设定时间准确触发」。
        CREATE TABLE IF NOT EXISTS reminder_fires (
            id          TEXT PRIMARY KEY,
            reminder_id TEXT NOT NULL,
            user_id     TEXT NOT NULL,
            fired_at    TEXT NOT NULL,
            affected    INTEGER NOT NULL DEFAULT 0,
            skipped     INTEGER NOT NULL DEFAULT 0,
            detail      TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_reminder_fires_reminder ON reminder_fires(reminder_id, fired_at DESC);

        CREATE TABLE IF NOT EXISTS groups (
            id                 TEXT PRIMARY KEY,
            name               TEXT NOT NULL,
            description        TEXT NOT NULL DEFAULT '',
            color              TEXT NOT NULL DEFAULT '',
            parent_id          TEXT,
            kind               TEXT NOT NULL DEFAULT '',
            default_profile_id TEXT,
            created_at         TEXT NOT NULL
        );
        -- 注意：idx_groups_parent 不在这里建。老库升级时 parent_id 要先由 EnsureColumnAsync 补出来，
        -- 否则 CREATE TABLE IF NOT EXISTS 不生效、建索引会报「no such column: parent_id」。
        -- 见 InitializeAsync 中紧跟分组成员迁移之后的建索引语句。

        CREATE TABLE IF NOT EXISTS profiles (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            description TEXT NOT NULL DEFAULT '',
            code        TEXT NOT NULL DEFAULT '',
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

        CREATE TABLE IF NOT EXISTS assignments (
            id          TEXT PRIMARY KEY,
            target_type TEXT NOT NULL DEFAULT 'device',
            target_id   TEXT NOT NULL DEFAULT '',
            profile_id  TEXT NOT NULL DEFAULT '',
            created_at  TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_assignments_profile ON assignments(profile_id);

        CREATE TABLE IF NOT EXISTS device_commands (
            id            TEXT PRIMARY KEY,
            device_id     TEXT NOT NULL,
            kind          TEXT NOT NULL DEFAULT '',
            payload       TEXT NOT NULL DEFAULT '',
            status        TEXT NOT NULL DEFAULT 'pending',
            output        TEXT NOT NULL DEFAULT '',
            exit_code     INTEGER NOT NULL DEFAULT 0,
            issued_at     TEXT NOT NULL,
            finished_at   TEXT,
            issued_by     TEXT NOT NULL DEFAULT '',
            dispatched_at TEXT,
            expires_at    TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_device_commands_device ON device_commands(device_id, issued_at DESC);
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
