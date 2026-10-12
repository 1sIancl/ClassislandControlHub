using System.IO.Compression;
using System.Text.Json;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;

namespace ControlHub.Server.Services;

/// <summary>
/// ClassIsland 插件市场索引的同步器（#121）。
/// <para>索引是官方发布的一个 <c>index.zip</c>（里面是 <c>index.v2.json</c>，含全部插件的元数据、
/// 下载地址模板与镜像表）。A 端把它同步到本地，管理端只读本地缓存——教室端因此完全不必访问外网。</para>
/// <para><b>关于镜像</b>：索引里的镜像根（<c>{root}</c>）用来展开**插件包**的下载地址，
/// 索引自身只有官方一个 CDN 地址（可在设置里改），所以这里不做镜像回退，失败就如实报错。</para>
/// </summary>
public sealed class PluginMarketSyncService : BackgroundService
{
    /// <summary>官方索引地址模板（<c>{time}</c> 会被替换成 Unix 秒，用于穿透 CDN 缓存）。</summary>
    public const string DefaultIndexUrl =
        "https://get.classisland.tech/d/ClassIsland-Ningbo-S3/classisland/plugin/index.zip?time={time}";

    /// <summary>设置键：索引地址模板。</summary>
    public const string IndexUrlKey = "plugin_market.index_url";

    /// <summary>设置键：首选镜像名（空 = 自动）。</summary>
    public const string PreferredMirrorKey = "plugin_market.preferred_mirror";

    /// <summary>设置键：索引给出的镜像表（JSON：名称 → 根地址）。</summary>
    public const string MirrorsKey = "plugin_market.mirrors";

    /// <summary>设置键：最近一次成功同步时间。</summary>
    public const string LastSyncKey = "plugin_market.last_sync";

    /// <summary>设置键：最近一次失败原因。</summary>
    public const string LastErrorKey = "plugin_market.last_error";

    /// <summary>自动同步周期。索引刷新不频繁，7 天与 ClassIsland 自身保持一致。</summary>
    private static readonly TimeSpan SyncInterval = TimeSpan.FromDays(7);

    private readonly HubStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PluginMarketSyncService> _logger;

    /// <summary>同一时刻只允许一次同步（管理端点与后台定时都可能触发）。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>创建索引同步器。</summary>
    public PluginMarketSyncService(HubStore store, IHttpClientFactory httpFactory,
        ILogger<PluginMarketSyncService> logger)
    {
        _store = store;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 让 Web 先起来再联网：否则首启若外网不通，启动日志会先被一串超时刷屏。
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

            if (await ShouldSyncAsync(stoppingToken))
            {
                await SyncAsync(stoppingToken);
            }

            using var timer = new PeriodicTimer(SyncInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SyncAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机。
        }
    }

    /// <summary>库里没有索引，或上次同步已超过周期，就该同步一次。</summary>
    private async Task<bool> ShouldSyncAsync(CancellationToken cancellationToken)
    {
        if ((await _store.ListMarketIndexAsync(cancellationToken)).Count == 0)
        {
            return true;
        }

        var last = await _store.GetSettingAsync(LastSyncKey, null, cancellationToken);
        return !DateTimeOffset.TryParse(last, out var at) || DateTimeOffset.UtcNow - at >= SyncInterval;
    }

