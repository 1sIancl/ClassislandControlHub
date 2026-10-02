using ControlHub.Protocol;
using ControlHub.Server.Data;
using ControlHub.Server.Options;
using ControlHub.Server.Services;
using Microsoft.Extensions.Options;

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

        // 分级健康检查（#86）——三种用途不同，别混用：
        //   live  ：进程活着就 200。**不查数据库**，避免数据库抖动导致进程被存活探针杀掉。
        //   ready ：数据库可读（能算出设备数与版本号），给反代 / 负载均衡做就绪判定。
        //   deep  ：追加数据目录可写、磁盘余量与设备统计，给人工排查与外部监控用。
        app.MapGet("/api/health/live", () => Results.Json(ApiResult<object>.Success(new
        {
            status = "live",
            version = HubProtocol.ProductVersion,
            protocol = HubProtocol.Version,
            uptimeSeconds = (long)ServerRuntime.Uptime.TotalSeconds,
        }))).AllowAnonymous().WithTags("public");

        app.MapGet("/api/health/ready",
            async (HubStore store, CancellationToken cancellationToken) =>
            {
                try
                {
                    var devices = await store.GetDevicesAsync(cancellationToken);
                    return Results.Json(ApiResult<object>.Success(new
                    {
                        status = "ready",
                        devices = devices.Count,
                        revision = await store.GetRevisionAsync(cancellationToken),
                    }));
                }
                catch (Exception ex)
                {
                    // 就绪失败要返回 503，否则反代会继续把流量送进来。
                    return Results.Json(
                        ApiResult<object>.Failure(HubErrorCodes.Internal, $"数据库不可用：{ex.Message}"),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }).AllowAnonymous().WithTags("public");

        app.MapGet("/api/health/deep",
            async (HubStore store, IOptions<ServerOptions> options, IHostEnvironment environment,
                CancellationToken cancellationToken) =>
            {
                var settings = options.Value;
                var dataDirectory = settings.ResolveDataDirectory(environment.ContentRootPath);

                bool writable;
                try
                {
                    var probe = Path.Combine(dataDirectory, ".health-probe");
                    await File.WriteAllTextAsync(probe, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
                    File.Delete(probe);
                    writable = true;
                }
                catch
                {
                    writable = false;
                }

                long freeMb = 0;
                long totalMb = 0;
                try
                {
                    var root = Path.GetPathRoot(Path.GetFullPath(dataDirectory));
                    if (!string.IsNullOrEmpty(root))
                    {
                        var drive = new DriveInfo(root);
                        freeMb = drive.AvailableFreeSpace / 1024 / 1024;
                        totalMb = drive.TotalSize / 1024 / 1024;
                    }
                }
                catch
                {
                    // 取不到磁盘信息不影响健康判定，留 0 即可。
                }

                var devices = await store.GetDevicesAsync(cancellationToken);
                var now = DateTimeOffset.UtcNow;
                var online = devices.Count(d => !d.Revoked && d.LastSeenAt.HasValue
                    && (now - d.LastSeenAt.Value).TotalSeconds < settings.OnlineTimeoutSeconds);
                var revision = await store.GetRevisionAsync(cancellationToken);

                return Results.Json(ApiResult<object>.Success(new
                {
                    status = writable ? "healthy" : "degraded",
                    version = HubProtocol.ProductVersion,
                    uptimeSeconds = (long)ServerRuntime.Uptime.TotalSeconds,
                    startedAt = ServerRuntime.StartedAt,
                    dataDirectory,
                    dataDirectoryWritable = writable,
                    diskFreeMb = freeMb,
                    diskTotalMb = totalMb,
                    deviceCount = devices.Count,
                    onlineDeviceCount = online,
                    pendingDeviceCount = devices.Count(d => !d.Revoked && d.AppliedRevision < revision),
                    revision,
                    time = now,
                }));
            }).AllowAnonymous().WithTags("public");

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
