using System.Text.RegularExpressions;
using IslandHub.Protocol;
using IslandHub.Server.Http;
using IslandHub.Server.Options;
using Microsoft.Extensions.Options;

namespace IslandHub.Server.Services;

/// <summary>
/// 上传上来的 `.cidata` 的临时存放（`data/imports/`）。
///
/// <para><b>为什么要落盘、不把包塞进请求里</b>：导入分成「先预览、再确认」两步，
/// 而包本身可能几十 MB（`Plugins/` 会撑大它）。若预览与确认各传一次，管理员就得等两遍；
/// 若把包转换成内容包后在内存里挂着，服务重启就丢。落临时文件 + 一次性 token 最省事：
/// 预览时读一次、确认时按 token 再读一次，服务端不需要保存任何会话状态。</para>
///
/// <para><b>token 形状必须校验</b>：它会被拼进文件路径，不校验就是一个目录穿越漏洞
/// （`../../data/hub.db`）。这里只接受 32 位小写十六进制。</para>
/// </summary>
public sealed class CidataImportStore
{
    /// <summary>上传包大小上限。与 <see cref="CidataImporter.MaxArchiveBytes"/> 保持一致。</summary>
    public const long MaxBytes = CidataImporter.MaxArchiveBytes;

    /// <summary>临时文件保留时长。够管理员看一眼预览再点确认，又不至于长期占盘。</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    /// <summary>临时文件数量上限，超出按最旧先删。</summary>
    private const int MaxPendingFiles = 20;

    private static readonly Regex TokenPattern = new(
        "^[0-9a-f]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _root;
    private readonly ILogger<CidataImportStore> _logger;

    public CidataImportStore(
        IOptions<ServerOptions> options,
        IWebHostEnvironment environment,
        ILogger<CidataImportStore> logger)
    {
        _root = Path.Combine(options.Value.ResolveDataDirectory(environment.ContentRootPath), "imports");
        _logger = logger;
    }

    /// <summary>把上传的包写进临时目录，返回一次性 token。</summary>
    /// <exception cref="HubException">文件为空、不是 ZIP、或超过大小上限。</exception>
    public async Task<string> SaveAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        Directory.CreateDirectory(_root);
        Purge();

        var token = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_root, token + ".cidata");
        var buffer = new byte[81920];
        long total = 0;
        var checkedSignature = false;

        try
        {
            await using var target = File.Create(path);

            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (!checkedSignature)
                {
                    checkedSignature = true;
                    // ZIP 的本地文件头 / 空归档 / 分卷都以 "PK" 开头。
                    if (read < 2 || buffer[0] != (byte)'P' || buffer[1] != (byte)'K')
                    {
                        throw HubException.Validation(
                            "这不是一个 .cidata 文件。它应该是 ClassIsland「数据迁移」里导出的 ZIP 包"
                            + "（文件名形如 ClassIsland_data_2.1.1.1_20261015_101530.cidata）。");
                    }
                }

                // 边写边计数：不能信 Content-Length，它由客户端说了算。
                total += read;
                if (total > MaxBytes)
                {
                    throw HubException.Validation(
                        $"文件超过 {MaxBytes / 1024 / 1024}MB 上限。"
                        + "导出时可以不勾「其它配置」，那样插件目录不会被打进包里，体积会小很多。");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        if (total == 0)
        {
            TryDelete(path);
            throw HubException.Validation("上传的文件是空的。");
        }

        return token;
    }

    /// <summary>把 token 解析成临时文件路径；token 形状不合法或文件已过期时返回 <c>null</c>。</summary>
    public string? ResolvePath(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !TokenPattern.IsMatch(token))
        {
            return null;
        }

        var path = Path.Combine(_root, token + ".cidata");
        return File.Exists(path) ? path : null;
    }

    /// <summary>删除一个临时文件（导入成功后调用）。</summary>
    public void Remove(string? token)
    {
        var path = ResolvePath(token);
        if (path is not null)
        {
            TryDelete(path);
        }
    }

    /// <summary>清理过期与超量的临时文件。每次上传前顺手做一次，不需要定时任务。</summary>
    public void Purge()
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - Lifetime;
            var files = new DirectoryInfo(_root).GetFiles("*.cidata")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            for (var i = 0; i < files.Count; i++)
            {
                if (files[i].LastWriteTimeUtc < cutoff || i >= MaxPendingFiles)
                {
                    TryDelete(files[i].FullName);
                }
            }
        }
        catch (Exception ex)
        {
            // 清理失败不能影响导入本身：这些文件最多占一点磁盘。
            _logger.LogWarning(ex, "清理 .cidata 临时文件失败。");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉（被占用等）就留着，下次 Purge 还会再试。
        }
    }
}
