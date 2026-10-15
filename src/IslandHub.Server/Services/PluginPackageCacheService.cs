using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;

namespace IslandHub.Server.Services;

/// <summary>
/// 插件包（<c>.cipx</c>）的代理缓存（#121）。
/// <para>A 端按索引里的地址把包下载到本地（<c>data/plugin-cache/&lt;插件&gt;/&lt;版本&gt;/</c>），
/// 校验上游 MD5、算出 SHA256 后登记；教室端之后只从 A 端取包，**不需要外网**。</para>
/// </summary>
public sealed class PluginPackageCacheService
{
    /// <summary>单个插件包的大小上限。论坛里流通的插件都在几 MB 量级，挡住异常的大包。</summary>
    private const long MaxPackageBytes = 512L * 1024 * 1024;

    /// <summary>自述文件的大小上限（1 MB）。README 不该这么大，超了多半是取到了别的东西。</summary>
    private const long MaxReadmeBytes = 1024 * 1024;

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
    /// 插件图标的缓存目录（#122 后续）。
    /// <para>图标同样是「A 端取一次、其余人都从 A 端拿」：索引里的图标地址指向 GitHub，
    /// 管理端浏览器在国内学校网络里直连经常加载不出来，整张市场列表会变成一排碎图。</para>
    /// </summary>
    public string IconDirectory
    {
        get
        {
            var dataDir = Path.GetDirectoryName(_store.DatabasePath);
            return Path.Combine(string.IsNullOrEmpty(dataDir) ? "." : dataDir, "plugin-icons");
        }
    }

    /// <summary>
    /// 把图标文件名解析成绝对路径，并**限定在图标目录之内**（与 <see cref="ResolvePath"/> 同理：
    /// 文件名来自数据库，万一被改过也不能变成任意文件读取）。
    /// </summary>
    public string ResolveIconPath(string fileName)
    {
        return ResolveInDirectory(IconDirectory, fileName, "图标文件名不合法。");
    }

    /// <summary>
    /// 插件自述文件（Markdown）的缓存目录。与图标同理：地址指向 GitHub raw，
    /// 让管理端浏览器直连多半打不开，A 端取一次更稳。
    /// </summary>
    public string ReadmeDirectory
    {
        get
        {
            var dataDir = Path.GetDirectoryName(_store.DatabasePath);
            return Path.Combine(string.IsNullOrEmpty(dataDir) ? "." : dataDir, "plugin-readmes");
        }
    }

    /// <summary>解析自述文件路径，并限定在自述目录之内。</summary>
    public string ResolveReadmePath(string fileName)
    {
        return ResolveInDirectory(ReadmeDirectory, fileName, "自述文件名不合法。");
    }

    private static string ResolveInDirectory(string directory, string fileName, string error)
    {
        var root = Path.GetFullPath(directory);
        var full = Path.GetFullPath(Path.Combine(root, Path.GetFileName(fileName)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw HubException.Validation(error);
        }

        return full;
    }

    /// <summary>
    /// 取一个插件的自述文件，缓存命中直接返回本地路径，否则**按需下载**（#121 后续）。
    /// <para>为什么按需而不是跟着索引同步一起下：自述文件比图标大、地址多指向 GitHub raw、
    /// 失败率也高（实测图标 69 张就要 55 秒）。管理员点开某个插件详情时才值得为它付一次网络成本。</para>
    /// <para>只按索引里登记的 <c>readme_path</c> 取，不接受调用方给任意地址——否则这个免鉴权接口
    /// 就变成了一个开放的抓取代理。</para>
    /// </summary>
    /// <returns>本地文件绝对路径；索引里没有自述文件或取不到时返回 <c>null</c>。</returns>
    public async Task<string?> EnsureReadmeAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var item = await _store.GetMarketItemAsync(pluginId, cancellationToken);
        if (item is null || item.ReadmePath.Length == 0)
        {
            return null;
        }

        // 已经缓存过：文件还在就直接用（索引换地址时会换文件名，见 ReadmeFileName）。
        if (item.ReadmeFile.Length > 0)
        {
            var cached = ResolveReadmePath(item.ReadmeFile);
            if (File.Exists(cached) && new FileInfo(cached).Length > 0)
            {
                return cached;
            }
        }

        var mirrors = await LoadMirrorsAsync(cancellationToken);
        var preferred = await _store.GetSettingAsync(PluginMarketSyncService.PreferredMirrorKey, string.Empty,
            cancellationToken);
        var ordered = PluginMirrorResolver.Order(mirrors, preferred ?? string.Empty);
        var errors = new List<string>();

        foreach (var mirror in ordered)
        {
            var url = PluginMirrorResolver.Expand(item.ReadmePath, mirror.Root);
            try
            {
                var http = _httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(20);

                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{mirror.Name}: HTTP {(int)response.StatusCode}");
                    continue;
                }

                if (response.Content.Headers.ContentLength is > MaxReadmeBytes)
                {
                    errors.Add($"{mirror.Name}: 文件超过 {MaxReadmeBytes / 1024} KB");
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length == 0 || bytes.Length > MaxReadmeBytes)
                {
                    errors.Add($"{mirror.Name}: 内容为空或过大");
                    continue;
                }

                var directory = ReadmeDirectory;
                Directory.CreateDirectory(directory);
                var fileName = ReadmeFileName(pluginId, url);
                var target = ResolveReadmePath(fileName);
                await File.WriteAllBytesAsync(target, bytes, cancellationToken);

                // 换过一次地址就换一个文件名：清掉这个插件此前留下的旧文件。
                foreach (var stale in Directory.EnumerateFiles(directory, $"{pluginId}.*"))
                {
                    if (!string.Equals(Path.GetFileName(stale), fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(stale);
                    }
                }

                await _store.SetMarketReadmeFileAsync(pluginId, fileName, cancellationToken);
                return target;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{mirror.Name}: {ex.Message}");
            }
        }

        _logger.LogDebug("插件 {Id} 的自述文件取回失败：{Errors}", pluginId, string.Join("；", errors));
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 被占用就留着，下次取的时候再清。
        }
    }

    /// <summary>
    /// 自述文件名：<c>&lt;插件 ID&gt;.&lt;地址哈希前 8 位&gt;.md</c>。
    /// <para>与图标同一套思路：带地址哈希，上游换文件就换名（浏览器 / 我们自己都不会拿旧的）。</para>
    /// </summary>
    public static string ReadmeFileName(string pluginId, string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLowerInvariant();
        var safeId = new string(pluginId
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray()).Trim('.', '_');
        return safeId.Length == 0 ? $"{hash}.md" : $"{safeId}.{hash}.md";
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
