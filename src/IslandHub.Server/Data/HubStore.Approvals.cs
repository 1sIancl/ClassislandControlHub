using Microsoft.Data.Sqlite;

namespace IslandHub.Server.Data;

/// <summary>一条操作审批申请。</summary>
public sealed class ApprovalRow
{
    /// <summary>申请 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>操作类型（见 <c>ApprovalKinds</c>）。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>一句话说明要做什么。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>原始参数（JSON）。</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>状态（见 <c>ApprovalStatuses</c>）。</summary>
    public string Status { get; set; } = "pending";

    /// <summary>发起人账号。</summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>发起时刻。</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>审批人账号。</summary>
    public string? DecidedBy { get; set; }

    /// <summary>处理时刻。</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>审批意见。</summary>
    public string? Comment { get; set; }

    /// <summary>执行结果摘要。</summary>
    public string? Result { get; set; }
}

/// <summary>操作审批（#35）的存取。</summary>
public partial class HubStore
{
    /// <summary>最多保留的审批记录数（更早的清掉，别让这张表无限长）。</summary>
    private const int ApprovalKeep = 500;

    /// <summary>新建一条审批申请。</summary>
    public async Task CreateApprovalAsync(ApprovalRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO approval_requests (
                id, kind, title, payload, status, requested_by, requested_at)
            VALUES ($id, $kind, $title, $payload, $status, $by, $at);
            """;
        AddParameters(command,
            ("$id", row.Id),
            ("$kind", row.Kind),
            ("$title", row.Title),
            ("$payload", row.Payload),
            ("$status", row.Status),
            ("$by", row.RequestedBy),
            ("$at", Ts(row.RequestedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 取审批记录。最近 200 条，可按状态与发起人过滤。
    /// <para>发起人过滤用于「普通账号只看自己的申请」——审批人（超管）才看全部。</para>
    /// </summary>
    public async Task<List<ApprovalRow>> GetApprovalsAsync(string? status, string? requestedBy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, title, payload, status, requested_by, requested_at,
                   decided_by, decided_at, comment, result
              FROM approval_requests
             WHERE ($status IS NULL OR status = $status)
               AND ($by IS NULL OR requested_by = $by)
             ORDER BY requested_at DESC
             LIMIT 200;
            """;
        command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("$by", (object?)requestedBy ?? DBNull.Value);

        var rows = new List<ApprovalRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ApprovalRow
            {
                Id = reader.GetString(0),
                Kind = reader.GetString(1),
                Title = reader.GetString(2),
                Payload = reader.GetString(3),
                Status = reader.GetString(4),
                RequestedBy = reader.GetString(5),
                RequestedAt = ParseTs(reader.GetString(6)),
                DecidedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
                DecidedAt = reader.IsDBNull(8) ? null : ParseTs(reader.GetString(8)),
                Comment = reader.IsDBNull(9) ? null : reader.GetString(9),
                Result = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }

        return rows;
    }

    /// <summary>按 ID 取一条。</summary>
    public async Task<ApprovalRow?> GetApprovalAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, title, payload, status, requested_by, requested_at,
                   decided_by, decided_at, comment, result
              FROM approval_requests
             WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ApprovalRow
        {
            Id = reader.GetString(0),
            Kind = reader.GetString(1),
            Title = reader.GetString(2),
            Payload = reader.GetString(3),
            Status = reader.GetString(4),
            RequestedBy = reader.GetString(5),
            RequestedAt = ParseTs(reader.GetString(6)),
            DecidedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
            DecidedAt = reader.IsDBNull(8) ? null : ParseTs(reader.GetString(8)),
            Comment = reader.IsDBNull(9) ? null : reader.GetString(9),
            Result = reader.IsDBNull(10) ? null : reader.GetString(10),
        };
    }

    /// <summary>
    /// 落定一条申请的处理结果。
    /// <para>
    /// 条件里带 <c>status = 'pending'</c>：两个人同时点「通过」时，只有一个人能改成功——
    /// 否则同一次恢复会被执行两遍（第二遍还会覆盖掉第一遍之后别人做的改动）。
    /// </para>
    /// </summary>
    public async Task<bool> DecideApprovalAsync(string id, string status, string decidedBy, string? comment,
        string? result, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE approval_requests
               SET status = $status, decided_by = $by, decided_at = $at, comment = $comment, result = $result
             WHERE id = $id AND status = 'pending';
            """;
        AddParameters(command,
            ("$id", id),
            ("$status", status),
            ("$by", decidedBy),
            ("$at", Ts(DateTimeOffset.UtcNow)),
            ("$comment", TextOrNull(comment)),
            ("$result", TextOrNull(result)));

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected > 0)
        {
            await PruneApprovalsAsync(connection, cancellationToken);
        }

        return affected > 0;
    }

    /// <summary>
    /// 回写执行结果（**不要求**当前状态是 pending）。
    /// <para>
    /// 为什么单独一个方法：像「恢复备份」这种操作会整库覆盖，执行后那一行可能已经不存在了
    /// （被备份里的旧库替换掉），所以回写只能尽力而为；而「决定」必须在执行**之前**落定。
    /// </para>
    /// </summary>
    public async Task<bool> UpdateApprovalResultAsync(string id, string status, string? result,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE approval_requests
               SET status = $status, result = $result
             WHERE id = $id;
            """;
        AddParameters(command,
            ("$id", id),
            ("$status", status),
            ("$result", TextOrNull(result)));

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>只保留最近若干条，避免审批表随年份无限增长。</summary>
    private static async Task PruneApprovalsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM approval_requests
             WHERE id NOT IN (
                   SELECT id FROM approval_requests ORDER BY requested_at DESC LIMIT $keep);
            """;
        command.Parameters.AddWithValue("$keep", ApprovalKeep);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
