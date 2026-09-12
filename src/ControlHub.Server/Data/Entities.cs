namespace ControlHub.Server.Data;

/// <summary>管理员账号。</summary>
public sealed class UserRow
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "admin";
    public bool MustChangePassword { get; set; }
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
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>设备分组。</summary>
public sealed class GroupRow
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
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
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>配置档案。内容以 JSON 文本存储，便于结构与协议同步演进。</summary>
public sealed class ProfileRow
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
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
