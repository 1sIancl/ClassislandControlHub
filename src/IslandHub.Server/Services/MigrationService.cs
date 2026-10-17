using System.Globalization;
using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using Microsoft.Data.Sqlite;

namespace IslandHub.Server.Services;

/// <summary>
/// 数据迁移（路线图 #61）：整包导出 / 预览 / 导入。
///
/// <para>
/// 为什么做成**表级**而不是逐实体映射：实体字段还会长，逐个映射必然漏字段，而漏掉的那个字段
/// 在现场表现为「迁过去某项配置没了」——很难查。表级拷贝的代价是要显式剥离敏感列，见
/// <see cref="SecretColumns"/>。
/// </para>
///
/// <para>
/// 三条安全口径（都是「宁可少搬，也不制造别人的数据」）：
/// <list type="number">
/// <item><b>凭据一律不进包</b>：设备令牌、账号密码哈希、Webhook 密钥被剥掉；
///     `settings` 里的密文（<c>enc:</c> 开头）也跳过——它用导出方的密钥加密，搬过去也解不开。</item>
/// <item><b>设备与账号只新增、不覆盖</b>：同名/同 ID 的记录保持本机原样。
///     覆盖设备会让教室端当场掉线（令牌被换掉），覆盖账号会把人锁在门外（密码被清空）。</item>
/// <item><b>导入前先备份</b>：由调用方（端点）负责，出问题一键恢复。</item>
/// </list>
/// </para>
/// </summary>
public static class MigrationService
{
    /// <summary>参与迁移的表（顺序不重要，但列在这里便于人工核对「搬了什么」）。</summary>
    public static readonly string[] Tables =
    [
        "settings",           // 品牌 / 时间偏移 / AI 配置等（密文行会跳过）
        "profiles",           // 配置档案（含内容 JSON）
        "groups",             // 分组（含层级与默认档案）
        "tags", "device_tags", // 设备标签
        "devices",            // 设备（令牌剥离 → 作为「预注册占位」，教室端重新注册即认领）
        "users",              // 账号（密码哈希剥离 → 导入后需重置密码；已有账号不覆盖）
        "webhooks",           // 通知渠道（密钥剥离）
        "reminders",          // 定时提醒
        "notice_templates",   // 通知模板
        "timetable_templates",// 课表模板
        "assignments",        // 走班制按时段切档案的规则
    ];

