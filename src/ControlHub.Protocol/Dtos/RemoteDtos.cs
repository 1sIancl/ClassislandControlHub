namespace ControlHub.Protocol.Dtos;

/// <summary>远程指令类型常量。</summary>
public static class RemoteCommandKinds
{
    /// <summary>执行命令行（统一/单台），载荷为 <c>{"command":"...","args":"..."}</c> 或纯命令字符串。</summary>
    public const string Shell = "shell";

    /// <summary>安装插件，载荷为 <c>{"cipxUrl":"...","cipxBase64":"...","fileName":"..."}</c>。</summary>
    public const string PluginInstall = "plugin.install";

    /// <summary>卸载插件，载荷为 <c>{"pluginId":"..."}</c>。</summary>
    public const string PluginUninstall = "plugin.uninstall";

    /// <summary>启用/禁用插件，载荷为 <c>{"pluginId":"...","enabled":true}</c>。</summary>
    public const string PluginToggle = "plugin.toggle";

    /// <summary>刷新并上报插件列表。</summary>
    public const string PluginRefresh = "plugin.refresh";

    /// <summary>应用外观配置，载荷为 <see cref="AppearanceConfigDto"/> 的 JSON。</summary>
    public const string AppearanceApply = "appearance.apply";

    /// <summary>发送提醒，载荷为 <see cref="NotifyRequestDto"/> 的 JSON。</summary>
    public const string Notify = "notify";

    /// <summary>请求 ClassIsland 重启。</summary>
    public const string Restart = "restart";

    /// <summary>关闭计算机（Windows 电源管理，无载荷）。</summary>
    public const string PowerShutdown = "power.shutdown";

    /// <summary>重启计算机，无载荷。</summary>
    public const string PowerRestart = "power.restart";

    /// <summary>让计算机进入睡眠，无载荷。</summary>
    public const string PowerSleep = "power.sleep";

    /// <summary>请求上报可远程触发的自动化信号列表（结果放在命令回报的 Output 中）。</summary>
    public const string AutomationList = "automation.list";

    /// <summary>触发一个自动化信号，载荷为 <c>{"signal":"放学"}</c>。</summary>
    public const string AutomationTrigger = "automation.trigger";

    /// <summary>抓取教室端画面并上传（内容见 <see cref="DiagnosticUploadRequest"/>），回报中给出分辨率与体积。</summary>
    public const string DiagnosticScreenshot = "diagnostic.screenshot";

    /// <summary>采集前台窗口与进程快照（结果放在命令回报的 Output 中）。</summary>
    public const string DiagnosticProcesses = "diagnostic.processes";

    /// <summary>采集诊断数据包：系统环境、应用与插件版本、集控运行状态与最近日志（结果放在 Output 中）。</summary>
    public const string DiagnosticBundle = "diagnostic.bundle";
}

/// <summary>触发自动化信号的载荷。</summary>
public sealed class AutomationTriggerPayload
{
    /// <summary>信号名（与 ClassIsland 自动化里「信号触发器」填写的名称一致）。</summary>
    public string Signal { get; set; } = string.Empty;
}

/// <summary>通知模板：把常用的广播内容存起来，发通知时一键套用。</summary>
public sealed class NoticeTemplateDto
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

/// <summary>新建或更新通知模板。</summary>
public sealed class NoticeTemplateUpsertRequest
{
    /// <summary>模板名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>通知标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>通知正文。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>是否语音播报。</summary>
    public bool Speak { get; set; }
}

/// <summary>远程指令（A 端下发，B 端执行并回报）。</summary>
public sealed class RemoteCommandDto
{
    /// <summary>指令 ID（服务端生成，用于回报去重）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>指令类型，取值见 <see cref="RemoteCommandKinds"/>。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>载荷（JSON 字符串，具体结构由 <see cref="Kind"/> 决定）。</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>下发时间（UTC）。</summary>
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>下发管理员。</summary>
    public string IssuedBy { get; set; } = string.Empty;
}

/// <summary>B 端回报的指令执行结果。</summary>
public sealed class CommandReportRequest
{
    /// <summary>对应的指令 ID。</summary>
    public string CommandId { get; set; } = string.Empty;

    /// <summary>是否执行成功。</summary>
    public bool Success { get; set; }

    /// <summary>标准输出 / 执行详情。</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>进程退出码（非 shell 类指令可留 0）。</summary>
    public int ExitCode { get; set; }

    /// <summary>错误信息（失败时）。</summary>
    public string? Error { get; set; }

    /// <summary>完成时间（UTC）。</summary>
    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>B 端上传的诊断工件（截图等二进制内容，Base64 编码）。</summary>
public sealed class DiagnosticUploadRequest
{
    /// <summary>触发本次采集的指令 ID（可空，用于与命令历史对应）。</summary>
    public string CommandId { get; set; } = string.Empty;

    /// <summary>工件类型，目前为 <c>screenshot</c>。</summary>
    public string Kind { get; set; } = "screenshot";

    /// <summary>说明（分辨率、前台窗口标题等）。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>MIME 类型。</summary>
    public string ContentType { get; set; } = "image/png";

    /// <summary>Base64 编码的内容。</summary>
    public string ContentBase64 { get; set; } = string.Empty;

    /// <summary>采集时间（UTC）。</summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A 端管理侧展示的设备诊断工件（不含内容本体）。</summary>
public sealed class DeviceDiagnosticDto
{
    /// <summary>工件 ID，用于读取内容。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>设备 ID。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名称。</summary>
    public string? DeviceName { get; set; }

