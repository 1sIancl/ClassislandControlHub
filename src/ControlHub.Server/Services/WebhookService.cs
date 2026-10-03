using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using Microsoft.Extensions.Hosting;

namespace ControlHub.Server.Services;

/// <summary>
/// Webhook 推送：把关键事件（设备掉线 / 恢复 / 同步失败 / 指令失败）发到企业微信、钉钉、飞书或自定义端点。
/// <para>三条设计约束，都是实际踩过的坑：</para>
/// <para>① <b>不阻塞主流程</b>：调用方只往队列里投一条，投递与重试都在后台进行。
/// 否则一个慢的（或挂起的）接收端会把设备心跳与指令回报一起拖慢。</para>
/// <para>② <b>不能只看 HTTP 状态码</b>：企微 / 钉钉 / 飞书「没发出去」时同样返回 200，
/// 业务失败只体现在响应体的 errcode（机器人被停用、Webhook key 无效、被移出群、触发频率限制）。
/// 必须解析响应体，否则界面提示「发送成功」而群里没有消息。</para>
/// <para>③ <b>失败要看得见</b>：每次投递结果落库（时间 / 状态码 / 原因 / 连续失败次数），
/// 管理端直接显示，而不是只能翻服务端日志。</para>
/// </summary>
public sealed class WebhookService(
    HubStore store,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookService> logger) : BackgroundService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>失败后的重试间隔（数组长度 = 重试次数）。接收端冷启动、网络抖动都靠它救回来。</summary>
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];

    /// <summary>投递队列：满了丢最旧的，绝不反过来阻塞设备上报。</summary>
    private readonly Channel<WebhookJob> queue = Channel.CreateBounded<WebhookJob>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>按事件类型推送：只发给「已启用且订阅了该事件」的配置项。调用方不会被网络阻塞。</summary>
    public async Task NotifyAsync(string eventName, string title, string message,
        Dictionary<string, object?>? detail = null, CancellationToken cancellationToken = default)
    {
        List<WebhookRow> hooks;
        try
        {
            hooks = (await store.GetWebhooksAsync(cancellationToken))
                .Where(h => h.Enabled && SubscribesTo(h, eventName))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取 Webhook 配置失败，跳过本次推送。");
            return;
        }

        foreach (var hook in hooks)
        {
            if (!queue.Writer.TryWrite(new WebhookJob(hook, eventName, title, message, detail)))
            {
                logger.LogWarning("Webhook「{Name}」投递队列已满，本条通知被丢弃。", hook.Name);
            }
        }
    }

    /// <summary>后台投递循环：逐条发送（保持群消息顺序），失败按间隔重试。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await DeliverAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机。
        }
    }

    /// <summary>发送一条测试消息（管理端「测试」按钮）：同步返回结果，便于界面直接显示失败原因。</summary>
    public async Task<WebhookTestResult> TestAsync(WebhookRow hook, CancellationToken cancellationToken = default)
    {
        var (ok, status, reason) = await TrySendAsync(hook, "test",
            "集控通知测试",
            "这是一条来自 ClassislandControlHub 的测试消息，收到说明配置正确。",
            null, cancellationToken);

        await RecordAsync(hook.Id, ok, status, ok ? null : reason, cancellationToken);

        return new WebhookTestResult
        {
            Success = ok,
            StatusCode = status,
            Attempts = 1,
            Message = ok
                ? $"发送成功（HTTP {status}）。若群里没看到消息，请确认机器人仍在会话内且未被限流。"
                : reason,
        };
    }

    private async Task DeliverAsync(WebhookJob job, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (ok, status, reason) = await TrySendAsync(job.Hook, job.EventName, job.Title, job.Message,
                job.Detail, cancellationToken);

            if (ok)
            {
                await RecordAsync(job.Hook.Id, true, status, null, cancellationToken);
                return;
            }

            if (attempt >= RetryDelays.Length)
            {
                await RecordAsync(job.Hook.Id, false, status, reason, cancellationToken);
                logger.LogWarning("Webhook「{Name}」投递失败（已尝试 {Attempts} 次）：{Reason}",
                    job.Hook.Name, attempt + 1, reason);
                return;
            }

            logger.LogInformation("Webhook「{Name}」第 {Attempt} 次投递失败（{Reason}），{Delay} 秒后重试。",
                job.Hook.Name, attempt + 1, reason, RetryDelays[attempt].TotalSeconds);
            await Task.Delay(RetryDelays[attempt], cancellationToken);
        }
    }

    /// <summary>发一次：返回「是否成功 / HTTP 状态码 / 失败原因」。</summary>
    private async Task<(bool Ok, int Status, string Reason)> TrySendAsync(WebhookRow hook, string eventName,
        string title, string message, Dictionary<string, object?>? detail, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient("webhook");
            client.Timeout = Timeout;

            var target = hook.Url;

            // 钉钉支持「加签」：把 timestamp + sign 拼到 webhook 地址上。
            if (hook.Kind == WebhookKinds.Dingtalk && !string.IsNullOrWhiteSpace(hook.Secret))
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var sign = DingtalkSign(hook.Secret, timestamp);
                target = hook.Url + (hook.Url.Contains('?') ? "&" : "?")
                         + $"timestamp={timestamp}&sign={Uri.EscapeDataString(sign)}";
            }

            using var response = await client.PostAsync(target,
                new StringContent(BuildPayload(hook.Kind, eventName, title, message, detail), Encoding.UTF8,
                    "application/json"), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return (false, status, $"HTTP {status}：{TrimBody(body)}");
            }

            var businessError = InterpretBusinessError(hook.Kind, body);
            return businessError is null
                ? (true, status, string.Empty)
                : (false, status, businessError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, 0, ex is TaskCanceledException
                ? $"超时：超过 {Timeout.TotalSeconds:0} 秒未响应。"
                : ex.Message);
        }
    }

    private static string BuildPayload(string kind, string eventName, string title, string message,
        Dictionary<string, object?>? detail) => kind switch
    {
        WebhookKinds.Wecom or WebhookKinds.Dingtalk => HubJson.Serialize(new Dictionary<string, object?>
        {
            ["msgtype"] = "text",
            ["text"] = new Dictionary<string, object?> { ["content"] = BuildImText(title, message, detail) },
        }),
        WebhookKinds.Feishu => HubJson.Serialize(new Dictionary<string, object?>
        {
            ["msg_type"] = "text",
            ["content"] = new Dictionary<string, object?> { ["text"] = BuildImText(title, message, detail) },
        }),
        _ => HubJson.Serialize(new Dictionary<string, object?>
        {
            ["event"] = eventName,
            ["title"] = title,
            ["message"] = message,
            ["time"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["detail"] = detail ?? new Dictionary<string, object?>(),
        }),
    };

    private static readonly Dictionary<string, string> DetailLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deviceId"] = "设备 ID",
        ["deviceName"] = "设备",
        ["name"] = "设备",
        ["ip"] = "IP",
        ["lastSeenAt"] = "最后心跳",
        ["offlineMinutes"] = "离线时长(分钟)",
        ["revision"] = "配置版本",
        ["result"] = "结果",
        ["status"] = "状态",
        ["commandId"] = "指令 ID",
        ["kind"] = "指令类型",
    };

    /// <summary>
    /// 群机器人只收纯文本：把标题、正文与设备信息拼成一条消息。
    /// <para>此前企微 / 钉钉 / 飞书只发标题 + 正文，群里看不到是哪台设备、哪个版本，排障时对不上号。</para>
    /// </summary>
    private static string BuildImText(string title, string message, Dictionary<string, object?>? detail)
    {
        var text = new StringBuilder(title).Append('\n').Append(message);
        if (detail is { Count: > 0 })
        {
            foreach (var (key, value) in detail)
            {
                var rendered = value?.ToString();
                if (string.IsNullOrWhiteSpace(rendered))
                {
                    continue;
                }

                text.Append('\n').Append("· ")
                    .Append(DetailLabels.TryGetValue(key, out var label) ? label : key)
                    .Append('：').Append(rendered);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// 解析群机器人在 HTTP 200 里返回的业务错误码（企微 / 钉钉 <c>errcode</c>，飞书 <c>code</c> / <c>StatusCode</c>）。
    /// <para>这是「提示发送成功、群里却没有消息」的根因：机器人被停用、Webhook key 无效、
    /// 被移出群、触发频率限制等情况全都返回 200，只在响应体里报错。</para>
    /// </summary>
    private static string? InterpretBusinessError(string kind, string body)
    {
        if (kind is not (WebhookKinds.Wecom or WebhookKinds.Dingtalk or WebhookKinds.Feishu)
            || string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var code = ReadLong(root, "errcode") ?? ReadLong(root, "code") ?? ReadLong(root, "StatusCode");
            if (code is null or 0)
            {
                return null;
            }

            var reason = ReadString(root, "errmsg") ?? ReadString(root, "msg") ?? ReadString(root, "StatusMessage");
            return $"接收端返回错误码 {code}：{(string.IsNullOrWhiteSpace(reason) ? TrimBody(body) : reason)}";
        }
        catch (JsonException)
        {
            // 不是 JSON（例如自建网关返回一段文本）：无法判定业务错误，交给 HTTP 状态码判断。
            return null;
        }
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var number) ? number : null,
            JsonValueKind.String => long.TryParse(value.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task RecordAsync(string id, bool ok, int status, string? error, CancellationToken cancellationToken)
    {
        try
        {
            await store.RecordWebhookResultAsync(id, ok, status, error, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "记录 Webhook 投递结果失败（不影响通知本身）。");
        }
    }

    /// <summary>钉钉加签：HMAC-SHA256(secret, "{timestamp}\n{secret}") 之后 Base64。</summary>
    private static string DingtalkSign(string secret, long timestamp)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var data = $"{timestamp}\n{secret}";
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }

    private static bool SubscribesTo(WebhookRow hook, string eventName)
        => HubJson.DeserializeOrDefault(hook.Events, new List<string>()).Contains(eventName);

    private static string TrimBody(string text)
        => string.IsNullOrEmpty(text) ? string.Empty : (text.Length > 200 ? text[..200] + "…" : text);

    /// <summary>队列里的一条投递任务。</summary>
    private sealed record WebhookJob(WebhookRow Hook, string EventName, string Title, string Message,
        Dictionary<string, object?>? Detail);
}
