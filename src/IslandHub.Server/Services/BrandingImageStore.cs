using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Http;
using IslandHub.Server.Options;
using Microsoft.Extensions.Options;

namespace IslandHub.Server.Services;

/// <summary>
/// 品牌图片（Logo / 浏览器图标 / 登录壁纸）的落盘与读取。
///
/// <para><b>为什么要有它</b>：品牌图片原先只能填**外部地址**，于是管理员要先找个图床，
/// 而图床常见的两个问题都会直接变成「壁纸不好用」——防盗链（换域名/换协议就 403）、
/// 校内网打不开或限速。支持本地上传后，图片存在服务端自己身上，这些问题一并消失。</para>
///
/// <para><b>落在 data 目录而不是 wwwroot</b>：wwwroot 是构建产物，每次发版都会被整目录覆盖，
/// 放这里的图片下一次更新就没了。data 目录不参与部署覆盖。</para>
///
/// <para><b>为什么按文件头判定类型</b>：不采信客户端申报的 MIME 与扩展名。
/// 否则把一个 HTML 改名成 <c>logo.png</c> 传上来，会被当成图片原样发回浏览器，
/// 变成同源 XSS。另外**不接受 SVG**——SVG 能内嵌脚本，同源发出去等于把脚本能力交给了任何人。</para>
/// </summary>
public sealed class BrandingImageStore
{
    /// <summary>站内图片地址前缀。品牌配置里存的就是 <c>{前缀}{文件名}</c>。</summary>
    public const string UrlPrefix = "/api/v1/branding/img/";

    /// <summary>单张图片大小上限。登录壁纸常常是 2K/4K 图，8MB 足够且不至于把磁盘吃满。</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>文件名规则：32 位十六进制 + 白名单扩展名。</summary>
    private static readonly System.Text.RegularExpressions.Regex NamePattern = new(
        @"^[0-9a-f]{32}\.(png|jpg|webp|gif|avif)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly string _root;

    public BrandingImageStore(IOptions<ServerOptions> options, IWebHostEnvironment environment)
    {
        _root = Path.Combine(options.Value.ResolveDataDirectory(environment.ContentRootPath), "branding");
    }

    /// <summary>
    /// 保存一张图片：先按文件头判定类型，再落盘，返回可直接写进品牌配置的站内地址。
    /// </summary>
    /// <exception cref="HubException">类型不支持或超过大小上限。</exception>
    public async Task<BrandingImageDto> SaveAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        // 先读 32 字节头用于判定类型；读不够就直接判定为无效图片。
        var header = new byte[32];
        var read = await ReadAtLeastAsync(source, header, header.Length, cancellationToken);
        var detected = Detect(header.AsSpan(0, read));
        if (detected is null)
        {
            throw HubException.Validation(
                "只支持 PNG / JPEG / WebP / GIF / AVIF（按文件内容判定，改扩展名不行）；SVG 不收——它能内嵌脚本。");
        }

        var (extension, contentType) = detected.Value;
        // 用 Guid.NewGuid():N 而不是 HubChecksum.NewId()：后者返回的是**字符串**，
        // 对字符串写 ":N" 格式串不会去掉横线，于是文件名变成带横线的 GUID，
        // 与下面 NamePattern 要求的 32 位无横线十六进制对不上——
        // 表现是「上传成功返回了地址，但取回来永远 404」。
        var name = $"{Guid.NewGuid():N}.{extension}";
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);

