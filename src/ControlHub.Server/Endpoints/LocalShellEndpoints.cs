using ControlHub.Protocol;
using ControlHub.Server.Services;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 本地外壳（桌面端）配套接口：健康探针与免登录引导。
/// <para>两者都不需要登录：健康探针只回运行状态，引导接口只认「回环地址 + 外壳令牌」。</para>
/// </summary>
public static class LocalShellEndpoints
{
    /// <summary>注册相关路由。</summary>
    public static void MapLocalShellEndpoints(this IEndpointRouteBuilder app)
    {
        // 健康探针：桌面外壳据此轮询「A 端是否已就绪」，也可给任何监控系统使用。
        app.MapGet("/api/health", () => ApiResult<object>.Success(new
        {
            status = "healthy",
            version = HubProtocol.ProductVersion,
            protocol = HubProtocol.Version,
            uptimeSeconds = (long)ServerRuntime.Uptime.TotalSeconds,
            startedAt = ServerRuntime.StartedAt,
            time = DateTimeOffset.UtcNow,
        })).AllowAnonymous().WithTags("public");

        // 本地外壳免登录引导：校验通过后种 HttpOnly Cookie 并跳回首页（令牌不留在地址栏里）。
        app.MapGet($"{HubProtocol.ApiPrefix}/local-shell", BootstrapAsync)
            .AllowAnonymous()
            .WithTags("local");

        // 外壳退出时用它优雅停服；失败时外壳会退回强杀整棵进程树。
        app.MapPost($"{HubProtocol.ApiPrefix}/local-shell/shutdown", ShutdownAsync)
            .AllowAnonymous()
            .WithTags("local");
    }

    private static IResult ShutdownAsync(HttpContext http, LocalShellTrust trust, IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory)
    {
        if (!trust.TryAuthenticate(http, out _))
        {
            return Results.Json(
                ApiResult<bool>.Failure(HubErrorCodes.AuthInvalid,
                    "本地外壳令牌无效，或请求不是来自本机。"),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        loggerFactory.CreateLogger("ControlHub.Server.LocalShell")
            .LogInformation("收到本地外壳的停机请求，正在优雅退出。");

        // 先把响应发出去再停机，否则外壳拿到的是连接中断而不是 200。
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            lifetime.StopApplication();
        });

        return Results.Json(ApiResult<bool>.Success(true));
    }

    private static IResult BootstrapAsync(HttpContext http, LocalShellTrust trust)
    {
        if (!trust.TryAuthenticate(http, out _))
        {
            return Results.Json(
                ApiResult<bool>.Failure(HubErrorCodes.AuthInvalid,
                    "本地外壳令牌无效，或请求不是来自本机。"),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        http.Response.Cookies.Append(LocalShellTrust.CookieName, trust.CookieValue, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            // 回环地址通常是明文 HTTP，设 Secure 会导致 Cookie 根本发不出去。
            Secure = http.Request.IsHttps,
        });

        return Results.Redirect("/");
    }
}
