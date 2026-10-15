namespace IslandHub.Protocol.Dtos;

/// <summary>
/// 插件市场条目（A 端缓存的 ClassIsland 官方插件索引里的一个插件）。
/// <para>索引原文里的下载地址带 <c>{root}</c> 占位符（镜像根），这里存的是**原样**的模板，
/// 展示时按所选镜像展开——否则换了镜像就得重新同步一次索引。</para>
/// </summary>
public sealed class PluginMarketItemDto
{
    /// <summary>插件 ID（manifest 里的 id）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>插件名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>索引中的最新版本（manifest 里的 version）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>插件要求的最低宿主 API 版本。<b>字符串</b>（宿主如此存储，比较前要 TryParse）。</summary>
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>作者。</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>说明。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>下载地址模板（含 <c>{root}</c>，已按当前镜像展开）。</summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>索引给出的包 MD5（上游完整性校验用）。</summary>
    public string DownloadMd5 { get; set; } = string.Empty;

    /// <summary>图标地址（已展开 <c>{root}</c>，指向上游）。界面**不要**直接用：那个地址指向 GitHub，
    /// 管理端浏览器在国内学校网络里多半加载不出来——用 <see cref="IconLocalUrl"/>。</summary>
    public string IconUrl { get; set; } = string.Empty;

    /// <summary>
    /// A 端代理的图标地址（免鉴权，可直接喂给 <c>&lt;img src&gt;</c>）；**空**表示这个插件的图标
    /// 还没缓存到本地，界面应退化成首字母头像。
    /// </summary>
    public string IconLocalUrl { get; set; } = string.Empty;

    /// <summary>自述文件地址（已展开 <c>{root}</c>，指向上游）。界面**不要**直接用，用
    /// <see cref="ReadmeLocalUrl"/>。</summary>
    public string ReadmeUrl { get; set; } = string.Empty;

    /// <summary>
    /// A 端代理的自述文件地址（免鉴权，返回 Markdown 原文）；**空**表示索引里这个插件没有自述文件。
    /// <para>首次访问会让 A 端去取一次（可能几秒），之后走本地缓存。</para>
    /// </summary>
    public string ReadmeLocalUrl { get; set; } = string.Empty;

    /// <summary>插件仓库地址。</summary>
    public string RepositoryUrl { get; set; } = string.Empty;

    /// <summary>
    /// 插件声明的支持平台（索引里通常为空 = 未声明 = 全平台）。
    /// <para>教室端一律是 Windows，因此只有「声明了支持平台且不含 Windows」时才拦下发。</para>
    /// </summary>
    public List<string> SupportedOSPlatforms { get; set; } = [];

    /// <summary>索引统计的下载数。</summary>
    public long DownloadCount { get; set; }

    /// <summary>索引统计的星标数。</summary>
    public long StarsCount { get; set; }

    /// <summary>索引里这一版是否已经缓存在 A 端。</summary>
    public bool Cached { get; set; }

    /// <summary>A 端已缓存的该插件版本列表（可能含比索引更旧或更新的版本）。</summary>
    public List<string> CachedVersions { get; set; } = [];

    /// <summary>已缓存包的 SHA256（未缓存时为空）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>已缓存包的字节数（未缓存时为 0）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>缓存时间（ISO 8601 文本，未缓存时为空）。</summary>
    public string CachedAt { get; set; } = string.Empty;
}

/// <summary>缓存一个插件包的请求。</summary>
public sealed class PluginCacheRequest
{
    /// <summary>插件 ID。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>版本；留空 = 索引里的最新版。</summary>
    public string? Version { get; set; }
}

/// <summary>A 端已缓存的插件包（<c>.cipx</c>）。</summary>
public sealed class PluginPackageDto
{
    /// <summary>插件 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>包文件名（下发时告诉教室端，便于出错时对照）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>SHA256（十六进制大写）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>字节数。</summary>
    public long SizeBytes { get; set; }

