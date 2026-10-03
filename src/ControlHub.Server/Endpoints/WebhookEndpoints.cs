using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Services;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// Webhook 配置接口：把设备掉线 / 同步失败 / 指令失败推到企业微信、钉钉、飞书或自定义端点。
/// </summary>
public static class WebhookEndpoints
{
    /// <summary>注册 Webhook 路由。</summary>
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/webhooks", ListAsync).RequirePermission(PermissionKeys.SettingsRead);
        group.MapPost("/webhooks", CreateAsync).RequirePermission(PermissionKeys.SettingsWrite);
        group.MapPut("/webhooks/{id}", UpdateAsync).RequirePermission(PermissionKeys.SettingsWrite);
        group.MapDelete("/webhooks/{id}", DeleteAsync).RequirePermission(PermissionKeys.SettingsWrite);
        group.MapPost("/webhooks/{id}/test", TestAsync).RequirePermission(PermissionKeys.SettingsWrite);
    }

    private static WebhookDto ToDto(WebhookRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Url = row.Url,
        Kind = row.Kind,
        // 密钥不回传：只告诉前端「已设置」，避免只读账号拿到群机器人密钥。
        Secret = string.Empty,
        HasSecret = !string.IsNullOrEmpty(row.Secret),
        Events = HubJson.DeserializeOrDefault(row.Events, new List<string>())
            .Where(WebhookEvents.All.Contains)
            .ToList(),
        Enabled = row.Enabled,
        CreatedAt = row.CreatedAt,
        LastAttemptAt = row.LastAttemptAt,
        LastSuccessAt = row.LastSuccessAt,
        LastStatusCode = row.LastStatusCode,
        LastError = row.LastError,
        FailCount = row.FailCount,
    };

    /// <summary>Webhook 列表。</summary>
    private static async Task<ApiResult<List<WebhookDto>>> ListAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var rows = await store.GetWebhooksAsync(cancellationToken);
        return ApiResult<List<WebhookDto>>.Success(rows.Select(ToDto).ToList());
    }

    /// <summary>新建 Webhook。</summary>
    private static async Task<ApiResult<WebhookDto>> CreateAsync(
        WebhookUpsertRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = new WebhookRow { Id = HubChecksum.NewId(), CreatedAt = DateTimeOffset.UtcNow };
        Apply(request, row);

        await store.CreateWebhookAsync(row, cancellationToken);
        await store.AddAuditAsync(session.Username, "webhook.create", row.Name,
            $"新建 Webhook（{row.Kind}，订阅 {ToDto(row).Events.Count} 个事件）。",
            http.GetClientIpAddress(), cancellationToken);
        return ApiResult<WebhookDto>.Success(ToDto(row));
    }

    /// <summary>更新 Webhook。</summary>
    private static async Task<ApiResult<WebhookDto>> UpdateAsync(
        string id,
        WebhookUpsertRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await store.GetWebhookAsync(id, cancellationToken)
                  ?? throw HubException.NotFound("Webhook 不存在。");

        Apply(request, row);
        if (!await store.UpdateWebhookAsync(row, cancellationToken))
        {
            throw HubException.NotFound("Webhook 不存在。");
        }

        await store.AddAuditAsync(session.Username, "webhook.update", row.Name,
            "更新 Webhook 配置。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<WebhookDto>.Success(ToDto(row));
    }

    /// <summary>删除 Webhook。</summary>
    private static async Task<ApiResult<bool>> DeleteAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await store.GetWebhookAsync(id, cancellationToken)
                  ?? throw HubException.NotFound("Webhook 不存在。");

        await store.DeleteWebhookAsync(id, cancellationToken);
        await store.AddAuditAsync(session.Username, "webhook.delete", row.Name,
            "删除 Webhook。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    /// <summary>发送一条测试消息，验证地址与报文格式是否正确。</summary>
    private static async Task<ApiResult<WebhookTestResult>> TestAsync(
        string id,
        HttpContext http,
        HubStore store,
        WebhookService webhooks,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await store.GetWebhookAsync(id, cancellationToken)
                  ?? throw HubException.NotFound("Webhook 不存在。");

        var result = await webhooks.TestAsync(row, cancellationToken);
        await store.AddAuditAsync(session.Username, "webhook.test", row.Name,
            result.Success ? "测试发送成功。" : $"测试发送失败：{result.Message}",
            http.GetClientIpAddress(), cancellationToken);
        return ApiResult<WebhookTestResult>.Success(result);
    }

    /// <summary>把请求内容写入行对象（新建与更新共用同一套校验）。</summary>
    private static void Apply(WebhookUpsertRequest request, WebhookRow row)
    {
        var url = (request.Url ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw HubException.Validation("请填写合法的 http(s) 接收地址。");
        }

        var kind = (request.Kind ?? WebhookKinds.Generic).Trim().ToLowerInvariant();
        if (kind is not (WebhookKinds.Wecom or WebhookKinds.Dingtalk or WebhookKinds.Feishu or WebhookKinds.Generic))
        {
            kind = WebhookKinds.Generic;
        }

        var events = (request.Events ?? [])
            .Where(e => WebhookEvents.All.Contains(e))
            .Distinct()
            .ToList();
        if (events.Count == 0)
        {
            throw HubException.Validation("请至少选择一个要推送的事件。");
        }

        row.Name = string.IsNullOrWhiteSpace(request.Name) ? "未命名 Webhook" : request.Name.Trim();
        row.Url = url;
        row.Kind = kind;

        // 密钥只在明确传入时覆盖：前端读不到密钥，编辑时留空表示「保持原值」，
        // 否则每次改个名字都会把已配好的加签密钥清掉。
        if (request.Secret is not null)
        {
            row.Secret = request.Secret.Trim();
        }

        row.Events = HubJson.Serialize(events);
        row.Enabled = request.Enabled;
    }
}
