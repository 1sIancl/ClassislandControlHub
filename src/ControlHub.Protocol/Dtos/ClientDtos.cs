namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 设备注册请求。首次接入集控服务器时调用。
/// </summary>
public sealed class EnrollRequest
{
    /// <summary>管理员在 Web 端生成的注册码。</summary>
    public string EnrollCode { get; set; } = string.Empty;

    /// <summary>
    /// 机器指纹（客户端自行生成并持久化的 GUID）。
    /// 服务端以此识别「同一台设备重新注册」，避免重复建档。
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>展示用设备名，例如「高一(3)班」。留空时服务端会使用机器名。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>操作系统机器名。</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>操作系统描述。</summary>
    public string OsVersion { get; set; } = string.Empty;

    /// <summary>ClassIsland 版本号。</summary>
    public string ClassIslandVersion { get; set; } = string.Empty;

    /// <summary>集控插件版本号。</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>客户端支持的同步分区，取值见 <see cref="HubProtocol.Sections"/>。</summary>
    public List<string> Capabilities { get; set; } = [];
}

/// <summary>
/// 设备注册应答。
/// </summary>
public sealed class EnrollResponse
{
    /// <summary>服务端分配的稳定设备 ID。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备长期访问令牌。客户端应持久化并妥善保管。</summary>
    public string DeviceToken { get; set; } = string.Empty;

    /// <summary>服务端最终采用的设备显示名。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>设备所属分组 ID。</summary>
    public string? GroupId { get; set; }

    /// <summary>设备所属分组名称。</summary>
    public string? GroupName { get; set; }

    /// <summary>当前应生效的配置版本号。客户端可据此判断是否需要立即同步。</summary>
    public long Revision { get; set; }

    /// <summary>建议的心跳间隔（秒）。</summary>
    public int HeartbeatSeconds { get; set; } = HubProtocol.DefaultHeartbeatSeconds;

    /// <summary>服务器名称，用于客户端界面展示。</summary>
    public string ServerName { get; set; } = string.Empty;
}

/// <summary>
/// 心跳上报。客户端按服务器指定的间隔周期调用。
/// </summary>
public sealed class HeartbeatRequest
{
    /// <summary>当前运行状态，取值见 <see cref="DeviceStates"/>。</summary>
    public string State { get; set; } = DeviceStates.Idle;

    /// <summary>客户端已成功应用的配置版本号。</summary>
    public long AppliedRevision { get; set; }

    /// <summary>
    /// 客户端已应用的推送世代号。用于支持「定向推送」：
    /// 管理员只对部分设备发起推送时，全局配置版本号不变，仅这些设备的世代号递增。
    /// </summary>
    public long AppliedPushEpoch { get; set; }

    /// <summary>客户端进程已运行秒数。</summary>
    public long UptimeSeconds { get; set; }

    /// <summary>最近一次成功同步时间（UTC）。</summary>
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>当前显示的课表名称，用于 Web 端直观展示。</summary>
    public string? CurrentClassPlanName { get; set; }

    /// <summary>当前或即将开始的课程名称。</summary>
    public string? CurrentSubjectName { get; set; }

    /// <summary>客户端最近一条告警信息，可为空。</summary>
    public string? LastError { get; set; }

    /// <summary>附加运行指标，键值均为字符串以便扩展。</summary>
    public Dictionary<string, string> Metrics { get; set; } = [];
}

/// <summary>
/// 心跳应答。
/// </summary>
public sealed class HeartbeatResponse
{
    /// <summary>服务器当前配置版本号。</summary>
    public long Revision { get; set; }

    /// <summary>服务器为当前设备维护的推送世代号。</summary>
    public long PushEpoch { get; set; }

    /// <summary>是否需要客户端立即执行一次同步。</summary>
    public bool ShouldSync { get; set; }

    /// <summary>下一次心跳的建议间隔（秒）。</summary>
    public int HeartbeatSeconds { get; set; } = HubProtocol.DefaultHeartbeatSeconds;

    /// <summary>服务端时间，客户端可用于校正时钟偏移。</summary>
    public DateTimeOffset ServerTime { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>管理员下发的即时消息，客户端可在界面上提示，可为空。</summary>
    public string? Message { get; set; }

    /// <summary>管理员是否已吊销本设备。为 <c>true</c> 时客户端应停止同步并清除凭证。</summary>
    public bool Revoked { get; set; }
}

/// <summary>
/// 配置同步应答。
/// </summary>
public sealed class SyncResponse
{
    /// <summary>配置版本号。</summary>
    public long Revision { get; set; }

