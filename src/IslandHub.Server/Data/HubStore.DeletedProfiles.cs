using Microsoft.Data.Sqlite;

namespace IslandHub.Server.Data;

/// <summary>
/// 已删除档案的留档（#137）。
///
/// <para>
/// 删除档案时，<c>profiles</c> 那行连同 <c>content</c>（课表、科目、设置）一起消失；
/// 时间表在 <c>time_layouts</c> 里，**不受影响**。所以这里只负责把整份内容留一份，
/// 让「删错了」有救。
/// </para>
/// </summary>
public sealed partial class HubStore
{
    private const string DeletedProfileColumns =
        "id, profile_id, name, description, content, revision, deleted_at, deleted_by";

    private static DeletedProfileRow ReadDeletedProfile(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        ProfileId = GetString(reader, "profile_id"),
        Name = GetString(reader, "name"),
        Description = GetString(reader, "description"),
        Content = GetString(reader, "content"),
        Revision = GetInt64(reader, "revision"),
        DeletedAt = GetTimestampOrNow(reader, "deleted_at"),
        DeletedBy = GetString(reader, "deleted_by"),
    };

    /// <summary>列出留档（新的在前）。不含 content，列表不需要几 MB 的正文。</summary>
    public async Task<List<DeletedProfileRow>> GetDeletedProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, profile_id, name, description, '' AS content, revision, deleted_at, deleted_by
            FROM deleted_profiles ORDER BY deleted_at DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var result = new List<DeletedProfileRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadDeletedProfile(reader));
        }

        return result;
    }

    /// <summary>取一条留档（含 content，用于恢复）。</summary>
    public async Task<DeletedProfileRow?> GetDeletedProfileAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {DeletedProfileColumns} FROM deleted_profiles WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDeletedProfile(reader) : null;
    }

    /// <summary>彻底删除一条留档（连内容一起）。</summary>
    public async Task<bool> PurgeDeletedProfileAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM deleted_profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
