namespace ControlHub.Protocol;

/// <summary>
/// 管理端权限键。
/// <para>约定 <c>模块.动作</c>：<c>模块.read</c> 管查看，<c>模块.write</c> 管新增/修改/删除。</para>
/// <para>账号按模块勾选权限；服务端对每条管理端路由强制校验，未声明权限的路由一律拒绝（fail-closed）。</para>
/// </summary>
public static class PermissionKeys
{
    /// <summary>仅要求「已登录」，不需要额外模块权限。</summary>
    public const string Authenticated = "__authenticated";

    /// <summary>超级管理员角色：拥有全部权限，且不可被裁剪。</summary>
    public const string AdministratorRole = "admin";

    /// <summary>自定义角色：按 <c>permissions</c> 勾选决定能做什么。</summary>
    public const string CustomRole = "custom";

    /// <summary>配置档案：查看。</summary>
    public const string ProfilesRead = "profiles.read";

    /// <summary>配置档案：新增 / 修改 / 删除 / 导入。</summary>
    public const string ProfilesWrite = "profiles.write";

    /// <summary>设备与分组：查看。</summary>
    public const string DevicesRead = "devices.read";

    /// <summary>设备与分组：改名 / 调组 / 停用 / 删除 / 注册码管理。</summary>
    public const string DevicesWrite = "devices.write";

    /// <summary>配置下发：向教室终端推送配置。</summary>
    public const string DeployWrite = "deploy.write";

    /// <summary>远程管理：查看指令历史与插件清单。</summary>
    public const string RemoteRead = "remote.read";

    /// <summary>远程管理：下发命令 / 启停插件 / 下发外观 / 发送提醒。</summary>
    public const string RemoteWrite = "remote.write";

    /// <summary>定时提醒：查看自己的提醒。</summary>
    public const string RemindersRead = "reminders.read";

    /// <summary>定时提醒：创建 / 修改 / 删除（提醒会推送到教室大屏）。</summary>
    public const string RemindersWrite = "reminders.write";

    /// <summary>审计日志：查看。</summary>
    public const string AuditRead = "audit.read";

    /// <summary>备份：查看与下载。</summary>
    public const string BackupRead = "backup.read";

    /// <summary>备份：创建 / 删除 / 恢复。</summary>
    public const string BackupWrite = "backup.write";

    /// <summary>系统设置：查看（品牌 / 时间偏移 / AI 配置 / 更新状态）。</summary>
    public const string SettingsRead = "settings.read";

    /// <summary>系统设置：修改。</summary>
    public const string SettingsWrite = "settings.write";

    /// <summary>账号管理：查看账号列表与权限。</summary>
    public const string AccountsRead = "accounts.read";

    /// <summary>账号管理：新建 / 修改 / 重置密码 / 删除账号，以及邀请码管理。</summary>
    public const string AccountsWrite = "accounts.write";

    /// <summary>
    /// 全部可授予的权限键（不含 <see cref="Authenticated"/>）。
    /// <para>约定为只读，调用方不得修改；需要改动请走 <see cref="Normalize"/> 生成新集合。</para>
    /// </summary>
    public static readonly List<string> Grantable =
    [
        ProfilesRead, ProfilesWrite,
        DevicesRead, DevicesWrite,
        DeployWrite,
        RemoteRead, RemoteWrite,
        RemindersRead, RemindersWrite,
        AuditRead,
        BackupRead, BackupWrite,
        SettingsRead, SettingsWrite,
        AccountsRead, AccountsWrite,
    ];

    /// <summary>
    /// 新建账号时默认勾选的权限：覆盖日常管理（档案 / 设备 / 下发 / 远程 / 提醒 / 审计），
    /// 但不含账号管理与系统设置，避免默认就给出提权入口。
    /// </summary>
    public static readonly List<string> DefaultForNewUser =
    [
        ProfilesRead, ProfilesWrite,
        DevicesRead, DevicesWrite,
        DeployWrite,
        RemoteRead, RemoteWrite,
        RemindersRead, RemindersWrite,
        AuditRead,
    ];

    /// <summary>
    /// 判断某个角色 + 权限集合是否满足要求。
    /// <para>超级管理员通吃；其余账号在 <paramref name="granted"/> 里精确匹配。</para>
    /// </summary>
    public static bool Satisfies(string? role, IReadOnlyCollection<string>? granted, string required)
    {
        if (string.Equals(required, Authenticated, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(role, AdministratorRole, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return granted is not null && granted.Contains(required);
    }

    /// <summary>归一化权限集合：去空白、去重、剔除未知键，并保持稳定顺序。</summary>
    public static List<string> Normalize(IEnumerable<string>? permissions)
    {
        var wanted = (permissions ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToHashSet(StringComparer.Ordinal);

        return Grantable.Where(wanted.Contains).ToList();
    }
}
