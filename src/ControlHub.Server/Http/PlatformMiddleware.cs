using System.Diagnostics;
using System.Net;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Http;

// ────────────────────────────── 安全响应头（#37） ──────────────────────────────

/// <summary>
/// 给所有响应补上基础的浏览器侧防护头。
/// <para>CSP 保留 <c>'unsafe-inline'</c>（style 与 script）是有意的：管理界面是原生 HTML/CSS/JS，
/// 主题、密度与品牌色依赖内联样式与少量内联脚本，去掉会直接白屏。要收紧到 nonce 必须先改造前端的
/// 资源加载方式——在那之前不要「顺手加严」，否则等于把界面改坏。</para>
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'; object-src 'none'; " +
        "img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; " +
        "connect-src 'self'; font-src 'self' data:";

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;

        // HSTS 只在真正走 HTTPS 时下发：HTTP 下发了浏览器会把之后的 HTTP 访问也强制升级，反而更难用。
        if (context.Request.IsHttps)
        {
            headers["Strict-Transport-Security"] = "max-age=31536000";
        }

        await next(context);
    }
}

// ────────────────────────────── HTTPS 强制跳转（#32） ──────────────────────────────

/// <summary>
/// 把 HTTP 请求跳转到 HTTPS（仅在 <c>ControlHub:RequireHttpsRedirect=true</c> 时挂载）。
/// <para>回环地址**永远不跳转**：桌面外壳的「本机免登录」依赖 http://127.0.0.1，跳转会让它立刻失效。</para>
/// </summary>
public sealed class HttpsRedirectMiddleware(RequestDelegate next, IOptions<ServerOptions> options)
{
    private readonly ServerOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (context.Request.IsHttps || (remote is not null && IPAddress.IsLoopback(remote)))
        {
            await next(context);
            return;
        }

        var builder = new UriBuilder("https", context.Request.Host.Host)
        {
            Path = context.Request.Path.Value ?? "/",
            Query = context.Request.QueryString.Value ?? string.Empty,
        };

        if (_options.HttpsPort > 0 && _options.HttpsPort != 443)
        {
            builder.Port = _options.HttpsPort;
        }

        context.Response.Redirect(builder.Uri.ToString(), permanent: false);
    }
}

// ────────────────────────────── 管理端来源限制（#34） ──────────────────────────────

/// <summary>
/// 管理端来源允许列表：配置 <c>ControlHub:AdminIpAllowList</c> 后，只有名单内的来源能访问管理端接口。
/// <para>只约束 <c>/api/v1/admin</c>——教室端（<c>/client</c>）必须放行，否则教室就上不来了。
/// 回环地址始终放行（本机免登录与运维脚本依赖它）。名单为空表示完全不限制。</para>
/// </summary>
public sealed class AdminIpAllowListMiddleware(RequestDelegate next, IOptions<ServerOptions> options)
{
    private readonly IReadOnlyList<(byte[] Network, int Prefix)> _ranges =
        ParseRanges(options.Value.AdminIpAllowList);

    /// <summary>解析出的规则条数（供启动日志打印，便于排查「配了却没生效」）。</summary>
    internal int RuleCount => _ranges.Count;

