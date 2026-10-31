using IslandHub.Protocol;

namespace IslandHub.Tests;

/// <summary>
/// 可远程管理的配置文件路径判定。
///
/// <para>
/// 这些用例守的是**放开写文件之后唯一的信任边界**：指令能从 API 直接发，
/// 界面层的任何限制都等于没有。所以每条越权路径都得有对应用例。
/// </para>
/// </summary>
public class ConfigPathPolicyTests
{
    private static readonly string Root = Path.Combine(
        Path.GetTempPath(), "islandhub-config-policy-tests");

    [Theory]
    [InlineData("ComponentLayouts/Default.json")]
    [InlineData("Automations/Default.json")]
    [InlineData("Plugins/classisland.injector/settings.json")]
    [InlineData("Management/Policy.json")]
    [InlineData("Themes/custom.json")]
    [InlineData("EnabledThemes.json")]
    public void 白名单内的配置允许读写(string relative)
    {
        var ok = ConfigPathPolicy.TryResolve(Root, relative, out var full, out var reason);

        Assert.True(ok, reason);
        Assert.StartsWith(Path.GetFullPath(Root), full, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../secrets.json")]
    [InlineData("ComponentLayouts/../../secrets.json")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("")]
    [InlineData("   ")]
    public void 越界与非法路径一律拒绝(string relative)
    {
        Assert.False(ConfigPathPolicy.TryResolve(Root, relative, out _, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void 白名单外的目录拒绝且说明允许范围()
    {
        // PluginsIndex 是几十 MB 的市场缓存，改了没意义，不该出现在可写范围里。
        Assert.False(ConfigPathPolicy.TryResolve(Root, "PluginsIndex/Default/index.json", out _, out var reason));
        Assert.Contains("不在可远程管理的范围内", reason);
        Assert.Contains("ComponentLayouts", reason);
    }

    [Fact]
    public void 备份文件不给碰()
    {
        // 写坏了要能靠 .bak 回滚；允许远程改 .bak 等于把回滚余地也交出去。
        Assert.False(ConfigPathPolicy.TryResolve(Root, "ComponentLayouts/Default.json.bak", out _, out var reason));
        Assert.Contains("备份文件", reason);
    }

    [Fact]
    public void 凭据文件即使在白名单目录里也拒绝()
    {
        Assert.False(ConfigPathPolicy.TryResolve(Root, "Management/Credentials.json", out _, out var reason));
        Assert.Contains("凭据", reason);
    }

    [Fact]
    public void 名字相近的兄弟目录不会被前缀骗过()
    {
        // `ComponentLayoutsEvil` 以 `ComponentLayouts` 开头，但首段不在白名单里。
        Assert.False(ConfigPathPolicy.TryResolve(Root, "ComponentLayoutsEvil/x.json", out _, out var reason));
        Assert.Contains("不在可远程管理的范围内", reason);
    }

    [Fact]
    public void 配置根目录缺失时给出可读原因()
    {
        Assert.False(ConfigPathPolicy.TryResolve(null, "ComponentLayouts/Default.json", out _, out var reason));
        Assert.Contains("配置目录", reason);
    }
}