    /// <summary>本次响应附带的推送世代号。客户端应用成功后应回传该值。</summary>
    public long PushEpoch { get; set; }

    /// <summary>配置档案 ID。</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>配置档案名称。</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>服务端生成该版本的时间（UTC）。</summary>
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>内容包。仅包含客户端声明支持的分区。</summary>
    public ContentBundleDto Content { get; set; } = new();

    /// <summary>内容校验和，格式 <c>sha256:&lt;hex&gt;</c>，客户端可用于完整性校验。</summary>
    public string Checksum { get; set; } = string.Empty;

    /// <summary>本次下发是否为强制覆盖（<c>true</c> 时客户端不应跳过任何分区）。</summary>
    public bool Force { get; set; }
}

/// <summary>
/// 长轮询应答。用于让服务端在配置变更或定向推送时立即唤醒客户端。
/// </summary>
public sealed class WaitResponse
{
    /// <summary>配置（或本设备的推送世代）是否已发生变更。</summary>
    public bool Changed { get; set; }

    /// <summary>服务器当前配置版本号。</summary>
    public long Revision { get; set; }

    /// <summary>当前设备的推送世代号。</summary>
    public long PushEpoch { get; set; }

    /// <summary>是否收到服务端的即时指令。</summary>
    public string? Command { get; set; }
}

/// <summary>
/// 配置应用结果上报。
/// </summary>
public sealed class ApplyReportRequest
{
    /// <summary>所应用的配置版本号。</summary>
    public long Revision { get; set; }

    /// <summary>所应用的推送世代号。</summary>
    public long PushEpoch { get; set; }

    /// <summary>应用结果，取值见 <see cref="ApplyResults"/>。</summary>
    public string Result { get; set; } = ApplyResults.Success;

    /// <summary>实际应用成功的分区。</summary>
    public List<string> AppliedSections { get; set; } = [];

    /// <summary>结果说明，失败时给出原因。</summary>
    public string? Message { get; set; }

    /// <summary>各分区统计明细，例如 <c>timeLayouts=2</c>。</summary>
    public Dictionary<string, string> Details { get; set; } = [];
}

/// <summary>
/// 客户端日志上报。
/// </summary>
public sealed class LogReportRequest
{
    /// <summary>日志条目。</summary>
    public List<LogEntryDto> Entries { get; set; } = [];
}

/// <summary>
/// 单条日志。
/// </summary>
public sealed class LogEntryDto
{
    /// <summary>发生时间（UTC）。</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>级别：<c>debug</c> / <c>info</c> / <c>warn</c> / <c>error</c>。</summary>
    public string Level { get; set; } = "info";

    /// <summary>日志正文。</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 设备在 Web 管理端展示的摘要信息。
/// </summary>
public sealed class DeviceSummaryDto
{
    /// <summary>设备 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>设备显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分组 ID。</summary>
    public string? GroupId { get; set; }

    /// <summary>分组名称。</summary>
    public string? GroupName { get; set; }

    /// <summary>运行状态，取值见 <see cref="DeviceStates"/>。</summary>
    public string State { get; set; } = DeviceStates.Offline;

    /// <summary>是否在线（依据最近心跳时间推断）。</summary>
    public bool Online { get; set; }

    /// <summary>已应用的配置版本号。</summary>
    public long AppliedRevision { get; set; }

    /// <summary>服务端当前配置版本号。</summary>
    public long ServerRevision { get; set; }

    /// <summary>设备已应用的推送世代号。</summary>
    public long AppliedPushEpoch { get; set; }

    /// <summary>服务端为设备维护的推送世代号。</summary>
    public long ServerPushEpoch { get; set; }

    /// <summary>配置是否为最新（版本号与推送世代号均一致）。</summary>
    public bool UpToDate => AppliedRevision >= ServerRevision && AppliedPushEpoch >= ServerPushEpoch;

    /// <summary>最近心跳时间（UTC）。</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>最近同步时间（UTC）。</summary>
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>机器名。</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>操作系统描述。</summary>
    public string OsVersion { get; set; } = string.Empty;

    /// <summary>ClassIsland 版本。</summary>
    public string ClassIslandVersion { get; set; } = string.Empty;

    /// <summary>插件版本。</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>IP 地址。</summary>
    public string? IpAddress { get; set; }

    /// <summary>当前课表名称。</summary>
    public string? CurrentClassPlanName { get; set; }

    /// <summary>最近一条错误。</summary>
    public string? LastError { get; set; }

    /// <summary>是否已被吊销。</summary>
    public bool Revoked { get; set; }

    /// <summary>首次注册时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
