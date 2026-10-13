namespace ControlHub.Protocol;

/// <summary>
/// 集控系统协议常量。A 端（服务器）与 B 端（ClassIsland 插件）共同引用，
/// 任何协议层面的魔法数字/字符串都应集中定义在这里，避免两端漂移。
/// </summary>
public static class HubProtocol
{
    /// <summary>协议主版本。不兼容变更时才递增。</summary>
    public const int MajorVersion = 1;

    /// <summary>协议次版本。向后兼容的新增字段递增。</summary>
    public const int MinorVersion = 0;

    /// <summary>完整协议版本字符串，形如 <c>1.0</c>。</summary>
    public static string Version => $"{MajorVersion}.{MinorVersion}";

    /// <summary>集控系统产品版本号（三段式语义化版本，形如 <c>1.0.0</c>）。A/B 两端与界面展示共用。</summary>
    public const string ProductVersion = "1.4.0";

    /// <summary>HTTP API 根路径。</summary>
    public const string ApiPrefix = "/api/v1";

    /// <summary>服务端默认监听端口（HTTP）。</summary>
    public const int DefaultHttpPort = 29800;

    /// <summary>局域网自动发现所用的 UDP 广播端口。</summary>
    public const int DefaultDiscoveryPort = 29810;

    /// <summary>发现服务应答中用于标识本协议的魔数。</summary>
    public const string DiscoveryMagic = "ClassIsland.ControlHub";

    /// <summary>服务端标识名，出现在发现应答与 User-Agent 中。</summary>
    public const string ProductName = "ClassIsland.ControlHub";

    /// <summary>客户端设备令牌的 HTTP 鉴权方案名：<c>Authorization: HubDevice &lt;token&gt;</c></summary>
    public const string DeviceScheme = "HubDevice";

    /// <summary>管理员令牌的 HTTP 鉴权方案名：<c>Authorization: Bearer &lt;token&gt;</c></summary>
    public const string AdminScheme = "Bearer";

    /// <summary>设备上报机器指纹的请求头。</summary>
    public const string DeviceIdHeader = "X-Hub-Device-Id";

    /// <summary>客户端上报的插件版本请求头。</summary>
    public const string ClientVersionHeader = "X-Hub-Client-Version";

    /// <summary>默认心跳间隔（秒）。服务端可在心跳应答中动态调整。</summary>
    public const int DefaultHeartbeatSeconds = 30;

    /// <summary>默认长轮询挂起时长（秒）。</summary>
    public const int DefaultLongPollSeconds = 25;

    /// <summary>注册码默认字符集（去掉了易混淆的 0/O/1/I）。</summary>
    public const string EnrollCodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>注册码默认长度。</summary>
    public const int EnrollCodeLength = 8;

    /// <summary>同步内容分区名。客户端可按分区选择性应用。</summary>
    public static class Sections
    {
        /// <summary>时间表（作息时间）。</summary>
        public const string TimeLayouts = "timeLayouts";

        /// <summary>课表。</summary>
        public const string ClassPlans = "classPlans";

        /// <summary>科目。</summary>
        public const string Subjects = "subjects";

        /// <summary>档案设置（自定义配置项）。</summary>
        public const string Settings = "settings";

        /// <summary>全部可用分区。</summary>
        public static readonly string[] All = [TimeLayouts, ClassPlans, Subjects, Settings];
    }
}

/// <summary>
/// 协议错误码。客户端应依据这些稳定代码做分支处理，而不是解析错误文本。
/// </summary>
public static class HubErrorCodes
{
    /// <summary>请求未携带凭证。</summary>
    public const string AuthRequired = "AUTH_REQUIRED";

    /// <summary>凭证无效或已过期。</summary>
    public const string AuthInvalid = "AUTH_INVALID";

    /// <summary>设备未注册。</summary>
    public const string DeviceUnknown = "DEVICE_UNKNOWN";

    /// <summary>设备已被管理员吊销。</summary>
    public const string DeviceRevoked = "DEVICE_REVOKED";

    /// <summary>注册码错误。</summary>
    public const string EnrollCodeInvalid = "ENROLL_CODE_INVALID";

    /// <summary>注册码已过期或已被使用完毕。</summary>
    public const string EnrollCodeExpired = "ENROLL_CODE_EXPIRED";

    /// <summary>已登录，但当前账号没有执行该操作所需的权限。</summary>
    public const string PermissionDenied = "PERMISSION_DENIED";

