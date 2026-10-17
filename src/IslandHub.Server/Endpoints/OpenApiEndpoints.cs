using System.Text.Json.Nodes;
using IslandHub.Protocol;
using IslandHub.Server.Http;
using IslandHub.Server.Options;
using IslandHub.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace IslandHub.Server.Endpoints;

/// <summary>
/// 机器可读的接口规范（OpenAPI 3.1）—— 路线图 #95。
///
/// <para>
/// 为什么要有：<c>docs/api.md</c> 是**给人读**的，集成方要的是能直接吃进 Postman / Apifox / 代码生成器的规范。
/// 规范从**运行时端点表**生成，所以不会出现「加了接口忘了写文档」——新路由自动出现在里面。
/// </para>
///
/// <para>
/// 对外暴露面刻意收窄：规范等价于「全部接口 + 每个接口要什么权限」的清单，因此挂在管理端分组下
/// （要登录、且需要 <c>settings.read</c>），还能用 <c>ControlHub:EnableOpenApi=false</c> 整体关掉。
/// 另外它会**标注每个接口的鉴权方式**（管理端会话 / 设备令牌 / 本机外壳 / 监控令牌），
/// 于是「这条接口到底谁能调」不再需要翻代码。
/// </para>
/// </summary>
public static class OpenApiEndpoints
{
    /// <summary>文档名（决定 URL 里的文件名：<c>/api/v1/admin/openapi/v1.json</c>）。</summary>
    public const string DocumentName = "v1";

    /// <summary>安全方案：管理端会话令牌（<c>Authorization: Bearer &lt;token&gt;</c>）。</summary>
    public const string AdminScheme = "AdminBearer";

    /// <summary>安全方案：教室端设备令牌（<c>Authorization: HubDevice &lt;token&gt;</c>）。</summary>
    public const string DeviceScheme = "DeviceToken";

    /// <summary>安全方案：桌面外壳的本机信任令牌。</summary>
    public const string LocalShellScheme = "LocalShell";

    /// <summary>安全方案：监控抓取令牌（<c>/metrics</c>）。</summary>
    public const string MetricsScheme = "MetricsToken";

    /// <summary>把规范生成器注册进容器（文档级信息与逐接口标注由两个转换器补上）。</summary>
    public static IServiceCollection AddHubOpenApi(this IServiceCollection services, ServerOptions options)
    {
        services.AddOpenApi(DocumentName, openApi =>
        {
            openApi.AddDocumentTransformer(new HubDocumentTransformer(options));
            openApi.AddOperationTransformer(new HubOperationTransformer());
        });
        return services;
    }

    /// <summary>挂载规范端点（管理端分组下，复用登录与权限两个过滤器）。</summary>
    public static void MapOpenApiEndpoints(this IEndpointRouteBuilder app, ServerOptions options)
    {
        if (!options.EnableOpenApi)
        {
            app.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(nameof(OpenApiEndpoints))
                .LogInformation("已关闭接口规范（ControlHub:EnableOpenApi=false）。");
            return;
        }

        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapOpenApi($"/openapi/{DocumentName}.json")
            .WithMetadata(new RequiredPermissionAttribute(PermissionKeys.SettingsRead));
    }