    /// <summary>工件类型。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>说明。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>MIME 类型。</summary>
    public string ContentType { get; set; } = "image/png";

    /// <summary>内容体积（字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>采集时间。</summary>
    public DateTimeOffset CapturedAt { get; set; }
}

/// <summary>B 端已安装的 ClassIsland 插件信息。</summary>
public sealed class PluginInfoDto
{
    /// <summary>插件 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>插件名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>插件版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; }

    /// <summary>作者。</summary>
    public string? Author { get; set; }

    /// <summary>说明。</summary>
    public string? Description { get; set; }
}

/// <summary>B 端插件列表上报。</summary>
public sealed class PluginReportRequest
{
    /// <summary>插件列表。</summary>
    public List<PluginInfoDto> Plugins { get; set; } = [];
}

/// <summary>
/// A 端下发的 ClassIsland 外观配置。
/// <para>所有字段都可空：**只有显式给出的项才会被应用**，其余保持教室机现有设置不变。
/// 与「补丁」语义一致，避免统一分发时把个别教室的特殊配置抹平。</para>
/// </summary>
public sealed class AppearanceConfigDto
{
    /// <summary>主题：<c>light</c> / <c>dark</c> / <c>system</c>，为空表示不修改。</summary>
    public string? Theme { get; set; }

    /// <summary>强调色（十六进制，如 <c>#1E90FF</c>），为空表示不修改。</summary>
    public string? AccentColor { get; set; }

    /// <summary>第二色（十六进制），为空表示不修改。</summary>
    public string? SecondaryColor { get; set; }

    /// <summary>四类字体大小（次级/正文/强调/大号），键为 <c>secondary|body|emphasized|large</c>。</summary>
    public Dictionary<string, double> FontSizes { get; set; } = [];

    /// <summary>字体名称（需教室机已安装该字体），为空表示不修改。</summary>
    public string? FontFamily { get; set; }

    /// <summary>主界面圆角（像素，0~64），为空表示不修改。</summary>
    public double? Radius { get; set; }

    /// <summary>主界面背景不透明度（0.1~1），为空表示不修改。</summary>
    public double? Opacity { get; set; }

    /// <summary>界面缩放（0.5~3，如 1.25），为空表示不修改。</summary>
    public double? Scale { get; set; }

    /// <summary>背景材质：<c>off</c> / <c>acrylic</c>（亚克力）/ <c>liquidglass</c> / <c>mica</c>，为空表示不修改。</summary>
    public string? BackgroundMaterial { get; set; }

    /// <summary>是否启用「分体主界面」（各区块分离显示），为空表示不修改。</summary>
    public bool? SeparatedIsland { get; set; }

    /// <summary>自定义前景色（十六进制），为空表示不修改。</summary>
    public string? ForegroundColor { get; set; }

    /// <summary>
    /// 组件配置方案（ClassIsland 组件布局 JSON）。
    /// <para><b>当前版本尚未应用</b>：仅保留字段，收到时会如实回报「组件布局暂未支持」，
    /// 而不是静默忽略。</para>
    /// </summary>
    public string? ComponentProfileJson { get; set; }

    /// <summary>其它自定义键值，按需扩展。</summary>
    public Dictionary<string, string> Values { get; set; } = [];
}

/// <summary>A 端向 B 端发送的提醒。</summary>
public sealed class NotifyRequestDto
{
    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>正文。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>是否语音播报。</summary>
    public bool Speak { get; set; }

    /// <summary>是否播放强调特效（全屏）。</summary>
    public bool Emphasize { get; set; }

    /// <summary>显示时长（秒），0 表示使用默认。</summary>
    public int DurationSeconds { get; set; }
}

/// <summary>A 端管理侧展示的设备指令记录。</summary>
public sealed class DeviceCommandDto
{
    /// <summary>指令 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>设备 ID。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名称。</summary>
    public string? DeviceName { get; set; }

    /// <summary>指令类型。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>载荷。</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>状态：<c>pending</c> / <c>done</c> / <c>failed</c>。</summary>
    public string Status { get; set; } = "pending";

    /// <summary>输出。</summary>
    public string? Output { get; set; }

    /// <summary>退出码。</summary>
    public int ExitCode { get; set; }

    /// <summary>下发时间。</summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>完成时间。</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>下发管理员。</summary>
    public string IssuedBy { get; set; } = string.Empty;

    /// <summary>被设备取走的时刻（为空表示仍在排队）。</summary>
    public DateTimeOffset? DispatchedAt { get; set; }

    /// <summary>最早生效时间（定时指令用；为空表示立即生效）。</summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>过期时间：超过此刻仍未派发则作废，管理端据此显示「剩余时间」。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>A 端备份条目。</summary>
public sealed class BackupEntryDto
{
    /// <summary>备份目录名（唯一标识）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>备份类型：<c>manual</c> / <c>auto</c> / <c>update</c>。</summary>
    public string Type { get; set; } = "manual";

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>大小（字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>备注。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>备份内含的文件数（#59；老备份没有这项，为 0）。</summary>
    public int FileCount { get; set; }

    /// <summary>
    /// 是否包含加密密钥（#59）。界面据此提示「这份备份不含密钥，换机器恢复时注册码会失效」，
    /// 或反过来警告「含密钥，别外发」。
    /// </summary>
    public bool IncludesSecretsKey { get; set; }
}