    /// <summary>请求参数校验失败。</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>资源不存在。</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>资源冲突（例如唯一约束）。</summary>
    public const string Conflict = "CONFLICT";

    /// <summary>请求过于频繁。</summary>
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>来源地址不在管理端允许列表内（ControlHub:AdminIpAllowList）。</summary>
    public const string IpNotAllowed = "IP_NOT_ALLOWED";

    /// <summary>账号因连续登录失败被临时锁定。</summary>
    public const string AccountLocked = "ACCOUNT_LOCKED";

    /// <summary>协议版本不受支持。</summary>
    public const string ProtocolUnsupported = "PROTOCOL_UNSUPPORTED";

    /// <summary>服务端内部错误。</summary>
    public const string Internal = "INTERNAL";
}

/// <summary>
/// 教室端执行远程指令失败时的**原因分类**（B 端上报，A 端只展示不判状态）。
/// </summary>
/// <remarks>
/// <para>为什么单独一组、不并进 <see cref="HubErrorCodes"/>：那一组描述的是「**管理端接口**为什么拒绝了这次请求」，
/// 会直接映射成 HTTP 状态码；这一组描述的是「**教室端**为什么没做成」，取值来自设备上报、语言上也不带状态码语义。
/// 混在一起以后，改一处很容易影响到另一处。</para>
/// <para>码值风格与 <see cref="HubErrorCodes"/> 一致（UPPER_SNAKE_CASE）。新增码时**必须同步**
/// 前端 <c>wwwroot/js/core/error-hints.js</c> 的映射，否则界面上只会退化成显示上报的原始文案
/// （不会报错，但也帮不上用户）。</para>
/// <para>与命令的「退出码」是两件事：退出码来自被调起的进程（shell 类才有意义），
/// 这里回答的是「为什么没做成」——教室端认得，且不随宿主机环境变化。</para>
/// </remarks>
public static class CommandErrorCodes
{
    /// <summary>
    /// 教室端不认识这个指令类型，或**不支持其中的某项配置**——多半是教室端插件 / 宿主版本低于管理端。
    /// </summary>
    public const string Unsupported = "COMMAND_UNSUPPORTED";

    /// <summary>载荷缺字段或格式不对，教室端拒绝执行。</summary>
    public const string PayloadInvalid = "COMMAND_PAYLOAD_INVALID";

    /// <summary>指令指向的资源不存在（例如要操作的插件目录找不到）。</summary>
    public const string TargetNotFound = "COMMAND_TARGET_NOT_FOUND";

    /// <summary>当前 ClassIsland 未提供所需接口：宿主版本过低、接口签名不符，或服务尚未就绪。</summary>
    public const string HostApiUnavailable = "HOST_API_UNAVAILABLE";

    /// <summary>这台教室端的平台不支持该操作（例如电源管理只在 Windows 上可用）。</summary>
    public const string PlatformUnsupported = "PLATFORM_UNSUPPORTED";

    /// <summary>教室端自己关掉了该能力（例如插件设置里的「远程协助与隐私」关了截图）。</summary>
    public const string DisabledByPolicy = "COMMAND_DISABLED_BY_POLICY";

    /// <summary>执行过程中失败：外部程序报错、系统拒绝等，具体原因看文案。</summary>
    public const string ExecutionFailed = "COMMAND_EXECUTION_FAILED";

    /// <summary>
    /// 取回的插件包**完整性校验没过**（SHA256 与 A 端记录的不一致）。
    /// <para>与「执行失败」分开：这多半是传输途中损坏或 A 端缓存被改过，正确处置是**重新缓存该包**再下发，
    /// 而不是去教室机上排查。</para>
    /// </summary>
    public const string IntegrityFailed = "COMMAND_INTEGRITY_FAILED";
}

/// <summary>
/// 设备运行状态，用于心跳上报与 Web 端展示。
/// </summary>
public static class DeviceStates
{
    /// <summary>空闲（已连接、无同步任务）。</summary>
    public const string Idle = "idle";

    /// <summary>正在同步。</summary>
    public const string Syncing = "syncing";

    /// <summary>同步/应用出错。</summary>
    public const string Error = "error";

    /// <summary>长时间无心跳，由服务端推断。</summary>
    public const string Offline = "offline";
}

/// <summary>
/// 一次配置应用的结果。
/// </summary>
public static class ApplyResults
{
    /// <summary>全部成功。</summary>
    public const string Success = "success";

    /// <summary>部分分区成功。</summary>
    public const string Partial = "partial";

    /// <summary>失败。</summary>
    public const string Failed = "failed";
}
