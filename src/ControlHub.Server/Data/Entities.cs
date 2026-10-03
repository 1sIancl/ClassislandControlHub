namespace ControlHub.Server.Data;

/// <summary>管理员账号。</summary>
public sealed class UserRow
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 密码最后修改时间（#28 密码到期提醒的基准）。
    /// <para>老库升级后该列为空，此时以 <see cref="CreatedAt"/> 兜底——否则老账号永远不会提醒，
    /// 功能等于没做。</para>
    /// </summary>
    public DateTimeOffset? PasswordChangedAt { get; set; }

    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>角色：admin（超级管理员，全部权限）/ custom（按 <see cref="Permissions"/> 勾选）。</summary>
    public string Role { get; set; } = "admin";

    /// <summary>已授予的权限键集合（角色为 custom 时生效）。</summary>
    public List<string> Permissions { get; set; } = [];

    public bool MustChangePassword { get; set; }

    /// <summary>两步验证（TOTP）密钥；为空表示尚未绑定。</summary>
    public string TotpSecret { get; set; } = string.Empty;

    /// <summary>是否已启用两步验证。</summary>
    public bool TotpEnabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>管理员会话。</summary>
public sealed class SessionRow
{
    public string Token { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "admin";

    /// <summary>该账号的权限键集合（随会话一起查出，供权限过滤器使用）。</summary>
    public List<string> Permissions { get; set; } = [];

    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>设备分组。</summary>
public sealed class GroupRow
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>视觉标识色（调色板键名，见前端 GROUP_COLORS）；为空时由前端按 ID 推导。</summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>上级分组 ID；为空表示顶层（楼栋 / 自定义分组）。</summary>
    public string? ParentId { get; set; }

    /// <summary>层级类型：building（楼栋）/ floor（楼层）/ 空（自定义分组）。</summary>
    public string Kind { get; set; } = string.Empty;

    public string? DefaultProfileId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>受管设备。</summary>
public sealed class DeviceRow
{
    public string Id { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string ClassIslandVersion { get; set; } = string.Empty;
    public string PluginVersion { get; set; } = string.Empty;
    public string? GroupId { get; set; }
    public string? ProfileId { get; set; }

    /// <summary>
    /// 设备**上一次成功应用**的是哪个档案（回报成功时落定）。
    /// <para>与 <see cref="ProfileId"/>（设备「应当」使用哪个档案）配合，才能算出「本次下发会改什么」。
    /// 老库升级后该列为空，表示还没有可对比的基线。</para>
    /// </summary>
    public string? AppliedProfileId { get; set; }

    /// <summary>
    /// 设备应用该档案时，档案处于第几版。
    /// <para>档案之后被编辑时，靠它配合 <c>profile_versions</c> 快照还原「设备当前真正在用的内容」，
    /// 否则对比双方会指向同一份最新内容，差异永远是空的。</para>
    /// </summary>
    public long AppliedProfileRevision { get; set; }

    public string State { get; set; } = "offline";
    public long AppliedRevision { get; set; }

    /// <summary>服务端为设备维护的推送世代号。</summary>
    public long PushEpoch { get; set; }

    /// <summary>设备已回传的推送世代号。</summary>
    public long AppliedPushEpoch { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    public string? IpAddress { get; set; }
    public bool Revoked { get; set; }
    public string? CurrentClassPlanName { get; set; }
    public string Metrics { get; set; } = "{}";

    /// <summary>管理员备注：仅管理端可见，用于标注位置、负责人等。</summary>
    public string Remark { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>配置档案。内容以 JSON 文本存储，便于结构与协议同步演进。</summary>
public sealed class ProfileRow
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>四位识别码，用于快速区分档案（创建时自动生成，唯一）。</summary>
    public string Code { get; set; } = string.Empty;

    public long Revision { get; set; }
    public string Content { get; set; } = "{}";
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>设备注册码。</summary>
public sealed class EnrollCodeRow
{
    public string Code { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public int MaxUses { get; set; } = 1;
    public int UsedCount { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>审计日志。</summary>
public sealed class AuditLogRow
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
}

/// <summary>客户端上报的日志。</summary>
public sealed class ClientLogRow
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Level { get; set; } = "info";
    public string Message { get; set; } = string.Empty;
}
