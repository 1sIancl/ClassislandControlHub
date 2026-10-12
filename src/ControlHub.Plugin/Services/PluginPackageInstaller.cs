using System.IO.Compression;
using System.Security.Cryptography;
using ControlHub.Protocol;
using ClassIsland.Core;

namespace ControlHub.Plugin.Services;

/// <summary>
/// 把 A 端下发的插件包放进宿主的**待安装目录**（#121）。
/// <para>ClassIsland 没有「安装我这个文件」的公开 API：它的安装时机是**启动时**
/// （<c>PluginService.ProcessPluginsInstall</c> 会在下次启动时把
/// <c>&lt;缓存目录&gt;/PluginPackages/*.cipx</c> 解压到插件目录）。
/// 所以这里做的事就是：校验 → 落盘到那个目录 → 告诉管理员「需要重启才生效」。</para>
/// <para>刻意做成无宿主界面依赖的静态类：验证台可以直接喂字节进来跑全部分支。</para>
/// </summary>
public static class PluginPackageInstaller
{
    /// <summary>宿主读取待安装包的目录名（在 ClassIsland 的缓存目录下）。</summary>
    private const string PackagesFolderName = "PluginPackages";

    /// <summary>宿主能接受的最低插件 API 版本（低于它会被宿主直接拒绝加载）。</summary>
    private static readonly Version MinimumApiVersion = new(2, 0, 0, 0);

    /// <summary>安装结果的三种结局：落盘成功 / 校验没过 / 环境不支持。</summary>
    public sealed record Outcome(bool Ok, string Message, string? ErrorCode = null);

