using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>Webhook 配置的数据访问。</summary>
public sealed partial class HubStore
{
    private const string WebhookColumns =
        "id, name, url, kind, secret, events, enabled, created_at, "
        + "last_attempt_at, last_success_at, last_status, last_error, fail_count, "
        + "mention_all, headers, quiet_hours, timeout_seconds, max_retries";

    /// <summary>读取一行 Webhook：<c>secret</c> 列是密文，这里解密为明文（失败则置空并标记不可用）。</summary>
    private WebhookRow ReadWebhook(SqliteDataReader reader)
    {
        var secret = ReadSecret(GetString(reader, "secret"), out var secretAvailable);
        return new WebhookRow
        {
            Id = GetString(reader, "id"),
            Name = GetString(reader, "name"),
            Url = GetString(reader, "url"),
            Kind = GetString(reader, "kind"),
            Secret = secret,
            SecretUnavailable = !secretAvailable,
            Events = GetString(reader, "events"),
            Enabled = GetBool(reader, "enabled"),
            CreatedAt = GetTimestampOrNow(reader, "created_at"),
            LastAttemptAt = GetTimestamp(reader, "last_attempt_at"),
            LastSuccessAt = GetTimestamp(reader, "last_success_at"),
            LastStatusCode = GetInt32(reader, "last_status"),
            LastError = GetNullableString(reader, "last_error"),
            FailCount = GetInt32(reader, "fail_count"),
            MentionAll = GetBool(reader, "mention_all"),
            Headers = GetString(reader, "headers"),
            QuietHours = GetString(reader, "quiet_hours"),
            TimeoutSeconds = GetInt32(reader, "timeout_seconds"),
            MaxRetries = GetInt32(reader, "max_retries"),
        };
    }

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
            INSERT INTO webhooks (id, name, url, kind, secret, events, enabled, created_at,
                                  mention_all, headers, quiet_hours, timeout_seconds, max_retries)
            VALUES ($id, $name, $url, $kind, $secret, $events, $enabled, $createdAt,
                    $mentionAll, $headers, $quietHours, $timeoutSeconds, $maxRetries);
            """;
        AddParameters(command,
            ("$id", row.Id),
            ("$name", row.Name),
            ("$url", row.Url),
            ("$kind", row.Kind),
            // 加签密钥以密文落库（#36）；ProtectSecret 幂等，重复保存不会二次加密。
            ("$secret", ProtectSecret(row.Secret)),
            ("$events", row.Events),
            ("$enabled", row.Enabled ? 1 : 0),
            ("$createdAt", Ts(row.CreatedAt)),
            ("$mentionAll", row.MentionAll ? 1 : 0),
            ("$headers", row.Headers),
            ("$quietHours", row.QuietHours),
            ("$timeoutSeconds", row.TimeoutSeconds),
            ("$maxRetries", row.MaxRetries));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>更新 Webhook。</summary>
    public async Task<bool> UpdateWebhookAsync(WebhookRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // 密钥列特殊处理：若原值解密失败（密钥文件被更换 / 丢失）且本次没有填新密钥，
        // 保留库中的原密文不动——否则管理员改个名字就会把还能救回来的密文覆盖成空。
        var keepSecret = row.SecretUnavailable && row.Secret.Length == 0;
        command.CommandText = """
            UPDATE webhooks
               SET name = $name, url = $url, kind = $kind,
                   secret = CASE WHEN $keepSecret = 1 THEN secret ELSE $secret END,
                   events = $events,
                   enabled = $enabled, mention_all = $mentionAll, headers = $headers,
                   quiet_hours = $quietHours, timeout_seconds = $timeoutSeconds, max_retries = $maxRetries
             WHERE id = $id;
            """;
        AddParameters(command,
            ("$name", row.Name),
            ("$url", row.Url),
            ("$kind", row.Kind),
            // 加签密钥以密文落库（#36）；ProtectSecret 幂等，重复保存不会二次加密。
            ("$keepSecret", keepSecret ? 1 : 0),
            ("$secret", ProtectSecret(row.Secret)),
            ("$events", row.Events),
            ("$enabled", row.Enabled ? 1 : 0),
            ("$mentionAll", row.MentionAll ? 1 : 0),
            ("$headers", row.Headers),
            ("$quietHours", row.QuietHours),
            ("$timeoutSeconds", row.TimeoutSeconds),
            ("$maxRetries", row.MaxRetries),
            ("$id", row.Id));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>删除 Webhook。</summary>
    public async Task<bool> DeleteWebhookAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using         var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM webhooks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// 记录一次投递结果，供管理端显示「最近投递 / 失败原因 / 连续失败次数」。
    /// <para>失败时保留上一次成功时间——这样「多久没成功过了」一眼可见。</para>
    /// </summary>
    public async Task RecordWebhookResultAsync(string id, bool ok, int statusCode, string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE webhooks SET
                last_attempt_at = $now,
                last_success_at = CASE WHEN $ok = 1 THEN $now ELSE last_success_at END,
                last_status     = $status,
                last_error      = $error,
                fail_count      = CASE WHEN $ok = 1 THEN 0 ELSE fail_count + 1 END
            WHERE id = $id;
            """;
        AddParameters(command,
            ("$now", Ts(DateTimeOffset.UtcNow)),
            ("$ok", ok ? 1 : 0),
            ("$status", statusCode),
            ("$error", TextOrNull(ok ? null : error)),
            ("$id", id));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>每个 Webhook 保留的投递明细条数：诊断用，不需要长期堆积。</summary>
    private const int DeliveryKeep = 50;

