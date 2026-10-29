using System.Security.Cryptography;
using System.Text;

using IslandHub.Protocol;
using IslandHub.Server.Options;

using Microsoft.Extensions.Options;

namespace IslandHub.Server.Services;

/// <summary>
/// 管理员个人界面背景图（#127 的补完项）。
///
/// <para>
/// 与品牌图（<see cref="BrandingImageStore"/>）的区别是**按账号隔离**：
/// 品牌图全站共用、公开可读（登录页要显示）；个人背景图只有本人能读，
/// 换个人登录就是另一张。所以目录按账号分，文件名带随机 GUID。
/// </para>
///
/// <para>
/// 每个账号**只保留一张**：再上传会先删旧的。背景图不是资产库，
/// 留一堆旧图只会让目录越滚越大，且用户根本没有"选回上一张"的入口。
/// </para>
/// </summary>
public sealed class UserBackgroundStore
{
    /// <summary>单张上限（4 MB）。背景图会被浏览器整张解码，太大只会拖慢首屏。</summary>
    public const long MaxBytes = 4L * 1024 * 1024;

    /// <summary>站内读取地址（需登录，且只返回当前账号自己的那张）。</summary>
    public const string Url = "/api/v1/admin/ui-preferences/background-image";

    private readonly string _root;

    public UserBackgroundStore(IOptionsMonitor<ServerOptions> options, IWebHostEnvironment environment)
    {
        _root = Path.Combine(
            options.CurrentValue.ResolveDataDirectory(environment.ContentRootPath), "user-backgrounds");
    }

    /// <summary>
    /// 保存（或替换）某个账号的背景图，返回文件名。
    /// </summary>
    /// <exception cref="HubException">类型不支持或超过大小上限。</exception>
    public async Task<string> SaveAsync(string userId, Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var header = new byte[32];
        var read = await BrandingImageStore.ReadAtLeastAsync(source, header, header.Length, cancellationToken);
        var detected = BrandingImageStore.Detect(header.AsSpan(0, read));
        if (detected is null)
        {
            throw HubException.Validation(
                "只支持 PNG / JPEG / WebP / GIF / AVIF（按文件内容判定，改扩展名不行）；SVG 不收——它能内嵌脚本。");
        }

        var (extension, _) = detected.Value;
        var directory = DirectoryFor(userId);
        Directory.CreateDirectory(directory);

        // 先删旧图再写新图：一个账号只留一张，避免磁盘上堆一摞没人认领的壁纸。
        foreach (var existing in Directory.GetFiles(directory))
        {
            TryDelete(existing);
        }

        var name = $"{Guid.NewGuid():N}.{extension}";
        var path = Path.Combine(directory, name);

        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await file.WriteAsync(header.AsMemory(0, read), cancellationToken);
            await BrandingImageStore.CopyBoundedAsync(
                source, file, MaxBytes - read, cancellationToken);
        }

        return name;
    }

    /// <summary>取当前账号的背景图；没有返回 false。</summary>
    public bool TryResolve(string userId, out string path, out string contentType)
    {
        path = string.Empty;
        contentType = string.Empty;

        var directory = DirectoryFor(userId);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        // 一个账号只该有一个文件；万一剩了多个（历史上删失败），取最新的那个。
        var file = new DirectoryInfo(directory).GetFiles()
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (file is null)
        {
            return false;
        }

        path = file.FullName;
        contentType = BrandingImageStore.ContentTypeOf(file.Extension);
        return true;
    }

    /// <summary>删除当前账号的背景图（没有也当成功——删除是幂等的）。</summary>
    public void Delete(string userId)
    {
        var directory = DirectoryFor(userId);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(directory))
        {
            TryDelete(file);
        }
    }

    /// <summary>
    /// 账号 → 目录名：用哈希而不是用户名，避免路径里出现中文/特殊字符，
    /// 也避免 `../` 之类的路径穿越问题——目录名只可能是 32 位十六进制。
    /// </summary>
    private string DirectoryFor(string userId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userId));
        return Path.Combine(_root, Convert.ToHexString(hash)[..32].ToLowerInvariant());
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 删不掉不该让"上传新图"失败：新图会接着写，旧的留给下次清理。
        }
    }
}
