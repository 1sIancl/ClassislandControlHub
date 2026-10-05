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

// 备份解密（#60）必须在启动 Web 服务之前处理：这一步的典型场景是**灾难恢复 / 换机器**，
// 那时服务端往往跑不起来（库损坏、环境没配好），而备份必须能解出来。
// 注意用 Environment.Exit 而不是 return：top-level 里 `return 值` 会被推断成「入口返回 int」，
// 编译器随后会要求所有代码路径都返回值。
if (BackupTools.TryRunCommandLine(args, out var cliExitCode))
{
    Environment.Exit(cliExitCode);
}

// 以可执行文件所在目录作为内容根，这样无论从哪个工作目录启动，
// appsettings.json 与 wwwroot 都能被正确定位（便于做成开机自启的服务）。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// ────────────────────────────── 进程运行状态 ──────────────────────────────
// 记录服务端自身的启动时刻，供 /server/info 的「运行时长 / 启动时间」使用。
// 注意这不是系统开机时长（Environment.TickCount64），重启服务端必须归零。
ServerRuntime.Init();

// ────────────────────────────── 配置 ──────────────────────────────
// 以 Windows 服务方式运行时接入服务生命周期（普通控制台/Linux 运行时空操作）。
// 这样 `sc.exe create ... binPath=<发布目录>/ControlHub.Server.exe` 注册的服务才能正常响应 SCM。
builder.Host.UseWindowsService(options => options.ServiceName = "ClassislandControlHub");

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));
var serverOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>()
                    ?? new ServerOptions();

// 环境变量只能给标量值：`ControlHub__AdminIpAllowList=192.168.1.0/24` **不会**被绑定到 string[]，
// 结果是「明明配了却全部放行」——这比不配更危险（实测踩到：局域网来源照样能访问管理端）。
// 这里把标量 / 逗号分隔写法补回数组，让环境变量与 appsettings.json 两种写法都能用。
var allowListRaw = builder.Configuration[$"{ServerOptions.SectionName}:AdminIpAllowList"];
if (!string.IsNullOrWhiteSpace(allowListRaw) && serverOptions.AdminIpAllowList.Length == 0)
{
    serverOptions.AdminIpAllowList = SplitAllowList(allowListRaw);
}

builder.Services.PostConfigure<ServerOptions>(options =>
{
    if (!string.IsNullOrWhiteSpace(allowListRaw) && options.AdminIpAllowList.Length == 0)
    {
        options.AdminIpAllowList = SplitAllowList(allowListRaw);
    }
});

static string[] SplitAllowList(string raw) => raw
    .Split([',', ';', ' ', '\n', '\r', '\t'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
            serverOptions.CertificatePath,
            serverOptions.CertificatePassword,
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
    return new HubStore(options.ResolveDatabasePath(environment.ContentRootPath),
        sp.GetRequiredService<SecretProtector>());
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
// 本地外壳（桌面端）信任通道：仅回环地址 + 外壳令牌可免登录。
builder.Services.AddSingleton<LocalShellTrust>();

// 单个后台服务出错不应该把整个服务端拖垮（例如同机已有实例占着 UDP 发现端口）：
// 各后台服务自己负责降级并记录原因，HTTP 服务继续可用。
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});
builder.Services.AddSingleton<NtpClient>();
builder.Services.AddSingleton<ServerTimeService>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<AiTimetableService>();