    /// <summary>文档级信息：标题、版本、鉴权方案与分组说明。</summary>
    private sealed class HubDocumentTransformer(ServerOptions options) : IOpenApiDocumentTransformer
    {
        public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
            CancellationToken cancellationToken)
        {
            document.Info = new OpenApiInfo
            {
                Title = $"{HubProtocol.ProductName} 接口",
                Version = HubProtocol.ProductVersion,
                Description = $"""
                    服务器：{options.ServerName}

                    - 管理端接口都在 `/api/v1/admin/*`：先 `POST /api/v1/admin/login` 拿会话令牌，
                      之后带 `Authorization: Bearer <令牌>`；每条接口还需要对应的权限（见操作上的 `x-permission`）。
                    - 教室端接口在 `/api/v1/client/*`：带 `Authorization: HubDevice <设备令牌>`。
                    - 桌面外壳免登录通道在 `/api/v1/local/*`：带 `X-Hub-Local-Shell: <令牌>`（或首次导航用 `?shellToken=`）。
                    - `GET /metrics` 是给采集器的：配了 `ControlHub:MetricsToken` 就必须带令牌，没配则只允许本机访问。
                    - 未标注安全要求（既不是 `x-public: true`、也没有 security）的接口属于「靠中间件兜底」，
                      集成方不要依赖它们——这是回归检查项，出现即视为漏标注。

                    权限键与角色模板见管理端「账号」页；更细的字段说明与调用示例见仓库 `docs/api.md`。
                    """,
            };

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[AdminScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "管理端会话令牌：`POST /api/v1/admin/login` 的返回，放在 `Authorization: Bearer <令牌>`。",
            };
            document.Components.SecuritySchemes[DeviceScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "Authorization",
                Description = "教室端设备令牌：`Authorization: HubDevice <设备令牌>`（注册时一次性下发）。",
            };
            document.Components.SecuritySchemes[LocalShellScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = LocalShellTrust.HeaderName,
                Description = "桌面外壳的本机信任令牌：读数据目录下的 `local-shell.token` 并原样带上。",
            };
            document.Components.SecuritySchemes[MetricsScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "Authorization",
                Description = "监控抓取令牌：`ControlHub:MetricsToken` 的值，`Authorization: Bearer <令牌>` 或 `?token=`；没配令牌时只允许本机。",
            };

            document.Tags = new HashSet<OpenApiTag>
            {
                new() { Name = "admin", Description = "管理端：需要登录，且按接口声明校验权限。" },
                new() { Name = "client", Description = "教室端：设备令牌鉴权（注册 / 心跳 / 取配置与指令）。" },
                new() { Name = "public", Description = "公开：无需鉴权（存活探测、登录、注册码换取设备令牌等）。" },
            };

            return Task.CompletedTask;
        }
    }

    /// <summary>逐接口标注：鉴权方案、是否需要登录、以及所需的权限键。</summary>
    private sealed class HubOperationTransformer : IOpenApiOperationTransformer
    {
        public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
            CancellationToken cancellationToken)
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            var path = "/" + (context.Description.RelativePath ?? string.Empty).TrimStart('/');

            operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();

            // 权限键：管理端路由用 RequirePermission 声明过，直接抄出来（集成方据此判断要申请哪些权限）。
            var permission = metadata.OfType<RequiredPermissionAttribute>().FirstOrDefault()?.Permission;
            if (!string.IsNullOrEmpty(permission))
            {
                operation.Extensions["x-permission"] = new JsonNodeExtension(JsonValue.Create(permission)!);
            }

            // /metrics 在代码里是 AllowAnonymous（鉴权在处理器内部：令牌或仅本机），
            // 这里如实标成「需要监控令牌」，别让集成方以为它完全公开。
            if (path == "/metrics")
            {
                operation.Security = [Requirement(MetricsScheme, context)];
                operation.Extensions["x-note"] = new JsonNodeExtension(
                    JsonValue.Create("配了 ControlHub:MetricsToken 时必须带令牌；没配则只允许本机访问。")!);
                return Task.CompletedTask;
            }

            if (metadata.OfType<IAllowAnonymous>().Any())
            {
                operation.Security = [];
                operation.Extensions["x-public"] = new JsonNodeExtension(JsonValue.Create(true)!);
                return Task.CompletedTask;
            }

            var scheme = path switch
            {
                _ when path.StartsWith("/api/v1/admin", StringComparison.Ordinal) => AdminScheme,
                _ when path.StartsWith("/api/v1/client", StringComparison.Ordinal) => DeviceScheme,
                _ when path.StartsWith("/api/v1/local", StringComparison.Ordinal) => LocalShellScheme,
                _ => null,
            };

            if (scheme is not null)
            {
                operation.Security = [Requirement(scheme, context)];
            }

            // 既不公开、也不属于已知分组（靠中间件兜底）：**故意不写 security**，
            // 由端到端脚本把这类接口列出来——「谁能调这条接口」必须写在规范里，不能靠猜。
            return Task.CompletedTask;
        }

        private static OpenApiSecurityRequirement Requirement(string scheme, OpenApiOperationTransformerContext context) =>
            new() { [new OpenApiSecuritySchemeReference(scheme, context.Document)] = [] };
    }
}
