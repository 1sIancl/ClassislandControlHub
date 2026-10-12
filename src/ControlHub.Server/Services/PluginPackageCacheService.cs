using System.IO.Compression;
using System.Security.Cryptography;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;

namespace ControlHub.Server.Services;

/// <summary>
/// 插件包（<c>.cipx</c>）的代理缓存（#121）。
/// <para>A 端按索引里的地址把包下载到本地（<c>data/plugin-cache/&lt;插件&gt;/&lt;版本&gt;/</c>），
/// 校验上游 MD5、算出 SHA256 后登记；教室端之后只从 A 端取包，**不需要外网**。</para>
/// </summary>
public sealed class PluginPackageCacheService
{
    /// <summary>单个插件包的大小上限。论坛里流通的插件都在几 MB 量级，挡住异常的大包。</summary>
    private const long MaxPackageBytes = 512L * 1024 * 1024;

    private readonly HubStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PluginPackageCacheService> _logger;

    /// <summary>创建缓存服务。</summary>
    public PluginPackageCacheService(HubStore store, IHttpClientFactory httpFactory,
        ILogger<PluginPackageCacheService> logger)
    {
        _store = store;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>缓存根目录：与数据库同级（备份 / 迁移数据库时一起带走）。</summary>
    public string CacheRoot
    {
        get
        {
            var dataDir = Path.GetDirectoryName(_store.DatabasePath);
            return Path.Combine(string.IsNullOrEmpty(dataDir) ? "." : dataDir, "plugin-cache");
        }
    }

    /// <summary>
    /// 下载并缓存某个版本（<paramref name="version"/> 为空 = 索引里的最新版）。
    /// <para>镜像按「首选 → 其余」依次尝试；每一个都要过 MD5 与包内 manifest 校验，
    /// 因此某个镜像返回了错误页 / 半截文件时会自动换下一个，而不是把坏包留在缓存里。</para>
    /// </summary>
    /// <exception cref="HubException">插件不在索引里、没有可用镜像、或所有镜像都失败。</exception>
    public async Task<PluginPackageRow> CacheAsync(string pluginId, string? version,
        CancellationToken cancellationToken = default)
    {
        var item = await _store.GetMarketItemAsync(pluginId, cancellationToken)
                   ?? throw HubException.NotFound("插件不在本地索引里，请先同步插件市场索引。");

        var target = string.IsNullOrWhiteSpace(version) ? item.Version : version.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            throw HubException.Validation("无法确定要缓存的版本：索引里没有版本号，请显式指定。");
        }

        if (item.DownloadUrl.Length == 0)
        {
            throw HubException.Validation("索引里这个插件没有下载地址。");
        }

        var mirrors = await LoadMirrorsAsync(cancellationToken);
        var preferred = await _store.GetSettingAsync(PluginMarketSyncService.PreferredMirrorKey, string.Empty,
            cancellationToken);
        var ordered = PluginMirrorResolver.Order(mirrors, preferred ?? string.Empty);

        if (ordered.Count == 0)
        {
            throw HubException.Validation("索引里没有可用的下载镜像，请先同步索引。");
        }

        var errors = new List<string>();
        foreach (var (name, root) in ordered)
        {
            var url = PluginMirrorResolver.Expand(item.DownloadUrl, root);
            try
            {
                return await DownloadAndStoreAsync(item, target, name, url, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not HubException)
            {
                errors.Add($"{name}：{ex.Message}");
                _logger.LogWarning(ex, "从镜像 {Mirror} 缓存插件包失败（{Id} {Version}）。", name, pluginId, target);
            }
        }

        throw HubException.Validation(
            $"所有镜像都没能取到这个插件包：{string.Join("；", errors)}");
    }

    private async Task<PluginPackageRow> DownloadAndStoreAsync(PluginMarketRow item, string version,
        string mirrorName, string url, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(CacheRoot, SafeSegment(item.PluginId), SafeSegment(version));
        Directory.CreateDirectory(directory);

        var fileName = $"{SafeSegment(item.PluginId)}-{SafeSegment(version)}.cipx";
        var finalPath = Path.Combine(directory, fileName);
        var tempPath = finalPath + ".part";

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(5);

            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
                }

                if (response.Content.Headers.ContentLength is > MaxPackageBytes)
                {
                    throw new InvalidOperationException($"包体超过上限（{MaxPackageBytes / 1024 / 1024}MB）");
                }

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(tempPath);
                await source.CopyToAsync(target, cancellationToken);
            }

            var length = new FileInfo(tempPath).Length;
            if (length == 0)
            {
                throw new InvalidOperationException("取回的文件是空的");
            }

            if (length > MaxPackageBytes)
            {
                throw new InvalidOperationException($"包体超过上限（{MaxPackageBytes / 1024 / 1024}MB）");
            }

            // 上游完整性：索引里给了 MD5 就一定要对上——镜像返回错误页时也会「下载成功」，
            // 不校验的话坏包会一路发到教室机。
            var md5 = await HashAsync(tempPath, MD5.Create(), cancellationToken);
            if (item.DownloadMd5.Length > 0
                && !string.Equals(md5, item.DownloadMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"MD5 与索引不符（索引 {item.DownloadMd5}，实际 {md5}）");
            }

            // 包内自证：必须是 zip 且 manifest 的 id / 版本与请求一致。
            var (id, pkgVersion, error) = ReadManifest(tempPath);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            if (!string.Equals(id, item.PluginId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"包内插件 ID 是 {id}，与请求的 {item.PluginId} 不符");
            }