// 静态敏感数据加密（#36，见 docs/adr/0005）：设备注册码、管理端邀请码、Webhook 加签密钥
// 以密文落库（这几种值必须能还原，因此不能用哈希）。读取兼容明文，因此老库无需停机迁移。
// 密钥文件路径留空 = 数据目录下的 secrets.key；可用 ControlHub:SecretsKeyPath 指到别处，
// 但**不要**把它和数据库放进同一个会被外发的备份包里（ADR 0005「密钥管理」）。
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
    var environment = sp.GetRequiredService<IHostEnvironment>();
    var keyPath = string.IsNullOrWhiteSpace(options.SecretsKeyPath)
        ? Path.Combine(options.ResolveDataDirectory(environment.ContentRootPath), "secrets.key")
        : options.SecretsKeyPath.Trim();
    return new SecretProtector(keyPath);
});
// 定时提醒：ReminderService 负责推算触发时间与投递，ReminderScheduler 每 15 秒检查一次。
builder.Services.AddSingleton<ReminderService>();
builder.Services.AddHostedService<DiscoveryService>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddHostedService<NtpServer>();
builder.Services.AddHostedService<AutoBackupService>();
builder.Services.AddHostedService<ReminderScheduler>();
// 临时换课：跨天时递增一次版本号，让教室重新拉取「今天该上的课」。
builder.Services.AddHostedService<TimetableOverrideScheduler>();
// Webhook 外部通知：设备掉线/恢复、同步失败、指令失败推送到企业微信、钉钉、飞书或自定义端点。
// WebhookService 既是「可被端点注入的单例」，也是「后台投递服务」：投递走队列并带重试，
// 因此慢的 / 挂起的接收端不会拖慢设备心跳与指令回报。
builder.Services.AddHttpClient("webhook");
builder.Services.AddSingleton<WebhookService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookService>());
builder.Services.AddHostedService<DeviceWatchService>();
// 在线率采样（#63）：last_seen_at 是瞬时值，不做采样就没有「上周在线率」这种数据。
builder.Services.AddHostedService<StatsService>();
// ServerTimeService 同时是「可被端点注入的单例」与「后台授时服务」，用工厂引用同一实例。
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerTimeService>());
// UpdateService 同理：单例 + 后台自动检查，用工厂引用同一实例。
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());

builder.Services.AddProblemDetails();