    /// <summary>写入一条投递明细，并把该 Webhook 的明细裁到最近 <see cref="DeliveryKeep"/> 条。</summary>
    public async Task AddWebhookDeliveryAsync(WebhookDeliveryRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO webhook_deliveries
                    (id, webhook_id, event, title, ok, skipped, status, attempts, duration_ms,
                     error, response, created_at)
                VALUES ($id, $webhookId, $event, $title, $ok, $skipped, $status, $attempts, $duration,
                        $error, $response, $createdAt);
                """;
            AddParameters(command,
                ("$id", row.Id),
                ("$webhookId", row.WebhookId),
                ("$event", row.Event),
                ("$title", row.Title),
                ("$ok", row.Success ? 1 : 0),
                ("$skipped", row.Skipped ? 1 : 0),
                ("$status", row.StatusCode),
                ("$attempts", row.Attempts),
                ("$duration", row.DurationMs),
                ("$error", TextOrNull(row.Error)),
                ("$response", TextOrNull(row.Response)),
                ("$createdAt", Ts(row.CreatedAt)));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var prune = connection.CreateCommand();
        prune.CommandText = """
            DELETE FROM webhook_deliveries
             WHERE webhook_id = $webhookId
               AND id NOT IN (SELECT id FROM webhook_deliveries
                               WHERE webhook_id = $webhookId
                               ORDER BY created_at DESC LIMIT $keep);
            """;
        prune.Parameters.AddWithValue("$webhookId", row.WebhookId);
        prune.Parameters.AddWithValue("$keep", DeliveryKeep);
        await prune.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>最近若干条投递明细（新的在前）。</summary>
    public async Task<List<WebhookDeliveryRow>> GetWebhookDeliveriesAsync(string webhookId, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, webhook_id, event, title, ok, skipped, status, attempts, duration_ms,
                   error, response, created_at
              FROM webhook_deliveries
             WHERE webhook_id = $webhookId
             ORDER BY created_at DESC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$webhookId", webhookId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var result = new List<WebhookDeliveryRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WebhookDeliveryRow
            {
                Id = GetString(reader, "id"),
                WebhookId = GetString(reader, "webhook_id"),
                Event = GetString(reader, "event"),
                Title = GetString(reader, "title"),
                Success = GetBool(reader, "ok"),
                Skipped = GetBool(reader, "skipped"),
                StatusCode = GetInt32(reader, "status"),
                Attempts = GetInt32(reader, "attempts"),
                DurationMs = GetInt32(reader, "duration_ms"),
                Error = GetNullableString(reader, "error"),
                Response = GetNullableString(reader, "response"),
                CreatedAt = GetTimestampOrNow(reader, "created_at"),
            });
        }

        return result;
    }

    /// <summary>清空某个 Webhook 的投递明细。</summary>
    public async Task<int> ClearWebhookDeliveriesAsync(string webhookId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM webhook_deliveries WHERE webhook_id = $webhookId;";
        command.Parameters.AddWithValue("$webhookId", webhookId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 最近一次**成功**投递某条通知的时间（用于抑制短时间内的重复通知）。
    /// <para>按「事件 + 标题」匹配：标题里带设备名，因此不同教室掉线不会互相抑制，
    /// 只有同一台设备的反复上报才会被合并。</para>
    /// </summary>
    public async Task<DateTimeOffset?> GetLastSuccessDeliveryAsync(string webhookId, string eventName, string title,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT created_at FROM webhook_deliveries
             WHERE webhook_id = $webhookId AND event = $event AND title = $title
               AND ok = 1 AND skipped = 0
             ORDER BY created_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$webhookId", webhookId);
        command.Parameters.AddWithValue("$event", eventName);
        command.Parameters.AddWithValue("$title", title);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
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

    /// <summary>加签密钥（钉钉可选）。写入时加密落库，读取时已解密为明文（#36）。</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>密钥解密失败（密钥文件被更换 / 丢失）→ 不能按「未配置」静默处理，投递时应直接报错。</summary>
    public bool SecretUnavailable { get; set; }

    /// <summary>订阅的事件类型（JSON 数组文本）。</summary>
    public string Events { get; set; } = string.Empty;

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>最近一次投递尝试时间（含失败）。</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>最近一次投递成功时间。</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>最近一次投递的 HTTP 状态码（0 = 网络错误 / 超时）。</summary>
    public int LastStatusCode { get; set; }

    /// <summary>最近一次投递失败的原因（成功时为空）。</summary>
    public string? LastError { get; set; }

    /// <summary>连续失败次数（成功一次即清零）。</summary>
    public int FailCount { get; set; }

    /// <summary>推送时是否 @所有人。</summary>
    public bool MentionAll { get; set; }

    /// <summary>自定义请求头（每行 <c>名称: 值</c>）。</summary>
    public string Headers { get; set; } = string.Empty;

    /// <summary>静默时段，如 <c>22:00-07:00</c>（支持跨夜）；留空表示不静默。</summary>
    public string QuietHours { get; set; } = string.Empty;

    /// <summary>单次请求超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>失败重试次数。</summary>
    public int MaxRetries { get; set; } = 2;
}

/// <summary>Webhook 投递明细的一行。</summary>
public sealed class WebhookDeliveryRow
{
    /// <summary>记录 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>所属 Webhook。</summary>
    public string WebhookId { get; set; } = string.Empty;

    /// <summary>事件名（测试消息为 <c>test</c>）。</summary>
    public string Event { get; set; } = string.Empty;

    /// <summary>消息标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>是否投递成功。</summary>
    public bool Success { get; set; }

    /// <summary>是否因静默时段或重复抑制被跳过。</summary>
    public bool Skipped { get; set; }

    /// <summary>HTTP 状态码（0 = 网络错误 / 超时 / 未投递）。</summary>
    public int StatusCode { get; set; }

    /// <summary>实际尝试次数。</summary>
    public int Attempts { get; set; } = 1;

    /// <summary>总耗时（毫秒，含重试与退避）。</summary>
    public int DurationMs { get; set; }

    /// <summary>失败原因或跳过原因。</summary>
    public string? Error { get; set; }

    /// <summary>接收端响应片段。</summary>
    public string? Response { get; set; }

    /// <summary>发生时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