    public async Task InvokeAsync(HttpContext context)
    {
        if (_ranges.Count == 0 || !IsAdminApi(context.Request.Path))
        {
            await next(context);
            return;
        }

        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && (IPAddress.IsLoopback(remote) || IsAllowed(remote)))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(ApiResult<object>.Failure(
            HubErrorCodes.IpNotAllowed,
            $"来源地址 {remote} 不在管理端允许列表内（ControlHub:AdminIpAllowList）。"));
    }

    private static bool IsAdminApi(PathString path) =>
        path.StartsWithSegments(HubProtocol.ApiPrefix + "/admin");

    private bool IsAllowed(IPAddress address)
    {
        var bytes = Normalize(address).GetAddressBytes();
        foreach (var (network, prefix) in _ranges)
        {
            // 必须同族比较（v4 对 v4、v6 对 v6）：
            // 曾把双方都映射成 IPv6 再比前缀，结果 v4 的 ::ffff: 前缀全是 0，
            // 任何 IPv4 都能匹配上 —— 配置了却全部放行，比不配更危险。
            if (network.Length != bytes.Length)
            {
                continue;
            }

            if (Matches(bytes, network, prefix))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把 IPv4-mapped IPv6（::ffff:192.168.x.x，Windows/Linux 上很常见）还原成 IPv4，便于同族比较。</summary>
    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>按前缀位数逐位比较（v4 与 v6 统一按高位对齐）。</summary>
    private static bool Matches(byte[] address, byte[] network, int prefix)
    {
        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes && i < address.Length; i++)
        {
            if (address[i] != network[i])
            {
                return false;
            }
        }

        var remainingBits = prefix % 8;
        if (remainingBits == 0 || fullBytes >= address.Length)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (address[fullBytes] & mask) == (network[fullBytes] & mask);
    }

    internal static List<(byte[] Network, int Prefix)> ParseRanges(string[] entries)
    {
        var ranges = new List<(byte[], int)>();
        foreach (var entry in entries)
        {
            var text = entry?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var parts = text.Split('/', 2);
            if (!IPAddress.TryParse(parts[0], out var address))
            {
                // 写错的条目直接忽略：宁可少挡一层，也不要因为一条笔误把管理员自己关在门外。
                continue;
            }

            var bytes = Normalize(address).GetAddressBytes();
            var maxBits = bytes.Length * 8;
            var prefix = maxBits;
            if (parts.Length == 2 && int.TryParse(parts[1], out var parsed) && parsed >= 0 && parsed <= maxBits)
            {
                prefix = parsed;
            }

            ranges.Add((bytes, prefix));
        }

        return ranges;
    }

    /// <summary>解析日志用：把允许列表原样回显，便于启动时确认到底配上了没有。</summary>
    public string DescribeAllowList() =>
        _ranges.Count == 0
            ? "未配置（管理端不限制来源）"
            : string.Join(", ", _ranges.Select(r => $"{new IPAddress(r.Network)}/{r.Prefix}"));
}

// ────────────────────────────── 访问日志与追踪 ID（#29 / #84 / #85） ──────────────────────────────

/// <summary>
/// 为每个 API 请求记录一条结构化日志（方法 / 路径 / 状态码 / 耗时 / 来源），并回写 <c>X-Request-Id</c>。
/// <para>心跳与长轮询这类高频路径降级为 Debug：800 台设备的心跳若每条都记 Information，
/// 日志会把磁盘写满，也会淹掉真正需要看的那几条。</para>
/// </summary>
public sealed class ApiAccessLogMiddleware(
    RequestDelegate next,
    ILogger<ApiAccessLogMiddleware> logger,
    IOptions<ServerOptions> options)
{
    private readonly ServerOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // 追踪 ID：优先沿用调用方带来的，便于与服务端其它日志、前端报错对齐。
        var traceId = context.Request.Headers["X-Request-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(traceId))
        {
            traceId = context.TraceIdentifier;
        }

        context.Response.Headers["X-Request-Id"] = traceId;

        if (!_options.AccessLogEnabled || !path.StartsWith(HubProtocol.ApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();

            var noisy = IsHighFrequency(context.Request.Path);
            using (logger.BeginScope(new Dictionary<string, object> { ["traceId"] = traceId }))
            {
                var level = noisy ? LogLevel.Debug : LogLevel.Information;
                logger.Log(level,
                    "API {Method} {Path} → {Status} 用时 {ElapsedMs}ms，来源 {ClientIp}，追踪 {TraceId}",
                    context.Request.Method,
                    path,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    context.Connection.RemoteIpAddress?.ToString() ?? "-",
                    traceId);
            }
        }
    }

    /// <summary>心跳 / 长轮询 / 探针属于高频路径，按 Debug 记录。</summary>
    private static bool IsHighFrequency(PathString path) =>
        path.StartsWithSegments(HubProtocol.ApiPrefix + "/client/heartbeat")
        || path.StartsWithSegments(HubProtocol.ApiPrefix + "/client/wait")
        || path.StartsWithSegments(HubProtocol.ApiPrefix + "/client/commands")
        || path.StartsWithSegments(HubProtocol.ApiPrefix + "/ping")
        || path.Equals("/api/health");
}