            if (pkgVersion.Length > 0 && !string.Equals(pkgVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"包内版本是 {pkgVersion}，与请求的 {version} 不符");
            }

            var sha256 = await HashAsync(tempPath, SHA256.Create(), cancellationToken);

            File.Move(tempPath, finalPath, overwrite: true);

            var row = new PluginPackageRow
            {
                PluginId = item.PluginId,
                Version = version,
                FilePath = finalPath,
                FileName = fileName,
                Sha256 = sha256,
                Md5 = md5,
                SizeBytes = length,
                SourceMirror = mirrorName,
                SourceUrl = url,
                CachedAt = DateTimeOffset.UtcNow,
            };
            await _store.UpsertPackageAsync(row, cancellationToken);
            _logger.LogInformation("已缓存插件包 {Id} {Version}（{Size} 字节，镜像 {Mirror}）。",
                item.PluginId, version, length, mirrorName);
            return row;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>删除缓存（同时删掉文件；文件已不在也算删成功）。</summary>
    public async Task<bool> DeleteAsync(string pluginId, string version,
        CancellationToken cancellationToken = default)
    {
        var row = await _store.GetPackageAsync(pluginId, version, cancellationToken);
        if (row is null)
        {
            return false;
        }

        try
        {
            if (File.Exists(row.FilePath))
            {
                File.Delete(row.FilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除插件包文件失败：{Path}", row.FilePath);
        }

        return await _store.DeletePackageAsync(pluginId, version, cancellationToken);
    }

    /// <summary>读取索引里的镜像表。</summary>
    public async Task<Dictionary<string, string>> LoadMirrorsAsync(CancellationToken cancellationToken = default)
    {
        var raw = await _store.GetSettingAsync(PluginMarketSyncService.MirrorsKey, "{}", cancellationToken);
        return HubJson.DeserializeOrDefault(raw ?? "{}", new Dictionary<string, string>());
    }

    /// <summary>把相对路径按数据目录换算成本地绝对路径（B 端下载时定界用）。</summary>
    public string ResolvePath(PluginPackageRow row)
    {
        // 只信任我们自己在缓存时写下的路径：先算绝对路径，再确认它在缓存根目录之内，
        // 避免记录被改过之后这个“下载”接口变成任意文件读取。
        var full = Path.GetFullPath(row.FilePath);
        var root = Path.GetFullPath(CacheRoot);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw HubException.Validation("缓存文件路径不合法。");
        }

        return full;
    }

    /// <summary>
    /// 把文件名 / 版本号里的路径分隔符与上跳符号清掉（防止写到缓存目录外面）。
    /// <para>公开是为了让单元测试直接打这个纯函数——路径拼接的安全边界值得单独断言。</para>
    /// </summary>
    public static string SafeSegment(string value)
    {
        var cleaned = new string(value
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')
            .ToArray()).Trim('.', '_');
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    /// <summary>
    /// 读取 <c>.cipx</c> 里的 <c>manifest.yml</c>（扁平字段逐行解析，不引 YAML 依赖）。
    /// <para>公开给单元测试：镜像可能返回半截文件或错误页，这一层的容错要能单独验。</para>
    /// </summary>
    public static (string Id, string Version, string? Error) ReadManifest(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e =>
                            e.FullName.Equals("manifest.yml", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("包内没有 manifest.yml");

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            string? id = null, version = null;
            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var idx = trimmed.IndexOf(':');
                if (idx <= 0)
                {
                    continue;
                }

                var key = trimmed[..idx].Trim();
                var value = trimmed[(idx + 1)..].Trim().Trim('"', '\'');
                switch (key)
                {
                    case "id": id = value; break;
                    case "version": version = value; break;
                }
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                return (string.Empty, string.Empty, "包内 manifest 缺少 id");
            }

            return (id, version ?? string.Empty, null);
        }
        catch (Exception ex)
        {
            return (string.Empty, string.Empty, $"不是有效的 .cipx：" + ex.Message);
        }
    }

    private static async Task<string> HashAsync(string path, HashAlgorithm algorithm,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await algorithm.ComputeHashAsync(stream, cancellationToken);
        algorithm.Dispose();
        return Convert.ToHexString(hash);
    }
}

/// <summary>插件下载镜像的解析：<c>{root}</c> 展开与尝试顺序。</summary>
public static class PluginMirrorResolver
{
    /// <summary>
    /// 决定镜像的尝试顺序。
    /// <para>指定了首选就把它排在最前；「自动」时把代理类镜像排在 GitHub 直连之前——
    /// 校园网里 GitHub 直连经常不通，而代理镜像只是「多一跳」，失败会自己往后走。</para>
    /// </summary>
    public static List<(string Name, string Root)> Order(Dictionary<string, string> mirrors, string preferred)
    {
        var entries = mirrors
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => (Name: kv.Key, Root: kv.Value.TrimEnd('/')))
            .ToList();

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var wanted = entries.Where(e => string.Equals(e.Name, preferred.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (wanted.Count > 0)
            {
                return wanted.Concat(entries.Where(e => !wanted.Contains(e))).ToList();
            }
        }

        static int Rank(string name) => name.ToLowerInvariant() switch
        {
            "ghproxy" => 0,
            "moeyy" => 1,
            "github" => 2,
            _ => 3,
        };

        return entries.OrderBy(e => Rank(e.Name)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>把索引里的 <c>{root}</c> 模板展开成实际地址。</summary>
    public static string Expand(string template, string root)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        return template.Replace("{root}", root.TrimEnd('/')).Trim();
    }
}
