namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 通用分页结果。
/// </summary>
/// <typeparam name="T">条目类型。</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>当前页数据。</summary>
    public List<T> Items { get; set; } = [];

    /// <summary>总条数。</summary>
    public int Total { get; set; }

    /// <summary>当前页码，从 1 开始。</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数。</summary>
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// 管理员登录请求。
/// </summary>
public sealed class LoginRequest
{
    /// <summary>用户名。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>密码。</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// 管理员登录应答。
/// </summary>
public sealed class LoginResponse
{
    /// <summary>会话令牌。后续请求置于 <c>Authorization: Bearer &lt;token&gt;</c>。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>令牌过期时间（UTC）。</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>显示的账号名。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>角色：<c>admin</c> 或 <c>viewer</c>。</summary>
    public string Role { get; set; } = "admin";
}

/// <summary>
/// 修改密码请求。
/// </summary>
public sealed class ChangePasswordRequest
{
    /// <summary>原密码。</summary>
    public string CurrentPassword { get; set; } = string.Empty;

    /// <summary>新密码。</summary>
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// 服务器基础信息，供 Web 端与客户端展示。
/// </summary>
public sealed class ServerInfoDto
{
    /// <summary>服务器名称。</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>集控系统版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>通信协议版本。</summary>
    public string ProtocolVersion { get; set; } = HubProtocol.Version;

    /// <summary>当前配置版本号。</summary>
    public long Revision { get; set; }

    /// <summary>是否要求注册码。</summary>
    public bool RequiresEnrollCode { get; set; } = true;

    /// <summary>对外公布的基础地址，可为空（自动推断）。</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>是否启用自动发现。</summary>
    public bool DiscoveryEnabled { get; set; } = true;

    /// <summary>注册设备总数。</summary>
    public int DeviceCount { get; set; }

    /// <summary>在线设备数。</summary>
    public int OnlineDeviceCount { get; set; }

    /// <summary>待处理（未同步到最新版本）设备数。</summary>
    public int PendingDeviceCount { get; set; }

    /// <summary>服务启动时间（UTC）。</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>已运行秒数。</summary>
    public long UptimeSeconds { get; set; }

    /// <summary>数据目录。</summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>HTTP 监听端口。</summary>
    public int HttpPort { get; set; }

    /// <summary>局域网发现端口。</summary>
    public int DiscoveryPort { get; set; }

    /// <summary>内置 NTP 服务器是否启用。</summary>
    public bool NtpServerEnabled { get; set; }

    /// <summary>内置 NTP 服务器端口（标准 123）。</summary>
    public int NtpPort { get; set; }

    /// <summary>品牌个性化配置（站点名称 / Logo / 图标）。</summary>
    public BrandingDto Branding { get; set; } = new();
}

/// <summary>
/// 站点品牌个性化配置，仿 VoiceHub 站点配置，用于替换站点名称、Logo 与图标。
/// </summary>
public sealed class BrandingDto
{
    /// <summary>站点名称，显示在登录页标题、浏览器标题与侧边栏品牌区。</summary>
    public string SiteName { get; set; } = "ClassIsland 集控系统";

    /// <summary>Logo 文字，显示在品牌标识方块中（默认 "CI"）。</summary>
    public string LogoText { get; set; } = "CI";

    /// <summary>Logo 图片（data URL 或图片 URL），为空时使用 LogoText。</summary>
    public string LogoImage { get; set; } = string.Empty;

    /// <summary>浏览器标签图标（data URL），为空时使用默认图标。</summary>
    public string Favicon { get; set; } = string.Empty;
}

/// <summary>
/// 设备分组。
/// </summary>
public sealed class GroupDto
{
    /// <summary>分组 ID（GUID 字符串）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>分组名称，例如「高一年级」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>默认下发的配置档案 ID。分组内设备未单独指定档案时使用。</summary>
    public string? DefaultProfileId { get; set; }

