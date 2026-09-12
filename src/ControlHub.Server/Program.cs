using System.Net;
using ControlHub.Protocol;
using ControlHub.Server.Data;
using ControlHub.Server.Endpoints;
using ControlHub.Server.Http;
using ControlHub.Server.Options;
using ControlHub.Server.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

// 以可执行文件所在目录作为内容根，这样无论从哪个工作目录启动，
// appsettings.json 与 wwwroot 都能被正确定位（便于做成开机自启的服务）。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// ────────────────────────────── 配置 ──────────────────────────────
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));
var serverOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>()
                    ?? new ServerOptions();

// ────────────────────────────── 序列化 ──────────────────────────────
// 两端共用同一套 JSON 约定（camelCase、保留中文、忽略 null）。
builder.Services.Configure<JsonOptions>(options =>
{
    var shared = HubJson.Create();
    options.SerializerOptions.PropertyNamingPolicy = shared.PropertyNamingPolicy;
    options.SerializerOptions.PropertyNameCaseInsensitive = shared.PropertyNameCaseInsensitive;
    options.SerializerOptions.DefaultIgnoreCondition = shared.DefaultIgnoreCondition;
    options.SerializerOptions.Encoder = shared.Encoder;
    options.SerializerOptions.NumberHandling = shared.NumberHandling;
});

// ────────────────────────────── 监听地址 ──────────────────────────────
builder.WebHost.ConfigureKestrel(kestrel =>
{
    var bindAddress = string.IsNullOrWhiteSpace(serverOptions.BindAddress)
        ? IPAddress.Any
        : IPAddress.TryParse(serverOptions.BindAddress, out var parsed) ? parsed : IPAddress.Any;

    kestrel.Listen(bindAddress, serverOptions.HttpPort);

    if (serverOptions.EnableHttps)
    {
        var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
        var certificate = CertificateProvider.LoadOrCreate(
            builder.Configuration["ControlHub:CertificatePath"],
            builder.Configuration["ControlHub:CertificatePassword"],
            serverOptions.ResolveDataDirectory(builder.Environment.ContentRootPath),
            serverOptions.ServerName,
            loggerFactory.CreateLogger("Certificate"));

        if (certificate is not null)
        {
            kestrel.Listen(bindAddress, serverOptions.HttpsPort, listen =>
                listen.UseHttps(certificate));
        }
    }
});

// ────────────────────────────── 依赖注入 ──────────────────────────────
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
    var environment = sp.GetRequiredService<IHostEnvironment>();
    return new HubStore(options.ResolveDatabasePath(environment.ContentRootPath));
});

builder.Services.AddSingleton(sp =>
{
    var store = sp.GetRequiredService<HubStore>();
    // 启动时同步一次版本号，保证长轮询的初始状态正确。
    var revision = store.GetRevisionAsync().GetAwaiter().GetResult();
    return new RevisionNotifier(revision);
});

builder.Services.AddSingleton<AdminAuthService>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<AdminAuthFilter>();
builder.Services.AddSingleton<DeviceAuthFilter>();
builder.Services.AddSingleton<NtpClient>();
builder.Services.AddSingleton<ServerTimeService>();
builder.Services.AddHostedService<DiscoveryService>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddHostedService<NtpServer>();
// ServerTimeService 同时是「可被端点注入的单例」与「后台授时服务」，用工厂引用同一实例。
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerTimeService>());

builder.Services.AddProblemDetails();

if (serverOptions.CorsAllowedOrigins.Length > 0)
{
    builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
        .WithOrigins(serverOptions.CorsAllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

var app = builder.Build();

// ────────────────────────────── 初始化 ──────────────────────────────
await using (var scope = app.Services.CreateAsyncScope())
{
    var store = scope.ServiceProvider.GetRequiredService<HubStore>();
    await store.InitializeAsync();

    var auth = scope.ServiceProvider.GetRequiredService<AdminAuthService>();
    await auth.EnsureSeedAdminAsync();

    var options = scope.ServiceProvider.GetRequiredService<IOptions<ServerOptions>>().Value;
    var dataDirectory = options.ResolveDataDirectory(app.Environment.ContentRootPath);
    app.Logger.LogInformation("集控服务器已就绪。数据目录：{Directory}", dataDirectory);
}

// ────────────────────────────── 中间件 ──────────────────────────────
app.UseMiddleware<HubExceptionMiddleware>();

if (serverOptions.CorsAllowedOrigins.Length > 0)
{
    app.UseCors();
}

app.UseDefaultFiles();
app.UseStaticFiles();

// ────────────────────────────── 路由 ──────────────────────────────
app.MapGet("/api/v1/ping", () => ApiResult<object>.Success(new
{
    server = serverOptions.ServerName,
    protocol = HubProtocol.Version,
    time = DateTimeOffset.UtcNow,
})).AllowAnonymous().WithTags("public");

app.MapClientEndpoints();
app.MapAdminEndpoints();
app.MapDeviceEndpoints();
app.MapProfileEndpoints();

// 未匹配到的 API 路径统一返回 JSON 404，而不是落到前端页面。
app.Map($"{HubProtocol.ApiPrefix}/{{**rest}}",
        () => Results.Json(
            ApiResult<object>.Failure(HubErrorCodes.NotFound, "请求的接口不存在。"),
            statusCode: StatusCodes.Status404NotFound,
            contentType: "application/json; charset=utf-8"))
    .AllowAnonymous();

// 其余路径交给前端单页应用（前端自行处理路由）。
app.MapFallbackToFile("index.html");

// ────────────────────────────── 启动提示 ──────────────────────────────
app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<IServer>().Features
        .Get<IServerAddressesFeature>()?.Addresses;

    app.Logger.LogInformation("======================================================");
    app.Logger.LogInformation("  {Name} 已启动", serverOptions.ServerName);
    app.Logger.LogInformation("  管理界面：{Urls}", addresses is null ? "见下方监听地址" : string.Join("  ", addresses));
    app.Logger.LogInformation("  协议版本：{Version}    局域网发现端口：{Port}",
        HubProtocol.Version, serverOptions.EnableDiscovery ? serverOptions.DiscoveryPort : -1);
    app.Logger.LogInformation("======================================================");
});

app.Run();
