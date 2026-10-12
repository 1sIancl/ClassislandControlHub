using System.IO.Compression;
using ControlHub.Server.Services;

namespace ControlHub.Tests;

/// <summary>
/// 插件市场代理缓存（#121）里的纯逻辑：镜像顺序、<c>{root}</c> 展开、缓存路径清洗与包内清单解析。
/// <para>这些函数都被「下载一个来自互联网的文件」这条链路依赖，而端到端用例不好覆盖
/// 「镜像给的是错误页」「pluginId 里带 ../」这类输入——放在这里断言更划算。</para>
/// </summary>
public sealed class PluginMarketTests
{
    private static Dictionary<string, string> Mirrors() => new()
    {
        ["github"] = "https://github.com",
        ["moeyy"] = "https://github.moeyy.xyz/https://github.com",
        ["ghproxy"] = "https://ghfast.top/https://github.com",
    };

    [Fact]
    public void 首选镜像排在最前()
    {
        var ordered = PluginMirrorResolver.Order(Mirrors(), "moeyy");

        Assert.Equal("moeyy", ordered[0].Name);
        Assert.Equal(3, ordered.Count);
    }

    [Fact]
    public void 自动顺序把代理镜像排在GitHub直连之前()
    {
        // 校园网里 github.com 直连经常不通；代理只是多一跳，失败了会自己往后走。
        var names = PluginMirrorResolver.Order(Mirrors(), string.Empty).Select(e => e.Name).ToList();

        Assert.Equal("ghproxy", names[0]);
        Assert.Equal("github", names[^1]);
    }

    [Fact]
    public void 认不出的首选镜像退化为自动顺序()
    {
        var names = PluginMirrorResolver.Order(Mirrors(), "不存在的镜像").Select(e => e.Name).ToList();

        Assert.Equal(3, names.Count);
        Assert.Equal("ghproxy", names[0]);
    }

    [Fact]
    public void 展开root占位符并容忍末尾斜杠()
    {
        Assert.Equal("https://ghfast.top/https://github.com/a/b.cipx",
            PluginMirrorResolver.Expand("{root}/a/b.cipx", "https://ghfast.top/https://github.com/"));
        // 镜像根写空 / 模板写空都不能抛异常（索引是外部数据，坏值要能容忍）。
        Assert.Equal(string.Empty, PluginMirrorResolver.Expand(string.Empty, "https://x"));
        Assert.Equal("/a.cipx", PluginMirrorResolver.Expand("/a.cipx", string.Empty));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("a/b")]
    [InlineData("a:b*c?")]
    public void 缓存路径段不允许出现目录分隔符与上跳(string raw)
    {
        var safe = PluginPackageCacheService.SafeSegment(raw);

        Assert.DoesNotContain("/", safe);
        Assert.DoesNotContain("\\", safe);
        Assert.DoesNotContain("..", safe);
        Assert.DoesNotContain(":", safe);
        Assert.NotEqual(string.Empty, safe);
    }

    [Fact]
    public void 全是非法字符时回落到下划线()
    {
        Assert.Equal("_", PluginPackageCacheService.SafeSegment(".."));
        Assert.Equal("_", PluginPackageCacheService.SafeSegment("   "));
    }

    [Fact]
    public void 正常插件ID与版本保持原样()
    {
        Assert.Equal("bywhite.mediaisland", PluginPackageCacheService.SafeSegment("bywhite.mediaisland"));
        Assert.Equal("1.0.0.0", PluginPackageCacheService.SafeSegment("1.0.0.0"));
    }

    [Fact]
    public void 能从cipx里读出插件ID与版本()
    {
        var path = WriteTempPackage("id: Some.Plugin\nname: 某插件\nversion: 1.2.3.0\napiVersion: 2.1.1.1\n");

        var (id, version, error) = PluginPackageCacheService.ReadManifest(path);

        Assert.Null(error);
        Assert.Equal("Some.Plugin", id);
        Assert.Equal("1.2.3.0", version);
        File.Delete(path);
    }

    [Fact]
    public void 不是zip的文件给出错误而不是抛异常()
    {
        // 镜像返回错误页时也会「下载成功」，这一步必须能识别出来。
        var path = Path.Combine(Path.GetTempPath(), $"controlhub-test-{Guid.NewGuid():N}.cipx");
        File.WriteAllText(path, "<html>404 Not Found</html>");

        var (id, _, error) = PluginPackageCacheService.ReadManifest(path);

        Assert.Equal(string.Empty, id);
        Assert.False(string.IsNullOrWhiteSpace(error));
        File.Delete(path);
    }

    [Fact]
    public void zip里没有manifest时给出错误()
    {
        var path = Path.Combine(Path.GetTempPath(), $"controlhub-test-{Guid.NewGuid():N}.cipx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("readme.md");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("没有 manifest.yml");
        }

        var (_, _, error) = PluginPackageCacheService.ReadManifest(path);

        Assert.Contains("manifest", error ?? string.Empty);
        File.Delete(path);
    }

    private static string WriteTempPackage(string manifest)
    {
        var path = Path.Combine(Path.GetTempPath(), $"controlhub-test-{Guid.NewGuid():N}.cipx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("manifest.yml");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(manifest);
        return path;
    }
}
