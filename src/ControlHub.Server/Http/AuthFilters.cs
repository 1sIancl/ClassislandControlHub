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
public sealed class AdminAuthFilter(AdminAuthService authService) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var token = http.ReadToken(HubProtocol.AdminScheme);
        var session = await authService.ValidateAsync(token, http.RequestAborted);
        if (session is null)
        {
            throw HubException.AuthInvalid("登录状态已失效，请重新登录。");
        }

        http.SetAdminSession(session);
        return await next(context);
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