        long size;
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // 头已经读走了，先写回去；总大小仍然卡上限，避免「判定通过后传 1GB」。
            await file.WriteAsync(header.AsMemory(0, read), cancellationToken);
            size = read + await CopyBoundedAsync(source, file, MaxBytes - read, cancellationToken);
        }

        return new BrandingImageDto
        {
            Url = UrlPrefix + name,
            Name = name,
            ContentType = contentType,
            Size = size,
        };
    }

    /// <summary>把文件名解析成本地文件路径与内容类型；不是本站存的图片返回 false。</summary>
    public bool TryResolve(string? name, out string path, out string contentType)
    {
        path = string.Empty;
        contentType = string.Empty;
        if (string.IsNullOrWhiteSpace(name) || !NamePattern.IsMatch(name))
        {
            return false;
        }

        // 文件名规则已经把目录分隔符挡掉了，这里拼全路径只为让「绝不越出目录」这件事显式。
        var full = Path.GetFullPath(Path.Combine(_root, name));
        if (!File.Exists(full))
        {
            return false;
        }

        path = full;
        contentType = ContentTypeOf(Path.GetExtension(name));
        return true;
    }

    /// <summary>地址是否指向本服务托管的图片（外部图床的地址不能删）。</summary>
    public static bool IsManagedUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Contains(UrlPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 清理不再被引用的站内图片。
    ///
    /// <para>为什么要清：每次换图都会留下一个文件，而品牌配置里只保留最后一个地址——
    /// 不清理的话，管理员换十次壁纸，磁盘里就躺着十张没人引用的图（4K 壁纸一张能到几 MB）。</para>
    /// </summary>
    public void PruneExcept(IEnumerable<string?> referencedUrls)
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in referencedUrls)
        {
            if (!IsManagedUrl(url))
            {
                continue;
            }

            var start = url!.IndexOf(UrlPrefix, StringComparison.Ordinal) + UrlPrefix.Length;
            var name = url[start..];
            if (NamePattern.IsMatch(name))
            {
                keep.Add(name);
            }
        }

        foreach (var file in Directory.EnumerateFiles(_root))
        {
            var name = Path.GetFileName(file);
            if (!NamePattern.IsMatch(name) || keep.Contains(name))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 正被浏览器读着是常事（刚换掉的那张可能还有页面在显示），删不掉就下次保存时再清。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ────────────────────────────── 内部 ──────────────────────────────

    /// <summary>复制剩余内容，但总量超过 <paramref name="budget"/> 就判定超限。</summary>
    private static async Task<long> CopyBoundedAsync(
        Stream source, Stream target, long budget, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return total;
            }

            total += read;
            if (total > budget)
            {
                throw HubException.Validation($"图片超过 {MaxBytes / 1024 / 1024}MB 上限。");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    /// <summary>尽量读满 <paramref name="count"/> 字节（读不满也要把已读数量返回去）。</summary>
    private static async Task<int> ReadAtLeastAsync(
        Stream source, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>按文件头判定图片类型；认不出返回 null。</summary>
    private static (string Extension, string ContentType)? Detect(ReadOnlySpan<byte> header)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        // JPEG: FF D8 FF
        ReadOnlySpan<byte> jpeg = [0xFF, 0xD8, 0xFF];
        ReadOnlySpan<byte> gif87 = "GIF87a"u8;
        ReadOnlySpan<byte> gif89 = "GIF89a"u8;
        // WebP: "RIFF" .... "WEBP"
        ReadOnlySpan<byte> riff = "RIFF"u8;
        ReadOnlySpan<byte> webp = "WEBP"u8;

        if (header.Length >= 8 && header[..8].SequenceEqual(png))
        {
            return ("png", "image/png");
        }

        if (header.Length >= 3 && header[..3].SequenceEqual(jpeg))
        {
            return ("jpg", "image/jpeg");
        }

        if (header.Length >= 6 && (header[..6].SequenceEqual(gif87) || header[..6].SequenceEqual(gif89)))
        {
            return ("gif", "image/gif");
        }

        if (header.Length >= 12 && header[..4].SequenceEqual(riff) && header.Slice(8, 4).SequenceEqual(webp))
        {
            return ("webp", "image/webp");
        }

        // AVIF：4~8 是 "ftyp"，紧跟的品牌是 avif / avis（heic 之类一律不收）
        if (header.Length >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = header.Slice(8, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
            {
                return ("avif", "image/avif");
            }
        }

        return null;
    }

    private static string ContentTypeOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".avif" => "image/avif",
        _ => "application/octet-stream",
    };
}