using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 配置档案的历史版本（快照）数据访问。
/// <para>每次保存档案前把「保存前的内容」存一份，换课 / 改课表改坏时可以一键回滚。</para>
/// </summary>
public sealed partial class HubStore
{
    /// <summary>每个档案保留的快照份数上限，超出后丢弃最旧的。</summary>
    public const int ProfileVersionKeep = 20;

    private const string ProfileVersionColumns =
        "id, profile_id, revision, name, description, content, reason, created_by, created_at";

    private static ProfileVersionRow ReadProfileVersion(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        ProfileId = GetString(reader, "profile_id"),
        Revision = GetInt64(reader, "revision"),
        Name = GetString(reader, "name"),
        Description = GetString(reader, "description"),
        Content = GetString(reader, "content"),
        Reason = GetString(reader, "reason"),
        CreatedBy = GetString(reader, "created_by"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
    };

    /// <summary>某个档案的历史版本（新的排在前面）。</summary>
    public async Task<List<ProfileVersionRow>> GetProfileVersionsAsync(string profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {ProfileVersionColumns} FROM profile_versions WHERE profile_id = $profileId ORDER BY created_at DESC;";
        command.Parameters.AddWithValue("$profileId", profileId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ProfileVersionRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadProfileVersion(reader));
        }

        return result;
    }

    /// <summary>
    /// 按「档案 + 版本号」查找快照：用于还原设备**应用当时**的内容（下发前差异预览的基线）。
    /// <para>快照是「保存前留的那一份」，因此版本号 <c>v</c> 的快照内容 = 第 v 版的内容。</para>
    /// </summary>
    public async Task<ProfileVersionRow?> GetProfileVersionByRevisionAsync(string profileId, long revision,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {ProfileVersionColumns} FROM profile_versions WHERE profile_id = $profileId AND revision = $revision LIMIT 1;";
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfileVersion(reader) : null;
    }

    /// <summary>按 ID 查询某个历史版本。</summary>
    public async Task<ProfileVersionRow?> GetProfileVersionAsync(string versionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {ProfileVersionColumns} FROM profile_versions WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", versionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfileVersion(reader) : null;
    }

    /// <summary>保存一份快照，并把该档案的快照数量裁到上限以内。</summary>
    public async Task CreateProfileVersionAsync(ProfileVersionRow row,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO profile_versions
                    (id, profile_id, revision, name, description, content, reason, created_by, created_at)
                VALUES
                    ($id, $profileId, $revision, $name, $description, $content, $reason, $createdBy, $createdAt);
                """;
            AddParameters(command,
                ("$id", row.Id),
                ("$profileId", row.ProfileId),
                ("$revision", row.Revision),
                ("$name", row.Name),
                ("$description", row.Description),
                ("$content", row.Content),
                ("$reason", row.Reason),
                ("$createdBy", row.CreatedBy),
                ("$createdAt", Ts(row.CreatedAt)));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // 只保留最近 N 份：按时间倒序跳过前 N-1 条，其余删除。
        await using (var prune = connection.CreateCommand())
        {
            prune.CommandText = """
                DELETE FROM profile_versions
                 WHERE profile_id = $profileId
                   AND id NOT IN (
                       SELECT id FROM profile_versions
                        WHERE profile_id = $profileId
                        ORDER BY created_at DESC
                        LIMIT $keep
                   );
                """;
            AddParameters(prune, ("$profileId", row.ProfileId), ("$keep", ProfileVersionKeep));
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>删除某个历史版本。</summary>
    public async Task<bool> DeleteProfileVersionAsync(string versionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM profile_versions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", versionId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>清空某个档案的全部历史版本（删除档案时调用）。</summary>
    public async Task DeleteProfileVersionsOfProfileAsync(string profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM profile_versions WHERE profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", profileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>配置档案历史版本的一行。</summary>
public sealed class ProfileVersionRow
{
    /// <summary>快照 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>所属档案 ID。</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>快照对应的档案内容版本号。</summary>
    public long Revision { get; set; }

    /// <summary>快照时的档案名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>快照时的档案说明。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>快照内容（ContentBundleDto 的 JSON）。</summary>
    public string Content { get; set; } = "{}";

    /// <summary>产生快照的原因（例如「保存前自动备份」「恢复前自动备份」）。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>操作人用户名。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
