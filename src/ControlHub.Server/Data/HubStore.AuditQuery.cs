using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>审计日志的筛选条件（分页查询与 CSV 导出共用同一套条件，避免「看到的」和「导出的」不是一批）。</summary>
public sealed class AuditFilter
{
    /// <summary>全文关键词：匹配操作者 / 动作 / 目标 / 详情。</summary>
    public string? Search { get; set; }

    /// <summary>操作者（精确匹配）。</summary>
    public string? Actor { get; set; }

    /// <summary>动作前缀，例如 <c>device.</c>、<c>admin.login</c>，用于按类别看。</summary>
    public string? ActionPrefix { get; set; }

    /// <summary>
    /// 操作对象名称（**精确匹配** <c>target</c>）：用于「某个对象的历史时间线」（#42）。
    /// <para>与 <see cref="Search"/> 的区别：search 是「提到过这个名字的日志」（包括详情里顺带提到的），
    /// 时间线要的是「以这个对象为目标的操作」，所以要精确匹配，否则噪声大到没法看。</para>
    /// <para>注意 target 存的是**当时的名称**：对象改过名的话，改名前的那段历史查不到。</para>
    /// </summary>
    public string? Target { get; set; }

    /// <summary>来源 IP（精确匹配）。</summary>
    public string? Ip { get; set; }

    /// <summary>起始时间（含）。</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>结束时间（含）。</summary>
    public DateTimeOffset? To { get; set; }
}

/// <summary>审计日志的筛选查询与导出。</summary>
public sealed partial class HubStore
{
    private const string AuditColumns = "id, ts, actor, action, target, detail, ip";

    /// <summary>按筛选条件分页查询审计日志。</summary>
    public async Task<(List<AuditLogRow> Items, int Total)> QueryAuditLogsAsync(AuditFilter filter, int page,
        int pageSize, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var (where, args) = BuildAuditWhere(filter);

        await using var connection = await OpenAsync(cancellationToken);

        int total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(1) FROM audit_logs{where};";
            Bind(count, args);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken) ?? 0);
        }

        var items = new List<AuditLogRow>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = $"""
                SELECT {AuditColumns} FROM audit_logs{where}
                ORDER BY id DESC LIMIT $limit OFFSET $offset;
                """;
            Bind(query, args);
            query.Parameters.AddWithValue("$limit", pageSize);
            query.Parameters.AddWithValue("$offset", (page - 1) * pageSize);

            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
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
        }

        return (items, total);
    }

    /// <summary>按筛选条件导出审计日志（最多 <paramref name="limit"/> 条，新的在前）。</summary>
    public async Task<List<AuditLogRow>> ExportAuditLogsAsync(AuditFilter filter, int limit,
        CancellationToken cancellationToken = default)
    {
        var (where, args) = BuildAuditWhere(filter);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AuditColumns} FROM audit_logs{where} ORDER BY id DESC LIMIT $limit;";
        Bind(command, args);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100_000));

        var items = new List<AuditLogRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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

    /// <summary>拼出 WHERE 子句与参数。条件为空时返回空串（即不筛选）。</summary>
    private static (string Where, List<(string Name, object Value)> Args) BuildAuditWhere(AuditFilter filter)
    {
        var clauses = new List<string>();
        var args = new List<(string, object)>();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            clauses.Add("(actor LIKE $search OR action LIKE $search OR target LIKE $search OR detail LIKE $search)");
            args.Add(("$search", $"%{filter.Search.Trim()}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Actor))
        {
            clauses.Add("actor = $actor");
            args.Add(("$actor", filter.Actor.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(filter.ActionPrefix))
        {
            // 前缀匹配：既能按 device. 看整类，也能按 device.command 看具体动作。
            clauses.Add("action LIKE $actionPrefix");
            args.Add(("$actionPrefix", filter.ActionPrefix.Trim() + "%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Target))
        {
            clauses.Add("target = $target");
            args.Add(("$target", filter.Target.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(filter.Ip))
        {
            clauses.Add("ip = $ip");
            args.Add(("$ip", filter.Ip.Trim()));
        }

        if (filter.From.HasValue)
        {
            clauses.Add("ts >= $from");
            args.Add(("$from", Ts(filter.From.Value)));
        }

        if (filter.To.HasValue)
        {
            clauses.Add("ts <= $to");
            args.Add(("$to", Ts(filter.To.Value)));
        }

        return (clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses), args);
    }

    private static void Bind(SqliteCommand command, List<(string Name, object Value)> args)
    {
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }
}
