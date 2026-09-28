using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 通知模板的数据访问：把「广播站通知」「放学提醒」这类常用内容存成模板，
/// 发通知时一键套用，避免每次手打。
/// </summary>
public sealed partial class HubStore
{
    private const string NoticeTemplateColumns = "id, name, title, content, speak, created_at, updated_at";

    private static NoticeTemplateRow ReadNoticeTemplate(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        Name = GetString(reader, "name"),
        Title = GetString(reader, "title"),
        Content = GetString(reader, "content"),
        Speak = GetBool(reader, "speak"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
        UpdatedAt = GetTimestampOrNow(reader, "updated_at"),
    };

    /// <summary>全部通知模板（最近更新的排在前面）。</summary>
    public async Task<List<NoticeTemplateRow>> GetNoticeTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {NoticeTemplateColumns} FROM notice_templates ORDER BY updated_at DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var result = new List<NoticeTemplateRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadNoticeTemplate(reader));
        }

        return result;
    }

    /// <summary>按 ID 查询通知模板。</summary>
    public async Task<NoticeTemplateRow?> GetNoticeTemplateAsync(string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {NoticeTemplateColumns} FROM notice_templates WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNoticeTemplate(reader) : null;
    }

    /// <summary>新建通知模板。</summary>
    public async Task CreateNoticeTemplateAsync(NoticeTemplateRow row,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notice_templates (id, name, title, content, speak, created_at, updated_at)
            VALUES ($id, $name, $title, $content, $speak, $createdAt, $updatedAt);
            """;
        AddParameters(command,
            ("$id", row.Id),
            ("$name", row.Name),
            ("$title", row.Title),
            ("$content", row.Content),
            ("$speak", row.Speak ? 1 : 0),
            ("$createdAt", Ts(row.CreatedAt)),
            ("$updatedAt", Ts(row.UpdatedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>更新通知模板内容。</summary>
    public async Task<bool> UpdateNoticeTemplateAsync(NoticeTemplateRow row,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE notice_templates
               SET name = $name, title = $title, content = $content, speak = $speak, updated_at = $updatedAt
             WHERE id = $id;
            """;
        AddParameters(command,
            ("$name", row.Name),
            ("$title", row.Title),
            ("$content", row.Content),
            ("$speak", row.Speak ? 1 : 0),
            ("$updatedAt", Ts(DateTimeOffset.UtcNow)),
            ("$id", row.Id));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>删除通知模板。</summary>
    public async Task<bool> DeleteNoticeTemplateAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notice_templates WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}

/// <summary>通知模板的一行。</summary>
public sealed class NoticeTemplateRow
{
    /// <summary>模板 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>模板名称（管理端识别用，例如「广播站通知」）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>通知标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>通知正文。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>是否语音播报。</summary>
    public bool Speak { get; set; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>最近更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
