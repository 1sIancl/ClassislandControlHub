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

    /// <summary>管理员登录失败（连续失败会触发账号锁定，可用于安全预警）。</summary>
    public const string LoginFailed = "admin.login.failed";

    /// <summary>全部事件（前端渲染勾选框用）。</summary>
    public static readonly string[] All =
        [DeviceOffline, DeviceOnline, DeviceError, CommandFailed, LoginFailed];
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

    /// <summary>
    /// 已设置的加签密钥**无法解密**（服务端密钥文件被更换 / 丢失，见 #36）。
    /// 此时投递会直接失败，需要在管理端重新填写密钥。
    /// </summary>
    public bool SecretUnavailable { get; set; }

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

    /// <summary>推送时是否 @所有人（企微 / 钉钉 / 飞书群机器人支持；紧急通知才用）。</summary>
    public bool MentionAll { get; set; }

    /// <summary>自定义请求头，每行一条 <c>名称: 值</c>。对接需要鉴权头的自建端点时使用。</summary>
    public string Headers { get; set; } = string.Empty;

    /// <summary>静默时段，如 <c>22:00-07:00</c>（支持跨夜）。该区间内不推送，避免半夜刷屏；留空表示不静默。</summary>
    public string QuietHours { get; set; } = string.Empty;

    /// <summary>单次请求超时秒数（1~60）。</summary>
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>失败重试次数（0~5）。</summary>
    public int MaxRetries { get; set; } = 2;

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

    /// <summary>推送时是否 @所有人。</summary>
    public bool? MentionAll { get; set; }

    /// <summary>自定义请求头（每行 <c>名称: 值</c>）；传空字符串表示清空，传 null 表示不修改。</summary>
    public string? Headers { get; set; }

    /// <summary>静默时段（如 <c>22:00-07:00</c>）；传空字符串表示清空，传 null 表示不修改。</summary>
    public string? QuietHours { get; set; }

    /// <summary>单次请求超时秒数（1~60）；null 表示不修改。</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>失败重试次数（0~5）；null 表示不修改。</summary>
    public int? MaxRetries { get; set; }
}

/// <summary>一条 Webhook 投递明细，用于回答「这条通知到底发出去了没有、对方回了什么、耗时多久」。</summary>
public sealed class WebhookDeliveryDto
{
    /// <summary>记录 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>事件名（如 <c>device.offline</c>；测试消息为 <c>test</c>）。</summary>
    public string Event { get; set; } = string.Empty;

    /// <summary>消息标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>是否投递成功。</summary>
    public bool Success { get; set; }

    /// <summary>是否因静默时段被跳过（跳过不算失败）。</summary>
    public bool Skipped { get; set; }

    /// <summary>HTTP 状态码（0 = 网络错误 / 超时 / 未投递）。</summary>
    public int StatusCode { get; set; }

    /// <summary>实际尝试次数。</summary>
    public int Attempts { get; set; }

    /// <summary>总耗时（毫秒，含重试）。</summary>
    public int DurationMs { get; set; }

    /// <summary>失败原因（成功或跳过时为空）。</summary>
    public string? Error { get; set; }

    /// <summary>接收端响应片段（截断保存，便于对接调试）。</summary>
    public string? Response { get; set; }

    /// <summary>发生时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
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
