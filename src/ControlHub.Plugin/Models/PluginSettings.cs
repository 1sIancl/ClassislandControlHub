using ControlHub.Protocol;

namespace ControlHub.Plugin.Models;

/// <summary>
/// 插件持久化配置。对应 <c>settings.json</c>，保存在插件配置目录。
/// 除设备令牌等敏感字段外，其余字段都会在设置页面中开放编辑。
/// </summary>
public sealed class PluginSettings
{
    /// <summary>集控服务器地址，例如 <c>http://192.168.1.5:29800</c>。留空表示未配置。</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>设备注册码（由管理员在 Web 端生成）。</summary>
    public string EnrollCode { get; set; } = string.Empty;

    /// <summary>设备显示名称，留空时使用本机机器名。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>稳定的设备标识（首次运行时生成，勿随意修改）。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备访问令牌（注册成功后由服务器下发）。</summary>
    public string DeviceToken { get; set; } = string.Empty;

    /// <summary>是否启用自动同步。</summary>
    public bool AutoSync { get; set; } = true;

    /// <summary>是否应用「时间表」分区。</summary>
    public bool ApplyTimeLayouts { get; set; } = true;

    /// <summary>是否应用「课表」分区。</summary>
    public bool ApplyClassPlans { get; set; } = true;

    /// <summary>是否应用「科目」分区。</summary>
    public bool ApplySubjects { get; set; } = true;

    /// <summary>是否应用「自定义设置」分区。</summary>
    public bool ApplySettings { get; set; } = true;

    /// <summary>服务器要求本地编辑锁定时是否遵守（锁定后本地修改会被远程配置覆盖）。</summary>
    public bool RespectLockLocalEditing { get; set; } = true;

    /// <summary>是否允许连接使用自签名/不受信任证书的 HTTPS 服务器。</summary>
    public bool AllowInsecureTls { get; set; }

    /// <summary>是否启用时间同步（把 ClassIsland 精确时间服务器指向集控服务器）。</summary>
    public bool EnableTimeSync { get; set; } = true;

    /// <summary>心跳/同步周期（秒）。</summary>
    public int SyncIntervalSeconds { get; set; } = HubProtocol.DefaultHeartbeatSeconds;

    /// <summary>客户端已成功应用的内容校验和（用于避免重复应用同一份配置）。</summary>
    public string LastAppliedChecksum { get; set; } = string.Empty;

    /// <summary>已成功应用的配置版本号。</summary>
    public long AppliedRevision { get; set; }

    /// <summary>已成功应用的推送世代号。</summary>
    public long AppliedPushEpoch { get; set; }

    /// <summary>是否已在设置页面完成「首次引导」。</summary>
    public bool Onboarded { get; set; }
}
