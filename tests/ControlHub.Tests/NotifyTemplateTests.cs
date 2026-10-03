using ControlHub.Server.Data;
using ControlHub.Server.Services;

namespace ControlHub.Tests;

/// <summary>通知模板变量（#43）：一条通知发给多间教室时，变量必须按各自设备替换。</summary>
public sealed class NotifyTemplateTests
{
    private static DeviceRow Device() => new()
    {
        Id = "ci-1",
        Name = "教学楼A-301",
        MachineName = "PC-A301",
        IpAddress = "192.168.31.151",
    };

    private static readonly DateTimeOffset Noon = new(2026, 10, 3, 4, 5, 0, TimeSpan.Zero);

    [Fact]
    public void 变量按设备替换()
    {
        var text = NotifyTemplate.Apply("请 {教室名}（{机器名} / {分组} / {IP}）于 {时间} 关闭投影",
            Device(), "教学楼A", Noon);

        Assert.Contains("教学楼A-301", text);
        Assert.Contains("PC-A301", text);
        Assert.Contains("教学楼A", text);
        Assert.Contains("192.168.31.151", text);
        Assert.DoesNotContain("{教室名}", text);
        Assert.DoesNotContain("{分组}", text);
    }

    [Fact]
    public void 认不出的变量原样保留()
    {
        // 宁可让人在正文里看到 {班级} 发现写错了，也不要静默吃掉内容。
        var text = NotifyTemplate.Apply("{班级} 请注意", Device(), null, Noon);

        Assert.Contains("{班级}", text);
    }

    [Fact]
    public void 分组或IP为空时不留下残缺文本()
    {
        var device = Device();
        device.IpAddress = null;

        var text = NotifyTemplate.Apply("[{分组}][{IP}]", device, null, Noon);

        Assert.Equal("[][]", text);
    }

    [Fact]
    public void 空文本不会抛异常()
    {
        Assert.Equal(string.Empty, NotifyTemplate.Apply(null, Device(), null, Noon));
        Assert.Equal(string.Empty, NotifyTemplate.Apply(string.Empty, Device(), null, Noon));
    }

    [Fact]
    public void 变量清单与实现保持一致()
    {
        // 界面与文档都从这份清单取文案；这里钉住「清单里写的变量确实会被替换」。
        foreach (var (token, _) in NotifyTemplate.Variables)
        {
            var replaced = NotifyTemplate.Apply(token, Device(), "分组名", Noon);
            Assert.NotEqual(token, replaced);
        }
    }
}
