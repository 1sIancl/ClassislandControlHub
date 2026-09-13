using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using ControlHub.Protocol;
using ControlHub.Server.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Services;

/// <summary>更新状态快照，供 Web 端展示。</summary>
public sealed class UpdateState
{
    public string CurrentVersion { get; set; } = HubProtocol.ProductVersion;
    public string? LatestVersion { get; set; }
    public bool HasUpdate { get; set; }
    public string? DownloadUrl { get; set; }
    public string? ReleaseNotes { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public string Status { get; set; } = "尚未检查更新。";
    public bool Updating { get; set; }
}

/// <summary>
/// 自动更新服务：从 GitHub Release 检查新版本，下载自包含发布包（zip），
/// 解压替换程序文件（<b>保留 data 数据目录</b>），然后退出进程由 systemd（Restart=always）自动重启。
/// <para>更新包约定：资产名为 <c>ControlHub.Server-&lt;rid&gt;.zip</c>，rid 为当前运行时标识（如 linux-x64）。</para>
/// </summary>
public sealed class UpdateService(
    IOptions<ServerOptions> options,
    ILogger<UpdateService> logger) : BackgroundService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    static UpdateService()
    {
        // GitHub API 要求 User-Agent。
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClassislandControlHub", HubProtocol.ProductVersion));
    }

    private readonly object _gate = new();
    private UpdateState _state = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutoCheckUpdates)
        {
            return;
        }

        await Task.Yield();

        // 启动后延迟片刻再查一次，之后按配置间隔轮询。
        await SafeDelayAsync(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckForUpdateAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "检查更新失败。");
            }

            await SafeDelayAsync(TimeSpan.FromHours(Math.Max(1, options.Value.UpdateCheckIntervalHours)), stoppingToken);
        }
    }

    /// <summary>获取当前更新状态快照。</summary>
    public UpdateState GetState()
    {
        lock (_gate)
        {
            return new UpdateState
            {
                CurrentVersion = _state.CurrentVersion,
                LatestVersion = _state.LatestVersion,
                HasUpdate = _state.HasUpdate,
                DownloadUrl = _state.DownloadUrl,
                ReleaseNotes = _state.ReleaseNotes,
                CheckedAt = _state.CheckedAt,
                Status = _state.Status,
                Updating = _state.Updating,
            };
        }
    }

    /// <summary>检查 GitHub Release 是否有新版本，更新状态并返回。</summary>
    public async Task<UpdateState> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var repo = options.Value.UpdateRepo;
        var current = HubProtocol.ProductVersion;

        try
        {
            var api = $"https://api.github.com/repos/{repo}/releases/latest";
            using var doc = await GetJsonAsync(api, cancellationToken);

            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
            var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            var (hasUpdate, downloadUrl) = HasNewer(tag, current)
                ? (true, FindAssetUrl(root))
                : (false, null);

            lock (_gate)
            {
                _state = new UpdateState
                {
                    CurrentVersion = current,
                    LatestVersion = tag,
                    HasUpdate = hasUpdate,
                    DownloadUrl = downloadUrl,
                    ReleaseNotes = body,
                    CheckedAt = DateTimeOffset.UtcNow,
                    Status = hasUpdate
                        ? $"发现新版本 {tag}。"
                        : (tag is null ? "未找到发布版本。" : $"已是最新版本 {current}。"),
                    Updating = false,
                };
            }

            logger.LogInformation("检查更新：当前 {Current}，最新 {Latest}，{Status}", current, tag, _state.Status);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _state = new UpdateState
                {
                    CurrentVersion = current,
                    CheckedAt = DateTimeOffset.UtcNow,
                    Status = "检查更新失败：" + ex.Message,
                };
            }
            logger.LogWarning(ex, "检查更新失败。");
        }

        return GetState();
    }

    /// <summary>下载并应用更新：解压替换程序文件（保留 data 目录），然后重启进程。</summary>
    public async Task<UpdateState> ApplyUpdateAsync(CancellationToken cancellationToken = default)
    {
        await _applyGate.WaitAsync(cancellationToken);
        try
        {
            var state = await CheckForUpdateAsync(cancellationToken);
            if (!state.HasUpdate || string.IsNullOrWhiteSpace(state.DownloadUrl))
            {
                return GetState();
            }

            lock (_gate)
            {
                _state.Updating = true;
                _state.Status = $"正在更新到 {state.LatestVersion}…";
            }

            var zipPath = Path.Combine(Path.GetTempPath(), $"classislandcontrolhub-{Guid.NewGuid():N}.zip");
            var extractDir = Path.Combine(Path.GetTempPath(), $"classislandcontrolhub-{Guid.NewGuid():N}");

            try
            {
                logger.LogInformation("下载更新包：{Url}", state.DownloadUrl);
                await DownloadAsync(state.DownloadUrl, zipPath, cancellationToken);

                logger.LogInformation("解压更新包到 {Dir}", extractDir);
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                await ReplaceFilesAsync(extractDir, cancellationToken);
            }
            finally
            {
                try { File.Delete(zipPath); } catch { /* 忽略 */ }
            }

            lock (_gate)
            {
                _state.Status = $"已更新到 {state.LatestVersion}，正在重启…";
                _state.Updating = false;
            }

            // 延迟退出进程：systemd 的 Restart=always 会自动拉起新版本。
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                Environment.Exit(0);
            });

            return GetState();
        }
        finally
        {
            _applyGate.Release();
        }
    }

    // ────────────────────────────── 内部 ──────────────────────────────

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct);
    }

    /// <summary>用解压出来的文件覆盖程序目录，跳过数据目录（data）与更新临时文件。</summary>
    private async Task ReplaceFilesAsync(string extractDir, CancellationToken ct)
    {
        var appDir = Path.GetFullPath(AppContext.BaseDirectory);
        var dataDir = options.Value.ResolveDataDirectory(appDir);
        var dataDirFull = Path.GetFullPath(dataDir);

        foreach (var srcFile in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(extractDir, srcFile);
            var dstFile = Path.GetFullPath(Path.Combine(appDir, rel));

            // 跳过数据目录（保留原有数据）。
            if (dstFile.StartsWith(dataDirFull, StringComparison.Ordinal))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);

            // Windows 下当前进程的 dll 被锁定，无法覆盖；Linux 可直接替换。
            try
            {
                File.Copy(srcFile, dstFile, overwrite: true);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "覆盖文件失败（可能被锁定）：{File}", dstFile);
            }
        }

        logger.LogInformation("程序文件已替换，数据目录 {DataDir} 保留。", dataDirFull);
        await Task.CompletedTask;
    }

    private static string? FindAssetUrl(JsonElement root)
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // 优先精确匹配 <name>-<rid>.zip，其次任意 .zip。
        string? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Contains(rid, StringComparison.OrdinalIgnoreCase))
            {
                return asset.GetProperty("browser_download_url").GetString();
            }

            fallback ??= asset.GetProperty("browser_download_url").GetString();
        }

        return fallback;
    }

    private static bool HasNewer(string? latest, string current)
    {
        if (latest is null)
        {
            return false;
        }

        return Version.TryParse(latest, out var l)
               && Version.TryParse(current, out var c)
               && l > c;
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // 忽略。
        }
    }
}
