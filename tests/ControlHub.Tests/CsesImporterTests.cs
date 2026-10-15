using ControlHub.Protocol.Dtos;
using ControlHub.Server.Services;

namespace ControlHub.Tests;

/// <summary>
/// CSES 导入的周次映射。
///
/// <para>CSES 里 <c>weeks: odd / even</c> 表示单双周，而单双周的两份内容**同属一天**——
/// 早先的实现按「先出现的先占位」把它们并成一张课表，等于把单双周直接抹平；
/// 这里钉住「同一天生成两张、周期自动设为 2」这条语义。</para>
/// </summary>
public sealed class CsesImporterTests
{
    private const string OddEvenYaml = """
    version: 1
    subjects:
      - name: 语文
      - name: 数学
    schedules:
      - name: 周一
        enable_day: 1
        weeks: odd
        classes:
          - subject: 语文
            start_time: 08:00
            end_time: 08:45
      - name: 周一（双周）
        enable_day: 1
        weeks: even
        classes:
          - subject: 数学
            start_time: 08:00
            end_time: 08:45
      - name: 周二
        enable_day: 2
        weeks: all
        classes:
          - subject: 语文
            start_time: 08:00
            end_time: 08:45
    """;

    private const string UnknownWeeksYaml = """
    version: 1
    subjects:
      - name: 语文
    schedules:
      - name: 周三
        enable_day: 3
        weeks: 1,3,5
        classes:
          - subject: 语文
            start_time: 08:00
            end_time: 08:45
    """;

    /// <summary>取某一天、某一周的课表。</summary>
    private static ClassPlanDto? PlanOf(ContentBundleDto content, int day, int week)
    {
        return content.ClassPlans.FirstOrDefault(p =>
            p.DaysOfWeek.Count == 1 && p.DaysOfWeek[0] == day
            && ((p.WeekInterval < 2 ? 1 : p.WeekOffset + 1) == week));
    }

    private static string SubjectName(ContentBundleDto content, ClassPlanDto plan) =>
        content.Subjects.First(s => s.Id == plan.Slots[0].SubjectId).Name;

    [Fact]
    public void 单双周生成同一天的两张课表并把周期设为2()
    {
        var content = CsesImporter.ToContent(CsesImporter.Parse(OddEvenYaml));

        Assert.Equal(3, content.ClassPlans.Count);
        Assert.Equal(2, content.Rotation.Weeks);

        var odd = PlanOf(content, 1, 1);
        var even = PlanOf(content, 1, 2);
        Assert.NotNull(odd);
        Assert.NotNull(even);
        Assert.Equal(2, odd!.WeekInterval);
        Assert.Equal(0, odd.WeekOffset);
        Assert.Equal(2, even!.WeekInterval);
        Assert.Equal(1, even.WeekOffset);
        Assert.Equal("语文", SubjectName(content, odd));
        Assert.Equal("数学", SubjectName(content, even));
        Assert.Contains("第 2 周", even.Name);
    }

    [Fact]
    public void 每周的课表保持不轮换()
    {
        var content = CsesImporter.ToContent(CsesImporter.Parse(OddEvenYaml));

        var tuesday = PlanOf(content, 2, 1);

        Assert.NotNull(tuesday);
        Assert.Equal(0, tuesday!.WeekInterval);
        Assert.Equal(0, tuesday.WeekOffset);
        Assert.Equal("语文", SubjectName(content, tuesday));
    }

    [Fact]
    public void 认不出来的周次按每周处理并留痕()
    {
        var content = CsesImporter.ToContent(CsesImporter.Parse(UnknownWeeksYaml));

        var plan = Assert.Single(content.ClassPlans);
        Assert.Equal(0, plan.WeekInterval);
        Assert.Equal(1, content.Rotation.Weeks);

        // 不能让人以为「1,3,5」已经按周次导进来了。
        Assert.True(content.Settings.Values.ContainsKey("cses.weeksUnmapped"));
        Assert.Contains("1,3,5", content.Settings.Values["cses.weeksUnmapped"]);
    }
}
