using System.Security.Cryptography;
using System.Text;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;

namespace ControlHub.Server.Services;

/// <summary>
/// Webhook 推送：把关键事件（设备掉线 / 同步失败 / 指令失败）发到企业微信、钉钉、飞书或自定义端点。
/// <para>
/// 推送是「尽力而为」：失败只记日志，绝不影响主流程——心跳、指令回报这些都不该因为
/// 外部通知没发出去而失败。
/// </para>
/// </summary>
public sealed class WebhookService(
    HubStore store,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookService> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    /// <summary>按事件类型推送：只会发给「已启用且订阅了该事件」的配置项。</summary>
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
            try
            {
                var (ok, status, body) = await SendAsync(hook.Url, hook.Kind, hook.Secret, title, message,
                    eventName, detail, cancellationToken);
                if (!ok)
                {
                    logger.LogWarning("Webhook「{Name}」推送失败：HTTP {Status} {Body}", hook.Name, status, body);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Webhook「{Name}」推送异常。", hook.Name);
            }
        }
    }

    /// <summary>发送一条测试消息（管理端「测试」按钮）。</summary>
    public async Task<WebhookTestResult> TestAsync(WebhookRow hook, CancellationToken cancellationToken = default)
    {
        try
        {
            var (ok, status, body) = await SendAsync(hook.Url, hook.Kind, hook.Secret,
                "集控通知测试",
                "这是一条来自 ClassislandControlHub 的测试消息，收到说明配置正确。",
                "test", null, cancellationToken);

            return new WebhookTestResult
            {
                Success = ok,
                StatusCode = status,
                Message = ok ? "发送成功。" : $"HTTP {(status == 0 ? "网络错误" : status.ToString())}：{Trim(body)}",
            };
        }
        catch (Exception ex)
        {
            return new WebhookTestResult { Success = false, StatusCode = 0, Message = "发送失败：" + ex.Message };
        }
    }

    private async Task<(bool Ok, int Status, string Body)> SendAsync(string url, string kind, string secret,
        string title, string message, string eventName, Dictionary<string, object?>? detail,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("webhook");
        client.Timeout = Timeout;

        var target = url;
        var content = kind switch
        {
            WebhookKinds.Wecom or WebhookKinds.Dingtalk => HubJson.Serialize(new Dictionary<string, object?>
            {
                ["msgtype"] = "text",
                ["text"] = new Dictionary<string, object?> { ["content"] = $"{title}\n{message}" },
            }),
            WebhookKinds.Feishu => HubJson.Serialize(new Dictionary<string, object?>
            {
                ["msg_type"] = "text",
                ["content"] = new Dictionary<string, object?> { ["text"] = $"{title}\n{message}" },
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

        // 钉钉支持「加签」：把 timestamp + sign 拼到 webhook 地址上。
        if (kind == WebhookKinds.Dingtalk && !string.IsNullOrWhiteSpace(secret))
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sign = DingtalkSign(secret, timestamp);
            target = url + (url.Contains('?') ? "&" : "?")
                     + $"timestamp={timestamp}&sign={Uri.EscapeDataString(sign)}";
        }

        using var response = await client.PostAsync(target,
            new StringContent(content, Encoding.UTF8, "application/json"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response.IsSuccessStatusCode, (int)response.StatusCode, Trim(body));
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

    private static string Trim(string text)
        => string.IsNullOrEmpty(text) ? string.Empty : (text.Length > 200 ? text[..200] + "…" : text);
}
