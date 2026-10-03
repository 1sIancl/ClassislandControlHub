using System.Diagnostics;
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
/// Webhook 推送：把关键事件（设备掉线 / 恢复 / 同步失败 / 指令失败 / 登录失败）发到企业微信、钉钉、飞书或自定义端点。
/// <para>设计约束，都是实际踩过的坑：</para>
/// <para>① <b>不阻塞主流程</b>：调用方只往队列里投一条，投递与重试都在后台进行，
/// 慢的或挂起的接收端不会拖慢设备心跳与指令回报。</para>
/// <para>② <b>不能只看 HTTP 状态码</b>：企微 / 钉钉 / 飞书「没发出去」时同样返回 200，
/// 业务失败只体现在响应体的 errcode（机器人被停用、key 无效、被移出群、触发频率限制）。</para>
/// <para>③ <b>失败与跳过都要看得见</b>：每次投递（含成功、失败、静默跳过、重复抑制）都落一条明细，
/// 管理端可直接翻「到底发出去了没有、对方回了什么、耗时多久」。</para>
/// <para>④ <b>别刷屏</b>：同一事件在短时间内重复发生会被抑制；静默时段整段不推（半夜不吵人）。</para>
/// </summary>
public sealed class WebhookService(
    HubStore store,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookService> logger) : BackgroundService
{
    /// <summary>默认单次请求超时（可在每条 Webhook 上单独设置）。</summary>
    public const int DefaultTimeoutSeconds = 8;

    /// <summary>重试退避间隔；实际用几条由该 Webhook 的重试次数决定。</summary>
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    /// <summary>同一事件在这么短时间内重复发生只推第一条（避免故障风暴把群刷爆）。</summary>
    private static readonly TimeSpan SuppressWindow = TimeSpan.FromMinutes(3);

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

    /// <summary>
    /// 发送一条测试消息（管理端「测试」按钮）：同步返回结果，便于界面直接显示失败原因。
    /// <para>测试**不受静默时段与重复抑制影响**——否则管理员会以为配置坏了。</para>
    /// </summary>
    public async Task<WebhookTestResult> TestAsync(WebhookRow hook, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var (ok, status, reason, response) = await TrySendAsync(hook, "test",
            "集控通知测试",
            "这是一条来自 ClassislandControlHub 的测试消息，收到说明配置正确。",
            null, cancellationToken);
        started.Stop();

        await RecordAsync(hook.Id, ok, status, ok ? null : reason, cancellationToken);
        await AddDeliveryAsync(hook.Id, "test", "集控通知测试", ok, false, status, 1,
            (int)started.ElapsedMilliseconds, ok ? null : reason, response, cancellationToken);

        return new WebhookTestResult
        {
            Success = ok,
            StatusCode = status,
            Attempts = 1,
            Message = ok
                ? $"发送成功（HTTP {status}，耗时 {started.ElapsedMilliseconds} ms）。若群里没看到消息，请确认机器人仍在会话内且未被限流。"
                : reason,
        };
    }

    private async Task DeliverAsync(WebhookJob job, CancellationToken cancellationToken)
    {
        // 静默时段：整段不推（跨夜写法也支持）。跳过也留一条明细，避免「以为坏了」。
        if (InQuietHours(job.Hook.QuietHours, DateTimeOffset.Now))
        {
            var reason = $"静默时段（{job.Hook.QuietHours}）内不推送。";
            await AddDeliveryAsync(job.Hook.Id, job.EventName, job.Title, false, true, 0, 0, 0,
                reason, null, cancellationToken);
            logger.LogInformation("Webhook「{Name}」在静默时段内，跳过 {Event}。", job.Hook.Name, job.EventName);
            return;
        }

        // 重复抑制：同一事件短时间内反复发生（例如教室反复断电）只推第一条。
        try
        {
            // 按「事件 + 标题」抑制：标题里带设备名/账号，不同教室出问题不会被互相吞掉。
            var last = await store.GetLastSuccessDeliveryAsync(job.Hook.Id, job.EventName, job.Title,
                cancellationToken);
            if (last is not null && DateTimeOffset.UtcNow - last.Value < SuppressWindow)
            {
                var reason = $"{SuppressWindow.TotalMinutes:0} 分钟内已推送过同类事件，本次不再重复推送。";
                await AddDeliveryAsync(job.Hook.Id, job.EventName, job.Title, false, true, 0, 0, 0,
                    reason, null, cancellationToken);
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询 Webhook 重复抑制状态失败，按正常推送处理。");
        }

        var retries = Math.Clamp(job.Hook.MaxRetries, 0, RetryDelays.Length);
        var started = Stopwatch.StartNew();

        for (var attempt = 1; ; attempt++)
        {
            var (ok, status, reason, response) = await TrySendAsync(job.Hook, job.EventName, job.Title,
                job.Message, job.Detail, cancellationToken);

            if (ok)
            {
                started.Stop();
                await RecordAsync(job.Hook.Id, true, status, null, cancellationToken);
                await AddDeliveryAsync(job.Hook.Id, job.EventName, job.Title, true, false, status, attempt,
                    (int)started.ElapsedMilliseconds, null, response, cancellationToken);
                return;
            }

            if (attempt > retries)
            {
                started.Stop();
                await RecordAsync(job.Hook.Id, false, status, reason, cancellationToken);
                await AddDeliveryAsync(job.Hook.Id, job.EventName, job.Title, false, false, status, attempt,
                    (int)started.ElapsedMilliseconds, reason, response, cancellationToken);
                logger.LogWarning("Webhook「{Name}」投递失败（已尝试 {Attempts} 次）：{Reason}",
                    job.Hook.Name, attempt, reason);
                return;
            }

            logger.LogInformation("Webhook「{Name}」第 {Attempt} 次投递失败（{Reason}），{Delay} 秒后重试。",
                job.Hook.Name, attempt, reason, RetryDelays[attempt - 1].TotalSeconds);
            await Task.Delay(RetryDelays[attempt - 1], cancellationToken);
        }
    }

    /// <summary>发一次：返回「是否成功 / HTTP 状态码 / 失败原因 / 响应片段」。</summary>
    private async Task<(bool Ok, int Status, string Reason, string Response)> TrySendAsync(WebhookRow hook,
        string eventName, string title, string message, Dictionary<string, object?>? detail,
        CancellationToken cancellationToken)
    {
        // 加签密钥解密失败（密钥文件被更换 / 丢失，见 #36）：直接报错。
        // 不能按「未配置密钥」继续发送——那样钉钉会因缺少签名返回一个与密钥无关的错误，
        // 管理员按提示排查半天也找不到根因。
        if (hook.SecretUnavailable)
        {
            return (false, 0, "加签密钥无法解密：服务端加密密钥已更换或丢失，请在管理端重新填写密钥。",
                string.Empty);
        }

        try
        {
            var client = httpClientFactory.CreateClient("webhook");
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(hook.TimeoutSeconds <= 0 ? DefaultTimeoutSeconds
                : hook.TimeoutSeconds, 1, 60));

            var target = hook.Url;

            // 钉钉支持「加签」：把 timestamp + sign 拼到 webhook 地址上。
            if (hook.Kind == WebhookKinds.Dingtalk && !string.IsNullOrWhiteSpace(hook.Secret))
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var sign = DingtalkSign(hook.Secret, timestamp);
                target = hook.Url + (hook.Url.Contains('?') ? "&" : "?")
                         + $"timestamp={timestamp}&sign={Uri.EscapeDataString(sign)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = new StringContent(BuildPayload(hook.Kind, eventName, title, message, detail,
                    hook.MentionAll), Encoding.UTF8, "application/json"),
            };

            // 自定义请求头：对接需要鉴权（Token / 签名）的自建端点。
            foreach (var (name, value) in ParseHeaders(hook.Headers))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return (false, status, $"HTTP {status}：{TrimBody(body)}", TrimBody(body));
            }

            var businessError = InterpretBusinessError(hook.Kind, body);
            return businessError is null
                ? (true, status, string.Empty, TrimBody(body))
                : (false, status, businessError, TrimBody(body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, 0, DescribeError(ex, hook.TimeoutSeconds), string.Empty);
        }
    }

    /// <summary>
    /// 把底层异常翻译成管理员能看懂的话。
    /// <para>直接甩 <c>An error occurred while sending the request.</c> 这种原文，等于什么都没说——
    /// 现场需要的是「超时了 / 连不上 / 域名解析不了」。</para>
    /// </summary>
    private static string DescribeError(Exception ex, int timeoutSeconds)
    {
        if (ex is TaskCanceledException or TimeoutException
            || ex.InnerException is TaskCanceledException or TimeoutException)
        {
            return $"超时：超过 {timeoutSeconds} 秒未响应。";
        }

        if (ex is System.Net.Sockets.SocketException socket)
        {
            return $"无法连接接收端：{socket.Message}";
        }

        if (ex is HttpRequestException http)
        {
            var inner = http.InnerException;
            return inner switch
            {
                System.Net.Sockets.SocketException innerSocket => $"无法连接接收端：{innerSocket.Message}",
                not null => $"请求失败：{inner.Message}",
                _ => $"请求失败：{http.Message}",
            };
        }

        return $"请求失败：{ex.Message}";
    }

    private static string BuildPayload(string kind, string eventName, string title, string message,
        Dictionary<string, object?>? detail, bool mentionAll) => kind switch
    {
        // 企微：@所有人写在 text.mentioned_list 里。
        WebhookKinds.Wecom => HubJson.Serialize(new Dictionary<string, object?>
        {
            ["msgtype"] = "text",
            ["text"] = new Dictionary<string, object?>
            {
                ["content"] = BuildImText(title, message, detail),
                ["mentioned_list"] = mentionAll ? new[] { "@all" } : Array.Empty<string>(),
            },
        }),
        // 钉钉：@所有人是顶层的 at.isAtAll。
        WebhookKinds.Dingtalk => HubJson.Serialize(new Dictionary<string, object?>
        {
            ["msgtype"] = "text",
            ["text"] = new Dictionary<string, object?> { ["content"] = BuildImText(title, message, detail) },
            ["at"] = new Dictionary<string, object?> { ["isAtAll"] = mentionAll },
        }),
        // 飞书：@所有人需要在文本里内联 <at> 标签。
        WebhookKinds.Feishu => HubJson.Serialize(new Dictionary<string, object?>
        {
            ["msg_type"] = "text",
            ["content"] = new Dictionary<string, object?>
            {
                ["text"] = (mentionAll ? "<at user_id=\"all\">所有人</at>\n" : string.Empty)
                           + BuildImText(title, message, detail),
            },
        }),
        _ => HubJson.Serialize(new Dictionary<string, object?>
        {
            ["event"] = eventName,
            ["title"] = title,
            ["message"] = message,
            ["mentionAll"] = mentionAll,
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
        ["username"] = "账号",
        ["attempts"] = "失败次数",
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

    /// <summary>解析自定义请求头：每行 <c>名称: 值</c>（也接受 <c>名称=值</c>），# 开头为注释。</summary>
    private static List<(string Name, string Value)> ParseHeaders(string? text)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                separator = line.IndexOf('=');
            }

            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Length > 0)
            {
                result.Add((name, value));
            }
        }

        return result;
    }

    /// <summary>
    /// 判断当前是否处于静默时段（如 <c>22:00-07:00</c>，支持跨夜）。
    /// <para>格式不合法时视为「不静默」——宁可多推一条，也不要静默掉真正的告警。</para>
    /// </summary>
    private static bool InQuietHours(string? quietHours, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(quietHours))
        {
            return false;
        }

        var parts = quietHours.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !TimeOnly.TryParse(parts[0], out var start)
            || !TimeOnly.TryParse(parts[1], out var end))
        {
            return false;
        }

        var current = TimeOnly.FromDateTime(now.LocalDateTime);
        return start <= end
            ? current >= start && current < end
            : current >= start || current < end;
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

    private async Task AddDeliveryAsync(string webhookId, string eventName, string title, bool ok, bool skipped,
        int status, int attempts, int durationMs, string? error, string? response,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.AddWebhookDeliveryAsync(new WebhookDeliveryRow
            {
                Id = HubChecksum.NewId(),
                WebhookId = webhookId,
                Event = eventName,
                Title = title,
                Success = ok,
                Skipped = skipped,
                StatusCode = status,
                Attempts = Math.Max(1, attempts),
                DurationMs = durationMs,
                Error = error,
                Response = response,
                CreatedAt = DateTimeOffset.UtcNow,
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "写入 Webhook 投递明细失败（不影响通知本身）。");
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
