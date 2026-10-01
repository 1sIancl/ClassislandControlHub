using System.Net;
using System.Security.Cryptography;
using System.Text;
using ControlHub.Protocol;
using ControlHub.Server.Data;
using ControlHub.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Services;

/// <summary>
/// 本地外壳（桌面端）信任通道：让桌面外壳里嵌的 Web 管理界面免登录。
/// <para>
/// 为什么不直接「来自回环地址就免登录」：本机任何进程、甚至浏览器里打开的网页都能向
/// <c>http://127.0.0.1:29800</c> 发请求，那样等于把超级管理员权限开放给本机的一切代码。
/// 因此这里要求出示一个只有本机可读的令牌文件内容——外壳读
/// <c>&lt;数据目录&gt;/local-shell.token</c>，把它带在首次导航上；服务端校验通过后种一个
/// HttpOnly Cookie，之后的请求凭 Cookie 放行（Cookie 里存的是令牌的 SHA-256，不落原始令牌）。
/// </para>
/// </summary>
public sealed class LocalShellTrust
{
    /// <summary>本地外壳会话使用的固定用户标识（不落库，仅用于审计与界面展示）。</summary>
    public const string UserId = "local-shell";

    /// <summary>审计与展示用的用户名。</summary>
    public const string Username = "local-shell";

    /// <summary>界面展示名。</summary>
    public const string DisplayName = "本地控制台";

    /// <summary>免登录 Cookie 名。</summary>
    public const string CookieName = "hub_local_shell";

    /// <summary>也可用该请求头出示令牌（便于脚本化验证与调试）。</summary>
    public const string HeaderName = "X-Hub-Local-Shell";

    /// <summary>令牌文件名（位于数据目录）。</summary>
    public const string TokenFileName = "local-shell.token";

    private readonly ServerOptions _options;
    private readonly ILogger<LocalShellTrust> _logger;
    private string _token = string.Empty;

    /// <summary>构造函数。</summary>
    public LocalShellTrust(IOptions<ServerOptions> options, IHostEnvironment environment,
        ILogger<LocalShellTrust> logger)
    {
        _options = options.Value;
        _logger = logger;
        TokenPath = Path.Combine(_options.ResolveDataDirectory(environment.ContentRootPath), TokenFileName);
    }

    /// <summary>令牌文件路径（供外壳与运维查看，文件内容即令牌）。</summary>
    public string TokenPath { get; }

    /// <summary>信任通道是否可用（未关闭且令牌已就绪）。</summary>
    public bool Enabled => _options.LocalShellTrustEnabled && _token.Length > 0;

    /// <summary>启动时调用：确保令牌文件存在并读入内存。</summary>
    public void Initialize()
    {
        if (!_options.LocalShellTrustEnabled)
        {
            _logger.LogInformation("本地外壳信任通道已关闭（ControlHub:LocalShellTrustEnabled=false）。");
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
            if (File.Exists(TokenPath))
            {
                var existing = File.ReadAllText(TokenPath).Trim();
                if (existing.Length >= 32)
                {
                    _token = existing;
                    return;
                }
            }

            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            File.WriteAllText(TokenPath, _token);
            TryRestrictPermissions(TokenPath);
            _logger.LogInformation("已生成本地外壳令牌文件：{Path}（内容即令牌，请勿外传）", TokenPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "无法准备本地外壳令牌，本地免登录不可用（不影响其它功能）。");
            _token = string.Empty;
        }
    }

    /// <summary>当前令牌对应的 Cookie 值（令牌的 SHA-256）。</summary>
    public string CookieValue => CookieValueOf(_token);

    /// <summary>
    /// 判断请求是否来自本机外壳；命中时给出可用于鉴权的会话。
    /// </summary>
    public bool TryAuthenticate(HttpContext http, out SessionRow session)
    {
        session = null!;
        if (!Enabled || !IsLoopback(http))
        {
            return false;
        }

        // 1) 请求头 / 查询串出示原始令牌：外壳首次导航、脚本化验证走这条。
        var presented = http.Request.Headers[HeaderName].FirstOrDefault()
                        ?? http.Request.Query["shellToken"].FirstOrDefault();
        var matched = !string.IsNullOrEmpty(presented) && FixedTimeEquals(presented, _token);

        // 2) Cookie 里存的是令牌的 SHA-256。
        if (!matched)
        {
            var cookie = http.Request.Cookies[CookieName];
            matched = !string.IsNullOrEmpty(cookie) && FixedTimeEquals(cookie, CookieValue);
        }

        if (!matched)
        {
            return false;
        }

        session = new SessionRow
        {
            // 本地外壳没有数据库会话，Token 留空；鉴权过滤器只把它塞进请求上下文。
            Token = string.Empty,
            UserId = UserId,
            Username = Username,
            DisplayName = DisplayName,
            Role = PermissionKeys.AdministratorRole,
            Permissions = [],
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        return true;
    }

    /// <summary>该会话是否来自本地外壳。</summary>
    public static bool IsLocalShell(SessionRow session) =>
        string.Equals(session.UserId, UserId, StringComparison.Ordinal);

    /// <summary>请求是否来自回环地址（局域网访问不受影响）。</summary>
    public static bool IsLoopback(HttpContext http)
    {
        var remote = http.Connection.RemoteIpAddress;
        return remote is not null && IPAddress.IsLoopback(remote);
    }

    /// <summary>令牌 → Cookie 值（不把原始令牌留在浏览器里）。</summary>
    public static string CookieValueOf(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows 上继承目录 ACL 即可，不再额外收紧。
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // 个别文件系统不支持，忽略。
        }
    }
}