if (serverOptions.CorsAllowedOrigins.Length > 0)
{
    builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
        .WithOrigins(serverOptions.CorsAllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

// 结构化（JSON）日志：给日志平台按字段检索用。默认保持人类友好的文本日志——现场排查时更可读。
if (serverOptions.LogJson)
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
}

var app = builder.Build();

// ────────────────────────────── 初始化 ──────────────────────────────
await using (var scope = app.Services.CreateAsyncScope())
{
    var store = scope.ServiceProvider.GetRequiredService<HubStore>();
    await store.InitializeAsync();

    // 静态敏感数据加密（#36）：把老库里的明文注册码 / 邀请码 / Webhook 密钥就地升级为密文（幂等）。
    var migration = await store.MigrateSecretsAsync();
    if (migration.Changed || migration.AuditReferencesScrubbed > 0)
    {
        app.Logger.LogInformation(
            "静态敏感数据迁移完成：注册码新加密 {Enroll} 条（补指纹 {EnrollHash} 条），"
            + "邀请码新加密 {Register} 条（补指纹 {RegisterHash} 条），Webhook 密钥新加密 {Hooks} 条，"
            + "清理审计中的明文引用 {Audit} 处。",
            migration.EnrollCodesEncrypted, migration.EnrollCodesFingerprinted,
            migration.RegisterCodesEncrypted, migration.RegisterCodesFingerprinted,
            migration.WebhooksEncrypted, migration.AuditReferencesScrubbed);
    }

    foreach (var error in migration.Errors)
    {
        app.Logger.LogWarning("静态敏感数据迁移失败（已跳过该条，不阻断启动）：{Error}", error);
    }

    if (migration.Unrecoverable > 0)
    {
        app.Logger.LogWarning(
            "静态敏感数据：{Count} 条记录已是密文但缺少查找指纹，明文不可得、无法自动修复（请重新生成）。",
            migration.Unrecoverable);
    }

    // 打印加密覆盖率与密钥文件位置：现场最需要核对「到底加密了没有」「密钥在哪里」。
    var secretStats = await store.GetSecretEncryptionStatsAsync();
    app.Logger.LogInformation(
        "静态敏感数据：注册码 {EnrollEncrypted}/{EnrollTotal} 已加密，邀请码 {RegisterEncrypted}/{RegisterTotal} 已加密，"
        + "Webhook 密钥 {HookEncrypted}/{HookTotal} 已加密（密钥文件：{KeyFile}，本次新建：{KeyGenerated}）",
        secretStats.EnrollCodes.Encrypted, secretStats.EnrollCodes.Total,
        secretStats.RegisterCodes.Encrypted, secretStats.RegisterCodes.Total,
        secretStats.Webhooks.Encrypted, secretStats.Webhooks.Total,
        secretStats.KeyFile, secretStats.KeyGenerated);

    if (secretStats.HasUnavailable)
    {
        app.Logger.LogWarning(
            "静态敏感数据：存在无法解密的值（密钥文件已更换或丢失）——注册码 {Enroll} 条、邀请码 {Register} 条、"
            + "Webhook 密钥 {Hooks} 条不可用，需重新生成 / 重新填写。",
            secretStats.EnrollCodes.Unavailable, secretStats.RegisterCodes.Unavailable,
            secretStats.Webhooks.Unavailable);
    }

    var auth = scope.ServiceProvider.GetRequiredService<AdminAuthService>();
    await auth.EnsureSeedAdminAsync();

    var options = scope.ServiceProvider.GetRequiredService<IOptions<ServerOptions>>().Value;
    var dataDirectory = options.ResolveDataDirectory(app.Environment.ContentRootPath);
    app.Logger.LogInformation("集控服务器已就绪。数据目录：{Directory}", dataDirectory);

    // 启动时说清安全相关的开关状态：这类配置「以为配上了其实没生效」是最危险的。
    app.Logger.LogInformation("管理端来源限制：{AllowList}",
        serverOptions.AdminIpAllowList.Length == 0
            ? "未配置（不限制来源）"
            : string.Join(", ", serverOptions.AdminIpAllowList));
    app.Logger.LogInformation("安全响应头：{Headers}；HTTPS 强制跳转：{Redirect}；API 访问日志：{AccessLog}",
        serverOptions.SecurityHeadersEnabled ? "开启" : "关闭",
        serverOptions.RequireHttpsRedirect ? "开启" : "关闭",
        serverOptions.AccessLogEnabled ? "开启" : "关闭");
}

// ────────────────────────────── 中间件 ──────────────────────────────
// 访问日志放在**最外层**：HubExceptionMiddleware 会把业务异常改写成 4xx/5xx，
// 只有包在它外面，日志里的状态码才是客户端真正看到的那一个。
app.UseMiddleware<ApiAccessLogMiddleware>();

app.UseMiddleware<HubExceptionMiddleware>();

// 安全响应头（CSP / X-Frame-Options / nosniff / Referrer-Policy 等）
if (serverOptions.SecurityHeadersEnabled)
{
    app.UseMiddleware<SecurityHeadersMiddleware>();
}

// HTTPS 强制跳转（默认关闭；回环地址永不跳转——桌面外壳的本机免登录依赖 http://127.0.0.1）
if (serverOptions.RequireHttpsRedirect)
{
    app.UseMiddleware<HttpsRedirectMiddleware>();
}

// 管理端来源限制（ControlHub:AdminIpAllowList；留空 = 不限制，教室端接口不受影响）
app.UseMiddleware<AdminIpAllowListMiddleware>();

// 「我明明配了却没生效」的第一现场证据：启动时把「配置项数」与「解析出的规则数」都打出来。
// 两者不一致时能立刻定位是配置写法问题还是规则格式问题。
app.Logger.LogInformation("管理端来源允许列表：配置 {Configured} 项，解析出 {Rules} 条有效规则（0 条 = 不限制）。",
    serverOptions.AdminIpAllowList.Length,
    AdminIpAllowListMiddleware.ParseRanges(serverOptions.AdminIpAllowList).Count);

if (serverOptions.CorsAllowedOrigins.Length > 0)
{
    app.UseCors();
}

// 本地外壳信任通道：准备令牌文件（已存在则复用），供桌面外壳免登录使用。
app.Services.GetRequiredService<LocalShellTrust>().Initialize();

app.UseDefaultFiles();
app.UseStaticFiles();

// ────────────────────────────── 路由 ──────────────────────────────
app.MapGet("/api/v1/ping", () => ApiResult<object>.Success(new
{
    server = serverOptions.ServerName,
    protocol = HubProtocol.Version,
    time = DateTimeOffset.UtcNow,
})).AllowAnonymous().WithTags("public");

app.MapLocalShellEndpoints();
app.MapClientEndpoints();
app.MapAdminEndpoints();
app.MapDeviceEndpoints();
app.MapDeviceCsvEndpoints();
app.MapSearchEndpoints();
app.MapReportEndpoints();
app.MapTagEndpoints();
app.MapMetricsEndpoints();
app.MapApiKeyEndpoints();
app.MapProfileEndpoints();
app.MapRemoteEndpoints();
app.MapBackupEndpoints();
app.MapAiEndpoints();
app.MapReminderEndpoints();
app.MapRegistrationEndpoints();
app.MapNoticeTemplateEndpoints();
app.MapTimetableOverrideEndpoints();
app.MapWebhookEndpoints();

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