    /// <summary>立刻同步一次索引。</summary>
    /// <exception cref="HubException">已有同步在进行中（<c>CONFLICT</c>）。</exception>
    public async Task<PluginMarketSyncResultDto> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            throw HubException.Conflict("已有一次索引同步在进行中，请稍后再试。");
        }

        try
        {
            return await SyncCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PluginMarketSyncResultDto> SyncCoreAsync(CancellationToken cancellationToken)
    {
        var template = await _store.GetSettingAsync(IndexUrlKey, DefaultIndexUrl, cancellationToken);
        var url = string.IsNullOrWhiteSpace(template) ? DefaultIndexUrl : template.Trim();
        url = url.Replace("{time}", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

        var result = new PluginMarketSyncResultDto
        {
            SyncedAt = DateTimeOffset.UtcNow.ToString("O"),
            Source = url,
        };

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(60);

            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"索引地址返回 HTTP {(int)response.StatusCode}。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var entry = zip.Entries.FirstOrDefault(e =>
                            e.FullName.EndsWith("index.v2.json", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("索引包里没有 index.v2.json。");

            string json;
            await using (var entryStream = entry.Open())
            using (var reader = new StreamReader(entryStream))
            {
                json = await reader.ReadToEndAsync(cancellationToken);
            }

            var (rows, mirrors) = ParseIndex(json);
            if (rows.Count == 0)
            {
                throw new InvalidOperationException("索引里没有任何插件条目。");
            }

            await _store.ReplaceMarketIndexAsync(rows, cancellationToken);
            await _store.SetSettingAsync(MirrorsKey, HubJson.Serialize(mirrors), cancellationToken);
            await _store.SetSettingAsync(LastSyncKey, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            await _store.SetSettingAsync(LastErrorKey, string.Empty, cancellationToken);

            result.Ok = true;
            result.Count = rows.Count;
            _logger.LogInformation("插件索引同步成功：{Count} 个插件（{Url}）。", rows.Count, url);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 失败不推翻已有缓存：管理端还能按上一次的索引发插件，只是页面上会显示这条原因。
            await _store.SetSettingAsync(LastErrorKey, ex.Message, cancellationToken);
            _logger.LogWarning(ex, "插件索引同步失败。");
            result.Ok = false;
            result.Error = ex.Message;
            return result;
        }
    }

    /// <summary>解析 <c>index.v2.json</c>（PascalCase；与宿主 <c>PluginIndex</c> 一致）。</summary>
    internal static (List<PluginMarketRow> Rows, Dictionary<string, string> Mirrors) ParseIndex(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var index = JsonSerializer.Deserialize<IndexDocument>(json, options)
                    ?? throw new InvalidOperationException("索引 JSON 无法解析。");

        var syncedAt = DateTimeOffset.UtcNow;
        var rows = new List<PluginMarketRow>();
        foreach (var item in index.Plugins)
        {
            var manifest = item.Manifest;
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            {
                continue;
            }

            rows.Add(new PluginMarketRow
            {
                PluginId = manifest.Id.Trim(),
                Name = manifest.Name?.Trim() ?? string.Empty,
                Version = manifest.Version?.Trim() ?? string.Empty,
                ApiVersion = manifest.ApiVersion?.Trim() ?? string.Empty,
                Author = manifest.Author?.Trim() ?? string.Empty,
                Description = manifest.Description?.Trim() ?? string.Empty,
                DownloadUrl = item.DownloadUrl?.Trim() ?? string.Empty,
                DownloadMd5 = item.DownloadMd5?.Trim() ?? string.Empty,
                IconPath = item.RealIconPath?.Trim() ?? string.Empty,
                ReadmePath = manifest.Readme?.Trim() ?? string.Empty,
                RepoUrl = manifest.Url?.Trim() ?? string.Empty,
                SupportedOs = HubJson.Serialize(manifest.SupportedOSPlatforms ?? []),
                DownloadCount = item.DownloadCount,
                StarsCount = item.StarsCount,
                SyncedAt = syncedAt,
            });
        }

        return (rows, index.DownloadMirrors ?? []);
    }

    /// <summary>索引文档的最小映射（只取我们用得上的字段）。</summary>
    private sealed class IndexDocument
    {
        public List<IndexItem> Plugins { get; set; } = [];

        public Dictionary<string, string>? DownloadMirrors { get; set; }
    }

    /// <summary>索引里的一个插件条目。</summary>
    private sealed class IndexItem
    {
        public string? DownloadUrl { get; set; }

        public string? DownloadMd5 { get; set; }

        public string? RealIconPath { get; set; }

        public long DownloadCount { get; set; }

        public long StarsCount { get; set; }

        public IndexManifest? Manifest { get; set; }
    }

    /// <summary>索引条目里的 manifest 片段。</summary>
    private sealed class IndexManifest
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Version { get; set; }

        public string? ApiVersion { get; set; }

        public string? Author { get; set; }

        public string? Description { get; set; }

        public string? Icon { get; set; }

        public string? Readme { get; set; }

        public string? Url { get; set; }

        public List<string>? SupportedOSPlatforms { get; set; }
    }
}
