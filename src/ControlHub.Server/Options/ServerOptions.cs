using ControlHub.Protocol;

namespace ControlHub.Server.Options;

/// <summary>
/// 集控服务端配置。绑定自 <c>appsettings.json</c> 的 <c>ControlHub</c> 节，
/// 也可通过环境变量（如 <c>ControlHub__HttpPort=8080</c>）或命令行覆盖。
/// </summary>
public sealed class ServerOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "ControlHub";

    /// <summary>服务器名称，会在客户端与局域网发现中显示。</summary>
    public string ServerName { get; set; } = "ClassislandControlHub 集控服务器";

    /// <summary>HTTP 监听端口。</summary>
    public int HttpPort { get; set; } = HubProtocol.DefaultHttpPort;

    /// <summary>
    /// 监听地址。<c>0.0.0.0</c> 表示监听全部网卡（局域网可访问），
    /// 仅本机访问可设为 <c>127.0.0.1</c>。
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>是否启用 HTTPS。</summary>
    public bool EnableHttps { get; set; }

    /// <summary>HTTPS 监听端口。</summary>
    public int HttpsPort { get; set; } = 29801;

    /// <summary>
    /// 对外公布的基础地址，例如 <c>https://hub.example.edu</c>。
    /// 为空时由服务端按请求自动推断（适用于反向代理之外的直连场景）。
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>数据目录（相对路径基于内容根目录）。</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>设备注册是否必须提供注册码。</summary>
    public bool RequireEnrollCode { get; set; } = true;

    /// <summary>是否启用局域网 UDP 自动发现。</summary>
    public bool EnableDiscovery { get; set; } = true;

    /// <summary>UDP 发现端口。</summary>
    public int DiscoveryPort { get; set; } = HubProtocol.DefaultDiscoveryPort;

    /// <summary>是否启用服务端授时（周期性从标准 NTP 服务器同步时间）。</summary>
    public bool EnableNtpSync { get; set; } = true;

    /// <summary>标准 NTP 服务器列表（按顺序尝试）。默认阿里云，国内可达。</summary>
    public string[] NtpServers { get; set; } = ["ntp.aliyun.com"];

    /// <summary>是否启用内置 NTP 服务器（供教室终端 ClassIsland「精确时间」接入）。</summary>
    public bool EnableNtpServer { get; set; } = true;

    /// <summary>内置 NTP 服务器监听端口（标准为 123，需 root/管理员权限）。</summary>
    public int NtpPort { get; set; } = 123;

    /// <summary>自动更新的 GitHub 仓库（owner/repo），用于检查最新 Release。</summary>
    public string UpdateRepo { get; set; } = "1sIancl/ClassislandControlHub";

    /// <summary>是否启用自动检查更新（后台周期性查询 GitHub Release）。</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>自动检查更新的间隔（小时）。</summary>
    public int UpdateCheckIntervalHours { get; set; } = 12;

    /// <summary>是否在发现新版本后自动应用更新（谨慎：默认关闭，由管理员在 Web 端手动触发）。</summary>
    public bool AutoApplyUpdates { get; set; }

    /// <summary>是否启用自动备份（参照 ClassIsland，备份数据库与配置到 data/Backups）。</summary>
    public bool EnableAutoBackup { get; set; } = true;

    /// <summary>自动备份间隔（小时）。</summary>
    public int AutoBackupIntervalHours { get; set; } = 168;

    /// <summary>自动备份保留份数（超出后删除最旧的自动备份）。</summary>
    public int BackupRetention { get; set; } = 16;

    /// <summary>超过该秒数未收到心跳即视为离线。</summary>
    public int OnlineTimeoutSeconds { get; set; } = 120;

    /// <summary>管理员会话有效期（小时）。</summary>
    public int SessionLifetimeHours { get; set; } = 12;

    /// <summary>客户端日志保留天数。</summary>
    public int ClientLogRetentionDays { get; set; } = 30;

    /// <summary>审计日志保留天数。</summary>
    public int AuditLogRetentionDays { get; set; } = 180;

    /// <summary>首次启动时创建的管理员用户名。</summary>
    public string DefaultAdminUser { get; set; } = "admin";

    /// <summary>首次启动时创建的管理员密码。强烈建议首次登录后立即修改。</summary>
    public string DefaultAdminPassword { get; set; } = "admin123";

    /// <summary>
    /// 允许跨域访问的来源。留空表示不启用 CORS（同源访问，推荐）。
    /// 若需要把 Web 界面部署到别的域名再调用本服务，可在此列出允许的来源。
    /// </summary>
    public string[] CorsAllowedOrigins { get; set; } = [];

    /// <summary>解析后的数据目录绝对路径。</summary>
    public string ResolveDataDirectory(string contentRoot) =>
        Path.IsPathRooted(DataDirectory)
            ? DataDirectory
            : Path.GetFullPath(Path.Combine(contentRoot, DataDirectory));

    /// <summary>SQLite 数据库文件路径。</summary>
    public string ResolveDatabasePath(string contentRoot) =>
        Path.Combine(ResolveDataDirectory(contentRoot), "controlhub.db");
}