    /// <summary>宿主的待安装目录（不存在时创建）。</summary>
    public static string ResolvePendingDirectory()
    {
        // 与宿主的 PluginService.PluginsPkgRootPath 保持同一算法：
        // CommonDirectories.AppCacheFolderPath + "PluginPackages"。
        var directory = Path.Combine(CommonDirectories.AppCacheFolderPath, PackagesFolderName);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// 只做校验、不落盘：返回 <c>null</c> 表示这个包可以安装。
    /// <para>与 <see cref="Install"/> 分开是为了让验证台能逐条覆盖校验分支——
    /// 那些分支不碰文件系统，测起来才是确定的。</para>
    /// </summary>
    public static Outcome? Validate(byte[] package, string pluginId, string version, string sha256) =>
        ValidateCore(package, pluginId, version, sha256, out _);

    /// <summary>
    /// 校验并落盘。失败时返回的 <see cref="Outcome.ErrorCode"/> 是给管理端看的分类。
    /// </summary>
    /// <param name="package">包字节。</param>
    /// <param name="pluginId">期望的插件 ID。</param>
    /// <param name="version">期望的版本。</param>
    /// <param name="sha256">A 端给出的 SHA256（为空表示不校验）。</param>
    public static Outcome Install(byte[] package, string pluginId, string version, string sha256)
    {
        var invalid = ValidateCore(package, pluginId, version, sha256, out var info);
        if (invalid is not null || info is null)
        {
            return invalid ?? new Outcome(false, "插件包无法解析。", CommandErrorCodes.PayloadInvalid);
        }

        try
        {
            var directory = ResolvePendingDirectory();
            // 文件名与宿主市场安装保持一致（<插件 ID>.cipx）：宿主启动时按 manifest 里的 id
            // 解压到插件目录，名字对不上不会出错，但一致的话排查现场更省事。
            var target = Path.Combine(directory, info.Id + ".cipx");
            File.WriteAllBytes(target, package);
            return new Outcome(true,
                $"已下载并放入待安装目录（{target}），**重启 ClassIsland 后生效**。");
        }
        catch (Exception ex)
        {
            return new Outcome(false, "写入待安装目录失败：" + ex.Message, CommandErrorCodes.ExecutionFailed);
        }
    }

    private static Outcome? ValidateCore(byte[] package, string pluginId, string version, string sha256,
        out PluginPackageInfo? info)
    {
        info = null;

        if (package.Length == 0)
        {
            return new Outcome(false, "取回的插件包是空的。", CommandErrorCodes.PayloadInvalid);
        }

        if (!string.IsNullOrWhiteSpace(sha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(package));
            if (!actual.Equals(sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // 单独分类：这多半是 A 端缓存或传输出了问题，处置办法是「重新缓存该包」，
                // 而不是去教室机上折腾——所以不能和一般的执行失败混在一起。
                return new Outcome(false,
                    $"插件包完整性校验失败（SHA256 应为 {sha256.Trim()}，实际 {actual}），请在管理端重新缓存该插件包。",
                    CommandErrorCodes.IntegrityFailed);
            }
        }

        try
        {
            info = ReadPackageInfo(package);
        }
        catch (Exception ex)
        {
            return new Outcome(false, "这个文件不是有效的 .cipx：" + ex.Message,
                CommandErrorCodes.PayloadInvalid);
        }

        if (info.Id.Length == 0)
        {
            return new Outcome(false, "包内 manifest.yml 缺少 id。", CommandErrorCodes.PayloadInvalid);
        }

        if (pluginId.Length > 0 && !info.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase))
        {
            return new Outcome(false, $"包内插件 ID 是 {info.Id}，与下发的 {pluginId} 不符。",
                CommandErrorCodes.PayloadInvalid);
        }

        if (info.Version.Length > 0 && version.Length > 0
            && !info.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
        {
            return new Outcome(false, $"包内版本是 {info.Version}，与下发的 {version} 不符。",
                CommandErrorCodes.PayloadInvalid);
        }

        if (Version.TryParse(info.ApiVersion, out var apiVersion) && apiVersion < MinimumApiVersion)
        {
            return new Outcome(false,
                $"插件要求的 API 版本 {info.ApiVersion} 低于宿主支持的 {MinimumApiVersion}。",
                CommandErrorCodes.Unsupported);
        }

        var hostVersion = HostVersion();
        if (apiVersion is not null && hostVersion is not null && hostVersion < apiVersion)
        {
            return new Outcome(false,
                $"这台教室端的 ClassIsland {hostVersion} 低于插件要求的 {apiVersion}，请先升级 ClassIsland。",
                CommandErrorCodes.Unsupported);
        }

        if (info.Platforms.Count > 0
            && !info.Platforms.Any(p => p.Contains("Windows", StringComparison.OrdinalIgnoreCase)))
        {
            return new Outcome(false,
                $"插件声明仅支持 {string.Join(" / ", info.Platforms)}，这台教室端是 Windows。",
                CommandErrorCodes.PlatformUnsupported);
        }

        return null;
    }

    /// <summary>读包内的 manifest.yml（扁平字段逐行解析，不引 YAML 依赖）。</summary>
    public static PluginPackageInfo ReadPackageInfo(byte[] package)
    {
        using var stream = new MemoryStream(package, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e =>
                        e.FullName.Equals("manifest.yml", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("包内没有 manifest.yml");

        using var reader = new StreamReader(entry.Open());
        var info = new PluginPackageInfo();
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
                case "id": info.Id = value; break;
                case "name": info.Name = value; break;
                case "version": info.Version = value; break;
                case "apiVersion": info.ApiVersion = value; break;
                case "supportedOSPlatforms":
                    // 形如 [Windows] 或 [Windows, Linux]；解析失败按「未声明」处理（宿主默认全平台）。
                    info.Platforms = value
                        .Trim('[', ']', ' ')
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                    break;
            }
        }

        return info;
    }

    /// <summary>当前宿主的 ClassIsland 版本（取不到时返回 null，跳过版本判断）。</summary>
    private static Version? HostVersion() =>
        typeof(ClassIsland.Core.Abstractions.PluginBase).Assembly.GetName().Version;

    /// <summary>包内 manifest 的摘要。</summary>
    public sealed class PluginPackageInfo
    {
        /// <summary>插件 ID。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>版本。</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>要求的最低宿主 API 版本。</summary>
        public string ApiVersion { get; set; } = string.Empty;

        /// <summary>声明的支持平台（未声明时为空）。</summary>
        public List<string> Platforms { get; set; } = [];
    }
}
