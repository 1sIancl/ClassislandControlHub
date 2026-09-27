using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Server.Services;

/// <summary>
/// 权限目录：把权限键按模块组织成「查看 / 修改」两列，供账号管理界面直接渲染。
/// <para>这里是权限文案的唯一来源，前端不重复维护一份。</para>
/// </summary>
public static class PermissionCatalog
{
    /// <summary>一个模块的权限定义。</summary>
    public sealed record Module(string Key, string Label, string Description, string? ReadKey, string? WriteKey);

    /// <summary>全部模块。</summary>
    public static readonly Module[] Modules =
    [
        new("profiles", "配置档案", "时间表、课表、科目与自定义设置。", PermissionKeys.ProfilesRead, PermissionKeys.ProfilesWrite),
        new("devices", "设备与分组", "教室终端、楼栋楼层分组与设备注册码。", PermissionKeys.DevicesRead, PermissionKeys.DevicesWrite),
        new("deploy", "配置下发", "把配置档案推送给教室终端。", null, PermissionKeys.DeployWrite),
        new("remote", "远程管理", "远程命令行、插件启停、外观下发与即时提醒。", PermissionKeys.RemoteRead, PermissionKeys.RemoteWrite),
        new("reminders", "定时提醒", "创建按时间自动推送到教室大屏的提醒。", PermissionKeys.RemindersRead, PermissionKeys.RemindersWrite),
        new("audit", "审计日志", "查看谁在什么时候做了什么。", PermissionKeys.AuditRead, null),
        new("backup", "备份与恢复", "数据库备份、下载与恢复。", PermissionKeys.BackupRead, PermissionKeys.BackupWrite),
        new("settings", "系统设置", "品牌、时间偏移、AI 配置与自动更新。", PermissionKeys.SettingsRead, PermissionKeys.SettingsWrite),
        new("accounts", "账号管理", "新建账号、分配权限、重置密码与邀请码。", PermissionKeys.AccountsRead, PermissionKeys.AccountsWrite),
    ];

    /// <summary>转换为前端使用的 DTO 列表。</summary>
    public static List<PermissionModuleDto> ToDto() => Modules
        .Select(m => new PermissionModuleDto
        {
            Key = m.Key,
            Label = m.Label,
            Description = m.Description,
            ReadKey = m.ReadKey,
            WriteKey = m.WriteKey,
        })
        .ToList();

    /// <summary>把权限键翻译成「模块 · 查看/修改」，用于错误提示。</summary>
    public static string Describe(string permission)
    {
        foreach (var module in Modules)
        {
            if (module.ReadKey == permission)
            {
                return $"{module.Label} · 查看";
            }

            if (module.WriteKey == permission)
            {
                return $"{module.Label} · 修改";
            }
        }

        return permission;
    }
}
