namespace ControlHub.Protocol.Dtos;

/// <summary>Webhook 事件类型。</summary>
public static class WebhookEvents
{
    /// <summary>设备掉线（上一次心跳超过在线超时阈值）。</summary>
    public const string DeviceOffline = "device.offline";

    /// <summary>设备同步失败或上报异常。</summary>
    public const string DeviceError = "device.error";

    /// <summary>远程指令执行失败。</summary>
    public const string CommandFailed = "command.failed";

    /// <summary>全部事件（前端渲染勾选框用）。</summary>
    public static readonly string[] All = [DeviceOffline, DeviceError, CommandFailed];
}

/// <summary>Webhook 接收端类型：决定推送的报文格式。</summary>
public static class WebhookKinds
{
    /// <summary>企业微信群机器人。</summary>
    public const string Wecom = "wecom";

    /// <summary>钉钉群机器人（支持加签密钥）。</summary>
    public const string Dingtalk = "dingtalk";

    /// <summary>飞书群机器人。</summary>
    public const string Feishu = "feishu";

    /// <summary>自定义端点：收到 { event, title, message, time, detail } 结构的 JSON。</summary>
    public const string Generic = "generic";
}

/// <summary>Webhook 配置。</summary>
public sealed class WebhookDto
{
    /// <summary>配置 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>名称，便于在管理端识别（例如「高一教师群」）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>接收地址。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>接收端类型，取值见 <see cref="WebhookKinds"/>。</summary>
    public string Kind { get; set; } = WebhookKinds.Generic;

    /// <summary>加签密钥（钉钉需要时填写，其它类型留空）。</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>订阅的事件类型集合。</summary>
    public List<string> Events { get; set; } = [];

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>新建或更新 Webhook。</summary>
public sealed class WebhookUpsertRequest
{
    /// <summary>名称。</summary>
    public string? Name { get; set; }

    /// <summary>接收地址。</summary>
    public string? Url { get; set; }

    /// <summary>接收端类型。</summary>
    public string? Kind { get; set; }

    /// <summary>加签密钥（可选）。</summary>
    public string? Secret { get; set; }

    /// <summary>订阅的事件类型集合。</summary>
    public List<string>? Events { get; set; }

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Webhook 测试发送的结果。</summary>
public sealed class WebhookTestResult
{
    /// <summary>是否发送成功。</summary>
    public bool Success { get; set; }

    /// <summary>HTTP 状态码（网络错误时为 0）。</summary>
    public int StatusCode { get; set; }

    /// <summary>说明文字（失败原因或接收端返回片段）。</summary>
    public string Message { get; set; } = string.Empty;
}