    /// <summary>下载该包所用的镜像名。</summary>
    public string SourceMirror { get; set; } = string.Empty;

    /// <summary>缓存时间（ISO 8601 文本）。</summary>
    public string CachedAt { get; set; } = string.Empty;
}

/// <summary>插件市场同步 / 缓存操作的结果。</summary>
public sealed class PluginMarketSyncResultDto
{
    /// <summary>是否成功。失败时其它字段可能为空，看 <see cref="Error"/>。</summary>
    public bool Ok { get; set; }

    /// <summary>实际取到索引的来源（索引 URL 或「镜像 + URL」）。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>同步到的插件条数。</summary>
    public int Count { get; set; }

    /// <summary>成功时 = 同步时刻；失败时 = 本次尝试时刻（ISO 8601 文本）。</summary>
    public string SyncedAt { get; set; } = string.Empty;

    /// <summary>失败原因（成功时为空）。</summary>
    public string? Error { get; set; }
}

/// <summary>插件市场设置（镜像、索引地址与同步状态）。</summary>
public sealed class PluginMarketSettingsDto
{
    /// <summary>索引地址模板（含 <c>{time}</c> 占位符，默认官方地址）。</summary>
    public string IndexUrl { get; set; } = string.Empty;

    /// <summary>首选镜像名（空 = 自动：按索引里的顺序依次尝试）。</summary>
    public string PreferredMirror { get; set; } = string.Empty;

    /// <summary>索引里给出的镜像表（名称 → 根地址）。</summary>
    public Dictionary<string, string> Mirrors { get; set; } = [];

    /// <summary>最近一次成功同步的时间（ISO 8601 文本）。</summary>
    public string LastSyncAt { get; set; } = string.Empty;

    /// <summary>最近一次同步失败的原因（成功时为空）。</summary>
    public string LastError { get; set; } = string.Empty;
}

/// <summary>修改插件市场设置。</summary>
public sealed class PluginMarketSettingsUpdateRequest
{
    /// <summary>索引地址模板（留空 = 不改；给 <c>-</c> = 恢复默认官方地址）。</summary>
    public string? IndexUrl { get; set; }

    /// <summary>首选镜像名（留空 = 不改；给 <c>-</c> = 改回「自动」）。</summary>
    public string? PreferredMirror { get; set; }
}

/// <summary>下发插件操作的请求（安装 / 卸载 / 启用 / 禁用）。</summary>
public sealed class PluginDeployRequest
{
    /// <summary>动作，取值见 <see cref="PluginDeployActions"/>。</summary>
    public string Action { get; set; } = PluginDeployActions.Install;

    /// <summary>插件 ID。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>版本（安装时必填；不填 = 用索引里的最新版）。</summary>
    public string? Version { get; set; }

    /// <summary>目标设备；为空 = 全部设备（受 <see cref="IncludeOffline"/> 约束）。</summary>
    public List<string>? DeviceIds { get; set; }

    /// <summary>是否包含离线设备（离线设备排队，上线后执行）。</summary>
    public bool IncludeOffline { get; set; }

    /// <summary>安装完成后是否顺带下发一条 <c>restart</c>（否则要管理员自己挑时间重启）。</summary>
    public bool RestartAfter { get; set; }
}

/// <summary>插件下发动作常量。</summary>
public static class PluginDeployActions
{
    /// <summary>安装（把 A 端缓存里的 <c>.cipx</c> 推到教室端待安装目录）。</summary>
    public const string Install = "install";

    /// <summary>卸载。</summary>
    public const string Uninstall = "uninstall";

    /// <summary>启用。</summary>
    public const string Enable = "enable";

    /// <summary>禁用。</summary>
    public const string Disable = "disable";

    /// <summary>判断动作是否合法。</summary>
    public static bool IsValid(string? action) =>
        action is Install or Uninstall or Enable or Disable;
}

