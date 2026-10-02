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
    /// HTTPS 证书文件（<c>.pfx</c>）路径。留空时自动生成自签名证书并缓存到数据目录，
    /// 便于内网在没有正式证书时也能直接启用 HTTPS。
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>HTTPS 证书口令。仅在 <see cref="CertificatePath"/> 指向受密码保护的证书时需要。</summary>
    public string? CertificatePassword { get; set; }

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

    /// <summary>
    /// 是否启用本地外壳（桌面端）信任通道：仅对回环地址 + 正确的外壳令牌生效，
    /// 让桌面外壳里嵌的 Web 界面免登录。不需要可关闭（例如多人共用的机器）。
    /// </summary>
    public bool LocalShellTrustEnabled { get; set; } = true;

    /// <summary>客户端日志保留天数。</summary>
    public int ClientLogRetentionDays { get; set; } = 30;

    /// <summary>远程指令历史保留天数（指令载荷与输出最大各 32KB，长期不清理会持续占用）。</summary>
    public int CommandRetentionDays { get; set; } = 30;

    /// <summary>诊断工件（屏幕截图）保留天数。</summary>
    public int DiagnosticRetentionDays { get; set; } = 30;

    /// <summary>
    /// 诊断工件总容量上限（MB）。超出后从最旧的开始删除——
    /// 单机截图体积大，只靠「每设备 5 张」在数百台规模下仍会累积到 GB 级。
    /// </summary>
    public int DiagnosticMaxTotalMb { get; set; } = 512;

    /// <summary>
    /// 配置下发分批大小（台）。设为 0（默认）表示一次性推进全部设备。
    /// <para>大校「一键全推」时建议设为 20~50：推送只改生效时机，分批是安全的，
    /// 但能避免数百台设备在同一秒涌上来拉配置、把学校上行带宽打满。</para>
    /// </summary>
    public int PushBatchSize { get; set; }

    /// <summary>批与批之间的间隔秒数（仅分批下发生效，最小按 1 秒处理）。</summary>
    public int PushBatchDelaySeconds { get; set; } = 5;

    /// <summary>
    /// 是否发送安全响应头（CSP / X-Frame-Options / X-Content-Type-Options / Referrer-Policy 等）。
    /// <para>默认开启；若接入的某个前端定制与 CSP 冲突，可临时关掉排查（关掉会降低浏览器侧防护）。</para>
    /// </summary>
    public bool SecurityHeadersEnabled { get; set; } = true;

    /// <summary>
    /// 是否把 HTTP 请求强制跳转到 HTTPS。默认关闭。
    /// <para>开启前请确认 HTTPS 已可用（<see cref="EnableHttps"/> 或有反代终结 TLS），
    /// 否则会把所有访问者挡在门外。回环地址永远不跳转——桌面外壳的本机免登录依赖 http://127.0.0.1。</para>
    /// </summary>
    public bool RequireHttpsRedirect { get; set; }

    /// <summary>
    /// 管理端来源允许列表（IP 或 CIDR，例如 <c>192.168.1.0/24</c>）。为空表示不限制。
    /// <para>只约束 <c>/api/v1/admin</c>：教室端接口不受影响（否则教室就上不来了）。
    /// 回环地址始终放行，便于本机运维与桌面外壳。</para>
    /// </summary>
    public string[] AdminIpAllowList { get; set; } = [];

    /// <summary>是否记录 API 访问日志（每个 /api 请求一条结构化日志，含耗时与追踪 ID）。</summary>
    public bool AccessLogEnabled { get; set; } = true;

    /// <summary>
    /// 是否改用 JSON 控制台日志（便于日志平台按字段检索）。默认关闭，保持人类友好的文本日志。
    /// </summary>
    public bool LogJson { get; set; }

    /// <summary>
    /// 连续登录失败多少次要临时锁定账号（#27）。设为 0 表示不锁定（不建议）。
    /// <para>只按用户名计数、不按来源 IP：学校的出口 IP 往往是同一个，
    /// 按 IP 统计会因为一台机器被扫而误伤整栋楼的所有人。</para>
    /// </summary>
    public int LoginMaxFailures { get; set; } = 5;

    /// <summary>锁定与失败计数的时间窗口（分钟）：超过这个时间的失败不再计入。</summary>
    public int LoginLockoutMinutes { get; set; } = 15;

    /// <summary>登录尝试记录的保留天数（维护任务清理）。</summary>
    public int LoginAttemptRetentionDays { get; set; } = 30;

    /// <summary>
    /// <c>/metrics</c> 的访问令牌。<para>留空时只允许**本机**访问（同机 Prometheus / Zabbix agent 的场景）；
    /// 填了令牌后，外部采集器需带 <c>?token=</c> 或 <c>Authorization: Bearer</c>。</para>
    /// </summary>
    public string MetricsToken { get; set; } = string.Empty;

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
