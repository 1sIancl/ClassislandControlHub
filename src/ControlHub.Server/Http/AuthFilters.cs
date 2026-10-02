using System.Net;
using ControlHub.Protocol;
using ControlHub.Server.Data;
using ControlHub.Server.Services;

namespace ControlHub.Server.Http;

/// <summary>
/// 与管理端/客户端鉴权相关的请求辅助方法。
/// </summary>
public static class HttpContextExtensions
{
    private const string AdminSessionKey = "ControlHub.AdminSession";
    private const string DeviceKey = "ControlHub.Device";

    /// <summary>取出当前请求来源 IP。</summary>
    public static string? GetClientIpAddress(this HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            // 反向代理场景下取第一段真实来源地址。
            var first = forwarded.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(first))
            {
                return first;
            }
        }

        var remote = context.Connection.RemoteIpAddress;
        return remote is null || IPAddress.IsLoopback(remote) ? "127.0.0.1" : remote.ToString();
    }

    /// <summary>保存已认证的管理员会话。</summary>
    public static void SetAdminSession(this HttpContext context, SessionRow session) =>
        context.Items[AdminSessionKey] = session;

    /// <summary>获取已认证的管理员会话；未认证时抛出 401。</summary>
    public static SessionRow RequireAdminSession(this HttpContext context) =>
        context.Items[AdminSessionKey] as SessionRow
        ?? throw HubException.AuthRequired();

    /// <summary>保存已认证的设备。</summary>
    public static void SetDevice(this HttpContext context, DeviceRow device) =>
        context.Items[DeviceKey] = device;

    /// <summary>获取已认证的设备；未认证时抛出 401。</summary>
    public static DeviceRow RequireDevice(this HttpContext context) =>
        context.Items[DeviceKey] as DeviceRow
        ?? throw HubException.AuthRequired("设备未认证。");

    /// <summary>读取请求头中的令牌。</summary>
    public static string? ReadToken(this HttpContext context, string scheme)
    {
        var header = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var parts = header.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0].Equals(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return parts[1].Trim();
        }

        // 兼容只发送裸令牌的客户端。
        return parts.Length == 1 ? parts[0].Trim() : null;
    }
}

/// <summary>
/// 需要管理员身份的接口所使用的端点过滤器。
/// </summary>
public sealed class AdminAuthFilter(AdminAuthService authService, LocalShellTrust localShell, HubStore store)
    : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var token = http.ReadToken(HubProtocol.AdminScheme);
        var session = await authService.ValidateAsync(token, http.RequestAborted);

        // 本地外壳（桌面端）免登录：仅当请求来自回环地址且出示了正确的外壳令牌时才生效，
        // 局域网访问与其它本机进程都不受影响（详见 LocalShellTrust）。
        if (session is null && localShell.TryAuthenticate(http, out var shellSession))
        {
            session = shellSession;
        }

        // 开放 API（#74）：脚本 / 第三方用 API 密钥访问，权限按密钥创建时选定的子集授予。
        session ??= await TryApiKeyAsync(http, store);

        if (session is null)
        {
            throw HubException.AuthInvalid("登录状态已失效，请重新登录。");
        }

        http.SetAdminSession(session);
        return await next(context);
    }

    /// <summary>
    /// 用 API 密钥建立会话。支持 <c>Authorization: ApiKey &lt;密钥&gt;</c> 与 <c>X-Api-Key</c> 两种携带方式
    /// （后者是为了让不方便自定义 Authorization 头的工具也能接入）。
    /// <para>会话的权限就是密钥的权限——密钥被撤销或过期即立刻失效，不需要额外踢会话。</para>
    /// </summary>
    private static async Task<SessionRow?> TryApiKeyAsync(HttpContext http, HubStore store)
    {
        var key = http.ReadToken(HubStore.ApiKeyScheme);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = http.Request.Headers["X-Api-Key"].FirstOrDefault()?.Trim();
        }

        // 前缀检查能在绝大多数「传错凭证」的情况下直接跳过查库。
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(HubStore.ApiKeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var row = await store.GetApiKeyByHashAsync(HubStore.HashApiKey(key), http.RequestAborted);
        if (row is null || row.Revoked)
        {
            return null;
        }

        if (row.ExpiresAt.HasValue && row.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            return null;
        }

        await store.TouchApiKeyAsync(row.Id, http.RequestAborted);

        return new SessionRow
        {
            Token = string.Empty,
            UserId = "apikey:" + row.Id,
            Username = $"API密钥（{row.Name}）",
            DisplayName = row.Name,
            Role = "apikey",
            Permissions = row.Permissions,
            // 会话自身不设短过期：有效性完全由密钥表决定（撤销 / 过期即失效）。
            ExpiresAt = row.ExpiresAt ?? DateTimeOffset.UtcNow.AddYears(10),
        };
    }
}

/// <summary>
/// 需要设备身份的接口所使用的端点过滤器。
/// </summary>
public sealed class DeviceAuthFilter(HubStore store) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var token = http.ReadToken(HubProtocol.DeviceScheme)
                    ?? http.Request.Headers[HubProtocol.DeviceIdHeader].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(token))
        {
            throw HubException.AuthRequired("缺少设备访问凭证。");
        }

        var device = await store.GetDeviceByTokenAsync(token, http.RequestAborted);
        if (device is null)
        {
            throw new HubException(HubErrorCodes.DeviceUnknown, "设备未注册或凭证已失效，请重新注册。", 401);
        }

        if (device.Revoked)
        {
            throw new HubException(HubErrorCodes.DeviceRevoked, "该设备已被管理员停用。", 403);
        }

        http.SetDevice(device);
        return await next(context);
    }
}