    /// <summary>分组内设备数。</summary>
    public int DeviceCount { get; set; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 新增或修改分组。
/// </summary>
public sealed class GroupUpsertRequest
{
    /// <summary>分组名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>分组默认下发的配置档案 ID，留空表示不指定。</summary>
    public string? DefaultProfileId { get; set; }
}

/// <summary>
/// 新增或修改配置档案。
/// </summary>
public sealed class ProfileUpsertRequest
{
    /// <summary>档案名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>档案内容。修改内容时会递增档案版本并触发全局配置版本更新。</summary>
    public ContentBundleDto? Content { get; set; }
}

/// <summary>
/// 档案保存结果。包含自动修复说明，便于管理端提示用户。
/// </summary>
public sealed class ProfileSaveResult
{
    /// <summary>保存后的档案。</summary>
    public ProfileDto? Profile { get; set; }

    /// <summary>内容校验过程中自动修复或跳过的项。</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>保存后的全局配置版本号。</summary>
    public long Revision { get; set; }
}

/// <summary>
/// 配置档案。一个档案即一套可下发的课表/时间表/科目集合。
/// </summary>
public sealed class ProfileDto
{
    /// <summary>档案 ID（GUID 字符串）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>档案名称，例如「2026 春季学期」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>内容版本号。每次内容变更递增，用于判断客户端是否需要重新同步。</summary>
    public long Revision { get; set; }

    /// <summary>档案内容。</summary>
    public ContentBundleDto Content { get; set; } = new();

    /// <summary>是否可作为新建设备的默认档案。</summary>
    public bool IsDefault { get; set; }

    /// <summary>最后更新时间（UTC）。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>已绑定该档案的设备数（含经分组间接绑定）。</summary>
    public int BoundDeviceCount { get; set; }
}

/// <summary>
/// 下发绑定关系。
/// </summary>
public sealed class AssignmentDto
{
    /// <summary>绑定 ID（GUID 字符串）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>目标类型：<c>device</c> 或 <c>group</c>。</summary>
    public string TargetType { get; set; } = "device";

    /// <summary>目标 ID（设备 ID 或分组 ID）。</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>目标显示名。</summary>
    public string? TargetName { get; set; }

    /// <summary>绑定的档案 ID。</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>档案名称。</summary>
    public string? ProfileName { get; set; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 注册码。用于允许新设备接入集控。
/// </summary>
public sealed class EnrollCodeDto
{
    /// <summary>注册码。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>备注，例如「高一（3）班」。绑定到设备后会记录设备名。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>可注册次数，<c>0</c> 表示不限。</summary>
    public int MaxUses { get; set; } = 1;

    /// <summary>已使用次数。</summary>
    public int UsedCount { get; set; }

    /// <summary>过期时间（UTC），为空表示长期有效。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>可用余量，<c>-1</c> 表示不限。</summary>
    public int RemainingUses => MaxUses <= 0 ? -1 : Math.Max(0, MaxUses - UsedCount);
}

/// <summary>
/// 新增/修改注册码。
/// </summary>
public sealed class EnrollCodeUpsertRequest
{
    /// <summary>备注。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>可注册次数，<c>0</c> 表示不限。</summary>
    public int MaxUses { get; set; } = 1;

    /// <summary>有效期（小时），<c>0</c> 表示长期有效。</summary>
    public int ValidHours { get; set; } = 72;
}

/// <summary>
/// 立即推送请求。管理员在 Web 端触发，服务端递增版本并唤醒客户端。
/// </summary>
public sealed class PushRequest
{
    /// <summary>推送范围：<c>all</c> / <c>group</c> / <c>device</c>。</summary>
    public string Scope { get; set; } = "all";

    /// <summary>目标 ID 列表。范围非 <c>all</c> 时必填。</summary>
    public List<string> TargetIds { get; set; } = [];

    /// <summary>是否强制覆盖客户端本地内容。</summary>
    public bool Force { get; set; }

    /// <summary>附带下发给客户端的提示消息。</summary>
    public string? Message { get; set; }
}

/// <summary>
/// 审计日志条目。
/// </summary>
public sealed class AuditLogDto
{
    /// <summary>自增序号。</summary>
    public long Id { get; set; }

    /// <summary>发生时间（UTC）。</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>操作来源：管理员用户名或设备名。</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>操作类型，例如 <c>device.enroll</c>、<c>profile.update</c>。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>操作目标。</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>补充说明。</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>来源 IP。</summary>
    public string? IpAddress { get; set; }
}

/// <summary>
/// 仪表盘统计。
/// </summary>
public sealed class DashboardStatsDto
{
    /// <summary>设备总数。</summary>
    public int DeviceCount { get; set; }

    /// <summary>在线设备数。</summary>
    public int OnlineDeviceCount { get; set; }

    /// <summary>离线设备数。</summary>
    public int OfflineDeviceCount { get; set; }

    /// <summary>待同步设备数。</summary>
    public int PendingDeviceCount { get; set; }

    /// <summary>同步异常设备数。</summary>
    public int ErrorDeviceCount { get; set; }

    /// <summary>分组数。</summary>
    public int GroupCount { get; set; }

    /// <summary>档案数。</summary>
    public int ProfileCount { get; set; }

    /// <summary>当前配置版本。</summary>
    public long Revision { get; set; }

    /// <summary>最近 24 小时注册设备数。</summary>
    public int RecentEnrollCount { get; set; }

    /// <summary>在线率（0~1）。</summary>
    public double OnlineRate => DeviceCount == 0 ? 0 : (double)OnlineDeviceCount / DeviceCount;

    /// <summary>最近事件。</summary>
    public List<AuditLogDto> RecentEvents { get; set; } = [];
}
