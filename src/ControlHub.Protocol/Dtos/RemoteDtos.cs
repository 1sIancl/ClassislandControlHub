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

/// <summary>A 端下发的 ClassIsland 外观配置（组件与字体等）。</summary>
public sealed class AppearanceConfigDto
{
    /// <summary>主题：<c>light</c> / <c>dark</c> / <c>system</c>，为空表示不修改。</summary>
    public string? Theme { get; set; }

    /// <summary>强调色（十六进制，如 <c>#1E90FF</c>），为空表示不修改。</summary>
    public string? AccentColor { get; set; }

    /// <summary>四类字体大小（次级/正文/强调/大号），键为 <c>secondary|body|emphasized|large</c>。</summary>
    public Dictionary<string, double> FontSizes { get; set; } = [];

    /// <summary>字体名称。</summary>
    public string? FontFamily { get; set; }

    /// <summary>组件配置方案（ClassIsland 组件布局 JSON），为空表示不修改。</summary>
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
}
