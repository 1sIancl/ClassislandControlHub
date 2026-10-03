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

    /// <summary>设备从离线恢复上线（与掉线配对：出问题要通知，恢复了同样要知道）。</summary>
    public const string DeviceOnline = "device.online";

    /// <summary>全部事件（前端渲染勾选框用）。</summary>
    public static readonly string[] All = [DeviceOffline, DeviceOnline, DeviceError, CommandFailed];
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

    /// <summary>
    /// 加签密钥（钉钉需要时填写，其它类型留空）。
    /// <para>只在写入时使用；**读取时固定为空**——密钥不回传给前端（任何只有只读权限的账号
    /// 都不该拿到群机器人密钥），用 <see cref="HasSecret"/> 表示「已设置」。</para>
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>是否已设置加签密钥（读取时用它判断，而不是把密钥本身发出去）。</summary>
    public bool HasSecret { get; set; }

    /// <summary>最近一次投递尝试时间（含失败；从未投递过时为空）。</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>最近一次投递成功时间（失败时保留旧值，便于看出「多久没成功过了」）。</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>最近一次投递的 HTTP 状态码（0 = 网络错误 / 超时）。</summary>
    public int LastStatusCode { get; set; }

    /// <summary>最近一次投递失败的原因——「为什么没发出去」看这里（成功时为空）。</summary>
    public string? LastError { get; set; }

    /// <summary>连续失败次数（成功一次即清零）。</summary>
    public int FailCount { get; set; }

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

    /// <summary>实际尝试次数（测试发送为 1；真实推送失败会重试，次数记在日志里）。</summary>
    public int Attempts { get; set; } = 1;

    /// <summary>说明文字：成功说明，或失败原因（含接收端返回的业务错误码）。</summary>
    public string Message { get; set; } = string.Empty;
}
