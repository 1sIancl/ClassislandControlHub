using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>配置档案、分组与下发绑定关系的数据访问。</summary>
public sealed partial class HubStore
{
    private const string ProfileColumns =
        "id, name, description, code, revision, content, is_default, created_at, updated_at";

    private static ProfileRow ReadProfile(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        Name = GetString(reader, "name"),
        Description = GetString(reader, "description"),
        Code = GetString(reader, "code"),
        Revision = GetInt64(reader, "revision"),
        Content = GetString(reader, "content"),
        IsDefault = GetBool(reader, "is_default"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
        UpdatedAt = GetTimestampOrNow(reader, "updated_at"),
    };

    private static GroupRow ReadGroup(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        Name = GetString(reader, "name"),
        Description = GetString(reader, "description"),
        DefaultProfileId = GetNullableString(reader, "default_profile_id"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
    };

    // ────────────────────────────── 配置档案 ──────────────────────────────

    /// <summary>查询全部配置档案。</summary>
    public async Task<List<ProfileRow>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ProfileColumns} FROM profiles ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ProfileRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadProfile(reader));
        }

        return result;
    }

    /// <summary>按 ID 查询配置档案。</summary>
    public async Task<ProfileRow?> GetProfileAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ProfileColumns} FROM profiles WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    /// <summary>查询默认配置档案。</summary>
    public async Task<ProfileRow?> GetDefaultProfileAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {ProfileColumns} FROM profiles ORDER BY is_default DESC, created_at LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    /// <summary>创建配置档案。</summary>
    public async Task CreateProfileAsync(ProfileRow profile, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO profiles ({ProfileColumns})
            VALUES ($id, $name, $description, $code, $revision, $content, $isDefault, $createdAt, $updatedAt);
            """;
        AddParameters(command,
            ("$id", profile.Id),
            ("$name", profile.Name),
            ("$description", profile.Description),
            ("$code", profile.Code),
            ("$revision", profile.Revision),
            ("$content", profile.Content),
            ("$isDefault", Bool(profile.IsDefault)),
            ("$createdAt", Ts(profile.CreatedAt)),
            ("$updatedAt", Ts(profile.UpdatedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>判断某四位识别码是否已被占用。</summary>
    public async Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM profiles WHERE code = $code;";
        command.Parameters.AddWithValue("$code", code);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    /// <summary>更新配置档案的名称、描述与内容（内容变更会递增该档案的版本号）。</summary>
    public async Task<ProfileRow?> UpdateProfileAsync(string id, string name, string description,
        string content, bool contentChanged, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = contentChanged
            ? """
              UPDATE profiles SET name = $name, description = $description, content = $content,
                                  revision = revision + 1, updated_at = $now
              WHERE id = $id;
              """
            : """
              UPDATE profiles SET name = $name, description = $description, updated_at = $now
              WHERE id = $id;
              """;
        AddParameters(command,
            ("$name", name),
            ("$description", description),
            ("$content", content),
            ("$now", Ts(DateTimeOffset.UtcNow)),
            ("$id", id));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetProfileAsync(id, cancellationToken);
    }

    /// <summary>设置唯一的默认档案。</summary>
    public async Task SetDefaultProfileAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "UPDATE profiles SET is_default = 0;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var set = connection.CreateCommand())
        {
            set.Transaction = (SqliteTransaction)transaction;
            set.CommandText = "UPDATE profiles SET is_default = 1 WHERE id = $id;";
            set.Parameters.AddWithValue("$id", id);
            await set.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>删除配置档案，并解除相关绑定。</summary>
    public async Task DeleteProfileAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var sql in new[]
                 {
                     "DELETE FROM profiles WHERE id = $id;",
                     "UPDATE devices SET profile_id = NULL WHERE profile_id = $id;",
                     "UPDATE groups SET default_profile_id = NULL WHERE default_profile_id = $id;",
                     "DELETE FROM assignments WHERE profile_id = $id;",
                 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>统计配置档案数量。</summary>
    public async Task<int> CountProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM profiles;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    // ────────────────────────────── 分组 ──────────────────────────────

    /// <summary>查询全部分组。</summary>
    public async Task<List<GroupRow>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, description, default_profile_id, created_at FROM groups ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<GroupRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadGroup(reader));
        }

        return result;
    }

    /// <summary>按 ID 查询分组。</summary>
    public async Task<GroupRow?> GetGroupAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, description, default_profile_id, created_at FROM groups WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadGroup(reader) : null;
    }

    /// <summary>创建分组。</summary>
    public async Task CreateGroupAsync(GroupRow group, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO groups (id, name, description, default_profile_id, created_at)
            VALUES ($id, $name, $description, $profileId, $createdAt);
            """;
        AddParameters(command,
            ("$id", group.Id),
            ("$name", group.Name),
            ("$description", group.Description),
            ("$profileId", TextOrNull(group.DefaultProfileId)),
            ("$createdAt", Ts(group.CreatedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>更新分组。</summary>
    public async Task UpdateGroupAsync(string id, string name, string description, string? defaultProfileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE groups SET name = $name, description = $description, default_profile_id = $profileId
            WHERE id = $id;
            """;
        AddParameters(command,
            ("$name", name),
            ("$description", description),
            ("$profileId", TextOrNull(defaultProfileId)),
            ("$id", id));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>删除分组。组内设备会变为未分组。</summary>
    public async Task DeleteGroupAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var sql in new[]
                 {
                     "UPDATE devices SET group_id = NULL WHERE group_id = $id;",
                     "DELETE FROM assignments WHERE target_type = 'group' AND target_id = $id;",
                     "DELETE FROM groups WHERE id = $id;",
                 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>统计各分组的设备数量。</summary>
    public async Task<Dictionary<string, int>> GetGroupDeviceCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT group_id, COUNT(1) AS c FROM devices WHERE group_id IS NOT NULL GROUP BY group_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result[GetString(reader, "group_id")] = GetInt32(reader, "c");
        }

        return result;
    }

    /// <summary>统计每个档案被直接绑定的目标数量。</summary>
    public async Task<Dictionary<string, int>> GetProfileBindingCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, SUM(c) AS c FROM (
                SELECT profile_id, COUNT(1) AS c FROM devices WHERE profile_id IS NOT NULL GROUP BY profile_id
                UNION ALL
                SELECT default_profile_id AS profile_id, COUNT(1) AS c FROM groups
                    WHERE default_profile_id IS NOT NULL GROUP BY default_profile_id
            ) GROUP BY profile_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result[GetString(reader, "profile_id")] = GetInt32(reader, "c");
        }

        return result;
    }
}