/// <summary>某台设备被跳过的原因（下发前的预校验结果）。</summary>
public sealed class PluginDeploySkipDto
{
    /// <summary>设备 ID。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>跳过原因（给管理员看的一句话）。</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>一次插件下发的创建结果。</summary>
public sealed class PluginDeployResultDto
{
    /// <summary>下发记录 ID（用于查询进度）。</summary>
    public string DeploymentId { get; set; } = string.Empty;

    /// <summary>实际下发到的设备数。</summary>
    public int Affected { get; set; }

    /// <summary>被跳过的设备数。</summary>
    public int Skipped { get; set; }

    /// <summary>逐台跳过原因。</summary>
    public List<PluginDeploySkipDto> Skips { get; set; } = [];

    /// <summary>本批指令 ID（前端据此提供撤销与逐台看结果）。</summary>
    public List<string> CommandIds { get; set; } = [];
}

/// <summary>一条插件下发记录与它的逐台进度。</summary>
public sealed class PluginDeploymentDto
{
    /// <summary>下发记录 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>插件 ID。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>插件名（缓存里的索引名，取不到时回落到 ID）。</summary>
    public string PluginName { get; set; } = string.Empty;

    /// <summary>版本（卸载 / 启停时为空）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>动作，见 <see cref="PluginDeployActions"/>。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>目标描述（供列表展示，例如「全部在线设备」）。</summary>
    public string TargetSummary { get; set; } = string.Empty;

    /// <summary>发起人。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>发起时间（ISO 8601 文本）。</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>本批指令总数。</summary>
    public int Total { get; set; }

    /// <summary>尚未结束的条数。</summary>
    public int Pending { get; set; }

    /// <summary>成功条数。</summary>
    public int Done { get; set; }

    /// <summary>失败条数（含超时作废）。</summary>
    public int Failed { get; set; }

    /// <summary>逐台明细。</summary>
    public List<PluginDeploymentItemDto> Items { get; set; } = [];
}

/// <summary>插件下发里某一台设备的结果。</summary>
public sealed class PluginDeploymentItemDto
{
    /// <summary>设备 ID。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>对应的指令 ID。</summary>
    public string CommandId { get; set; } = string.Empty;

    /// <summary>指令状态（pending / dispatched / success / failed / expired / canceled）。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>失败原因分类（取值见 <see cref="CommandErrorCodes"/>）。</summary>
    public string ErrorCode { get; set; } = string.Empty;

    /// <summary>教室端回报的输出 / 错误原文。</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>结束时间（ISO 8601 文本）。</summary>
    public string FinishedAt { get; set; } = string.Empty;
}

/// <summary>
/// 安装指令（<see cref="RemoteCommandKinds.PluginInstall"/>）的载荷。
/// <para><b>下载地址是 A 端自己的相对路径</b>（例如
/// <c>/client/plugin-packages/{id}/{version}/download</c>），教室端用设备令牌向 A 端取包——
/// 不让教室端直接去 GitHub 拿，这样没有外网的教室也能装插件，且 A 端能记账。</para>
/// </summary>
public sealed class PluginInstallPayload
{
    /// <summary>插件 ID。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>A 端缓存下载地址（相对路径；绝对地址也可，教室端会原样使用）。</summary>
    public string CipxUrl { get; set; } = string.Empty;

    /// <summary>包 SHA256（十六进制，大小写不敏感）；为空表示不校验。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>包文件名（仅用于提示与落盘命名）。</summary>
    public string FileName { get; set; } = string.Empty;
}

/// <summary>
/// 启用 / 禁用与卸载插件（<see cref="RemoteCommandKinds.PluginToggle"/> /
/// <see cref="RemoteCommandKinds.PluginUninstall"/>）共用的载荷。
/// </summary>
public sealed class PluginTogglePayload
{
    /// <summary>插件 ID（目录名或 manifest 里的 id 都能定位）。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>是否启用（仅 <see cref="RemoteCommandKinds.PluginToggle"/> 用）。</summary>
    public bool Enabled { get; set; }
}