    /// <summary>表名 → 给人看的名字。</summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["settings"] = "系统设置",
        ["profiles"] = "配置档案",
        ["groups"] = "分组",
        ["tags"] = "设备标签",
        ["device_tags"] = "设备与标签的关联",
        ["devices"] = "设备",
        ["users"] = "账号",
        ["webhooks"] = "通知渠道",
        ["reminders"] = "定时提醒",
        ["notice_templates"] = "通知模板",
        ["timetable_templates"] = "课表模板",
        ["assignments"] = "走班制规则",
    };

    /// <summary>必须剥离的敏感列（表名 → 列名）。</summary>
    private static readonly Dictionary<string, string[]> SecretColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["devices"] = ["token"],
        ["users"] = ["password_hash"],
        ["webhooks"] = ["secret"],
    };

    /// <summary>只新增、不覆盖已有的表：覆盖它们会把本机在用的人/设备弄掉线。</summary>
    private static readonly HashSet<string> InsertOnly = new(StringComparer.OrdinalIgnoreCase) { "devices", "users" };

    /// <summary>导出整包（只读，不动数据）。</summary>
    /// <param name="databasePath">SQLite 文件路径。</param>
    /// <param name="serverName">导出方服务器名。</param>
    public static MigrationPackageDto Export(string databasePath, string serverName)
    {
        var package = new MigrationPackageDto
        {
            Product = HubProtocol.ProductName,
            Version = HubProtocol.ProductVersion,
            ServerName = serverName,
            ExportedAt = DateTimeOffset.UtcNow,
        };

        using var connection = Open(databasePath);
        var skippedCipher = 0;
        var stripped = new List<string>();

        foreach (var table in Tables)
        {
            if (!TableExists(connection, table))
            {
                continue;
            }

            var allColumns = Columns(connection, table);
            var secrets = SecretColumns.TryGetValue(table, out var s) ? s : [];
            var kept = allColumns.Where(c => !secrets.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            if (secrets.Length > 0)
            {
                stripped.Add($"{Labels[table]}的 {string.Join("/", secrets)}");
            }

            var entry = new MigrationTableDto { Columns = kept };
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT {string.Join(",", kept.Select(Quote))} FROM {Quote(table)};";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var row = new Dictionary<string, string?>(StringComparer.Ordinal);
                    for (var i = 0; i < kept.Count; i++)
                    {
                        row[kept[i]] = reader.IsDBNull(i)
                            ? null
                            : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                    }

                    // 密文设置跳过：它由导出方的密钥加密，搬到另一台机器上永远解不开，
                    // 留下来只会让新服务器上出现「配置读不出来」的怪现象。
                    if (string.Equals(table, "settings", StringComparison.OrdinalIgnoreCase)
                        && row.TryGetValue("value", out var value)
                        && value is not null
                        && value.StartsWith("enc:", StringComparison.Ordinal))
                    {
                        skippedCipher++;
                        continue;
                    }

                    entry.Rows.Add(row);
                }
            }

            package.Tables[table] = entry;
        }

        package.Notes.Add($"包含：{string.Join("、", Tables.Where(t => package.Tables.ContainsKey(t)).Select(t => $"{Labels[t]}（{package.Tables[t].Rows.Count}）"))}。");
        package.Notes.Add("不包含：审计日志、诊断与指令、客户端日志、在线率采样、会话、注册码与邀请码、API 密钥、临时换课、提醒触发与投递明细——它们是运行时数据或凭据。");
        if (stripped.Count > 0)
        {
            package.Notes.Add($"已剥离的敏感列：{string.Join("；", stripped)}。设备与账号导入后按「只新增」处理，本机已有记录不受影响。");
        }

        if (skippedCipher > 0)
        {
            package.Notes.Add($"有 {skippedCipher} 条加密设置未导出（它们由导出方的密钥加密，换机器后解不开），导入后需要重新填写。");
        }

        return package;
    }

    /// <summary>预览：逐表算出「新增 / 覆盖 / 跳过」，不动任何数据。</summary>
    public static MigrationPreviewDto Preview(string databasePath, MigrationPackageDto package)
    {
        var preview = new MigrationPreviewDto
        {
            Token = HubChecksum.NewToken(),
            ExportedAt = package.ExportedAt,
            Version = package.Version,
            ServerName = package.ServerName,
        };

        using var connection = Open(databasePath);
        foreach (var (table, entry) in package.Tables)
        {
            if (!TableExists(connection, table))
            {
                preview.Tables.Add(new MigrationTablePreviewDto
                {
                    Table = table,
                    Label = Label(table),
                    Skipped = entry.Rows.Count,
                    Note = "本机没有这张表（可能是版本差异），整表跳过。",
                });
                continue;
            }

            var target = Columns(connection, table);
            var writable = entry.Columns.Where(c => target.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            var ignored = entry.Columns.Where(c => !target.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            var keys = PrimaryKeys(connection, table);

            var stat = new MigrationTablePreviewDto { Table = table, Label = Label(table) };
            foreach (var row in entry.Rows)
            {
                if (keys.Count == 0 || keys.Any(k => !row.ContainsKey(k)))
                {
                    stat.Skipped++;
                    continue;
                }

                var exists = Exists(connection, table, keys, row);
                if (!exists)
                {
                    stat.Added++;
                }
                else if (InsertOnly.Contains(table))
                {
                    stat.Skipped++;
                }
                else
                {
                    stat.Updated++;
                }
            }

            var notes = new List<string>();
            if (InsertOnly.Contains(table))
            {
                notes.Add("只新增：本机已有的记录保持不动（同 ID 的会被跳过）");
            }

            if (ignored.Count > 0)
            {
                notes.Add($"本机没有这些列，已忽略：{string.Join("/", ignored)}");
            }

            if (stat.Skipped > 0 && InsertOnly.Contains(table))
            {
                notes.Add($"{stat.Skipped} 条因「本机已有」被跳过");
            }

            if (notes.Count > 0)
            {
                stat.Note = string.Join("；", notes);
            }

            preview.Tables.Add(stat);
        }

        preview.Tables.Sort((a, b) => string.CompareOrdinal(a.Table, b.Table));
        preview.Warnings.Add("导入前会**自动生成一份备份**（类型「导入前」），确认导入后若有问题可在「系统维护」里一键恢复。");
        preview.Warnings.Add("设备与账号只新增、不覆盖：覆盖设备会让教室端当场掉线（令牌被换），覆盖账号会把人锁在门外（密码被清空）。");
        if (preview.Tables.Any(t => t.Updated > 0))
        {
            preview.Warnings.Add("带「覆盖」的表会被本包内容替换（按 ID 匹配）——请确认包里是想要的那一版。");
        }

        return preview;
    }

    /// <summary>应用（写在事务里；调用方负责先备份）。</summary>
    public static MigrationApplyResultDto Apply(string databasePath, MigrationPackageDto package)
    {
        var result = new MigrationApplyResultDto();

        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();

        foreach (var (table, entry) in package.Tables)
        {
            if (!TableExists(connection, table))
            {
                result.Tables.Add(new MigrationTablePreviewDto
                {
                    Table = table,
                    Label = Label(table),
                    Skipped = entry.Rows.Count,
                    Note = "本机没有这张表，整表跳过。",
                });
                continue;
            }

            var target = Columns(connection, table);
            var writable = entry.Columns.Where(c => target.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            var keys = PrimaryKeys(connection, table);
            var stat = new MigrationTablePreviewDto { Table = table, Label = Label(table) };

            foreach (var row in entry.Rows)
            {
                if (keys.Count == 0 || keys.Any(k => !row.ContainsKey(k)))
                {
                    stat.Skipped++;
                    continue;
                }

                var exists = Exists(connection, table, keys, row, transaction);
                if (exists && InsertOnly.Contains(table))
                {
                    stat.Skipped++;
                    continue;
                }

                // INSERT OR REPLACE 而不是 UPDATE：包里的列可能比本机多/少（版本差异），
                // 用它一条语句就能同时覆盖「新增」与「覆盖」两种情况。
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"INSERT OR REPLACE INTO {Quote(table)} ({string.Join(",", writable.Select(Quote))})"
                                      + $" VALUES ({string.Join(",", writable.Select(c => "$" + c))});";
                foreach (var column in writable)
                {
                    command.Parameters.AddWithValue("$" + column, (object?)row[column] ?? DBNull.Value);
                }

                command.ExecuteNonQuery();
                if (exists)
                {
                    stat.Updated++;
                }
                else
                {
                    stat.Added++;
                }
            }

            if (InsertOnly.Contains(table) && stat.Skipped > 0)
            {
                stat.Note = $"只新增：{stat.Skipped} 条因「本机已有」被跳过";
            }

            result.Tables.Add(stat);
        }

        transaction.Commit();
        result.Tables.Sort((a, b) => string.CompareOrdinal(a.Table, b.Table));
        result.Notes.Add("设备令牌与账号密码不在包里（凭据不导出）：被导入的设备是**预注册占位**，"
                         + "教室端重新注册时会按机器名认领它的名称/分组/档案；被导入的新账号需要重置一次密码。");
        return result;
    }

    private static string Label(string table) => Labels.TryGetValue(table, out var label) ? label : table;

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        return connection;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>表的所有列（顺序按建表顺序）。</summary>
    private static List<string> Columns(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(table)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>主键列（复合主键会返回多列，例如 device_tags）。</summary>
    private static List<string> PrimaryKeys(SqliteConnection connection, string table)
    {
        var keys = new List<(int Order, string Name)>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(table)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var pk = reader.GetInt32(5);
            if (pk > 0)
            {
                keys.Add((pk, reader.GetString(1)));
            }
        }

        return keys.OrderBy(k => k.Order).Select(k => k.Name).ToList();
    }

    private static bool Exists(SqliteConnection connection, string table, IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, string?> row, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT 1 FROM {Quote(table)} WHERE "
                              + string.Join(" AND ", keys.Select(k => $"{Quote(k)} IS $" + k)) + " LIMIT 1;";
        foreach (var key in keys)
        {
            command.Parameters.AddWithValue("$" + key, (object?)row[key] ?? DBNull.Value);
        }

        return command.ExecuteScalar() is not null;
    }
}
