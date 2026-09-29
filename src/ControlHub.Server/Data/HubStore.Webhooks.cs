using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>Webhook 配置的数据访问。</summary>
public sealed partial class HubStore
{
    private const string WebhookColumns = "id, name, url, kind, secret, events, enabled, created_at";

    private static WebhookRow ReadWebhook(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        Name = GetString(reader, "name"),
        Url = GetString(reader, "url"),
        Kind = GetString(reader, "kind"),
        Secret = GetString(reader, "secret"),
        Events = GetString(reader, "events"),
        Enabled = GetBool(reader, "enabled"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
    };

    /// <summary>全部 Webhook 配置（新的排在前面）。</summary>
    public async Task<List<WebhookRow>> GetWebhooksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {WebhookColumns} FROM webhooks ORDER BY created_at DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var result = new List<WebhookRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadWebhook(reader));
        }

        return result;
    }

    /// <summary>按 ID 查询 Webhook。</summary>
    public async Task<WebhookRow?> GetWebhookAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {WebhookColumns} FROM webhooks WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadWebhook(reader) : null;
    }

    /// <summary>新建 Webhook。</summary>
    public async Task CreateWebhookAsync(WebhookRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO webhooks (id, name, url, kind, secret, events, enabled, created_at)
            VALUES ($id, $name, $url, $kind, $secret, $events, $enabled, $createdAt);
            """;
        AddParameters(command,
            ("$id", row.Id),
            ("$name", row.Name),
            ("$url", row.Url),
            ("$kind", row.Kind),
            ("$secret", row.Secret),
            ("$events", row.Events),
            ("$enabled", row.Enabled ? 1 : 0),
            ("$createdAt", Ts(row.CreatedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>更新 Webhook。</summary>
    public async Task<bool> UpdateWebhookAsync(WebhookRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE webhooks
               SET name = $name, url = $url, kind = $kind, secret = $secret, events = $events, enabled = $enabled
             WHERE id = $id;
            """;
        AddParameters(command,
            ("$name", row.Name),
            ("$url", row.Url),
            ("$kind", row.Kind),
            ("$secret", row.Secret),
            ("$events", row.Events),
            ("$enabled", row.Enabled ? 1 : 0),
            ("$id", row.Id));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>删除 Webhook。</summary>
    public async Task<bool> DeleteWebhookAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM webhooks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}

/// <summary>Webhook 配置的一行。</summary>
public sealed class WebhookRow
{
    /// <summary>配置 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>接收地址。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>接收端类型（wecom / dingtalk / feishu / generic）。</summary>
    public string Kind { get; set; } = "generic";

    /// <summary>加签密钥（钉钉可选）。</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>订阅的事件类型（JSON 数组文本）。</summary>
    public string Events { get; set; } = string.Empty;

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
