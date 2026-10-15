using IslandHub.Protocol;

namespace IslandHub.Tests;

/// <summary>
/// 提醒内容里的模板变量（`#11` 的一部分）。
/// <para>值得单测的理由：它直接决定老师屏幕上看到什么，而渲染发生的时间点是「容易写错」的地方
/// （保存时渲染会把第一次的值固化）。这里把「什么时候渲染出什么」钉住。</para>
/// </summary>
public sealed class ReminderTemplateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 2, 30, 0, TimeSpan.Zero);

    [Fact]
    public void 时刻日期与台数都会被替换()
    {
        var text = ReminderTemplate.Render("上课时间 {time}（{date}），{targets} 共 {count} 台已收到",
            Now, "教室「高一(1)班」", 3);

        // 本地时区：断言里只钉「变量没了、真实值在」，避免依赖跑测试的机器时区。
        Assert.DoesNotContain("{time}", text);
        Assert.DoesNotContain("{count}", text);
        Assert.Contains("高一(1)班", text);
        Assert.Contains("共 3 台", text);
        Assert.Contains("2026-10-", text);
    }

    [Fact]
    public void 大小写都认()
    {
        var text = ReminderTemplate.Render("{Time} / {COUNT}", Now, "全部教室", 2);

        Assert.DoesNotContain("{Time}", text);
        Assert.DoesNotContain("{COUNT}", text);
        Assert.Contains("2", text);
    }

    [Fact]
    public void 写错的占位符原样保留()
    {
        // 静默删掉的话，管理员要等现场问「这里怎么空了」才发现拼错了。
        var text = ReminderTemplate.Render("开始于 {tme} 请注意", Now, "全部教室", 1);

        Assert.Contains("{tme}", text);
    }

    [Fact]
    public void 没有变量或内容为空时不改变原文本()
    {
        Assert.Equal("明天带实验报告", ReminderTemplate.Render("明天带实验报告", Now, "全部教室", 4));
        Assert.Equal(string.Empty, ReminderTemplate.Render(null, Now, "全部教室", 4));
        Assert.Equal(string.Empty, ReminderTemplate.Render(string.Empty, Now, "全部教室", 4));
    }

    [Fact]
    public void 目标描述为空时给出兜底文案()
    {
        var text = ReminderTemplate.Render("送达到 {targets}", Now, null, 1);

        Assert.Contains("全部教室", text);
    }
}
