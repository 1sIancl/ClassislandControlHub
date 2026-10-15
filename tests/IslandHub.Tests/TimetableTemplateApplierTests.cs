using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;

namespace IslandHub.Tests;

/// <summary>
/// 课表模板套用（`#9`）。
/// <para>这一段的规则都很容易「看起来对、实际把课表改错」，所以逐条钉住：按名称匹配、
/// 找不到**保留原科目**而不是清空、多天不一致**跳过**而不是猜。</para>
/// </summary>
public sealed class TimetableTemplateApplierTests
{
    /// <summary>造一份最简单的档案：3 个科目 + 一张覆盖周一到周五的课表（3 节）。</summary>
    private static ContentBundleDto Content(params int[] days)
    {
        return new ContentBundleDto
        {
            Subjects =
            [
                new SubjectDto { Id = "s-chinese", Name = "语文" },
                new SubjectDto { Id = "s-math", Name = "数学" },
                new SubjectDto { Id = "s-english", Name = "英语" },
            ],
            ClassPlans =
            [
                new ClassPlanDto
                {
                    Id = "plan-1",
                    Name = "周一至周五",
                    DaysOfWeek = days.Length > 0 ? [.. days] : [],
                    Slots =
                    [
                        new ClassPlanSlotDto { Index = 0, SubjectId = "s-english" },
                        new ClassPlanSlotDto { Index = 1, SubjectId = "s-english" },
                        new ClassPlanSlotDto { Index = 2, SubjectId = "s-english" },
                    ],
                },
            ],
        };
    }

    private static Dictionary<string, List<string?>> Grid(params (string Day, string?[] Column)[] days) =>
        days.ToDictionary(d => d.Day, d => d.Column.ToList());

    /// <summary>把课表的科目 ID 串起来比对（<c>null</c> 打成「（无）」——比数组断言更好读）。</summary>
    private static string Slots(ContentBundleDto content) => string.Join(",",
        content.ClassPlans[0].Slots.Select(s => s.SubjectId ?? "（无）"));

    [Fact]
    public void 按科目名称套用到覆盖该天的课表()
    {
        var content = Content(1, 2, 3, 4, 5);
        var grid = Grid(("1", ["语文", "数学", "英语"]), ("2", ["语文", "数学", "英语"]));

        var outcome = TimetableTemplateApplier.Apply(content, grid, 3);

        Assert.True(outcome.Changed);
        Assert.Single(outcome.UpdatedPlans);
        Assert.Equal([1, 2], outcome.UpdatedDays);
        Assert.Equal("s-chinese,s-math,s-english", Slots(content));
        Assert.Empty(outcome.MissingSubjects);
    }

    [Fact]
    public void 找不到同名科目时保留原科目而不是清空()
    {
        var content = Content(1);
        var grid = Grid(("1", ["语文", "地理", null]));

        var outcome = TimetableTemplateApplier.Apply(content, grid, 3);

        // 第 1 节套上了语文；第 2 节「地理」档案里没有 → 保留原值（英语），只记一笔；
        // 第 3 节模板里是空的 → 该节设为无课。
        Assert.Equal("s-chinese,s-english,（无）", Slots(content));
        Assert.Contains("地理", outcome.MissingSubjects);
    }

    [Fact]
    public void 一张课表覆盖多天而模板里这几天不一致时跳过()
    {
        var content = Content(1, 2, 3, 4, 5);   // 一张课表覆盖周一~周五
        var grid = Grid(
            ("1", ["语文", "数学", "英语"]),
            ("2", ["数学", "语文", "英语"]));   // 周二与周一不同

        var outcome = TimetableTemplateApplier.Apply(content, grid, 3);

        Assert.False(outcome.Changed);
        Assert.Single(outcome.ConflictedPlans);
        // 关键：宁可什么都不改，也不能替管理员猜「以哪天为准」。
        Assert.All(content.ClassPlans[0].Slots, s => Assert.Equal("s-english", s.SubjectId));
    }

    [Fact]
    public void 模板里没有课表覆盖的天会被单独指出()
    {
        var content = Content(1);               // 只有周一
        var grid = Grid(("1", ["语文", null, null]), ("6", ["数学", null, null]));

        var outcome = TimetableTemplateApplier.Apply(content, grid, 3);

        Assert.Contains(6, outcome.UncoveredDays);
        Assert.Contains(1, outcome.UpdatedDays);
    }

    [Fact]
    public void 节数不一致会给出提示但仍按较短的套用()
    {
        var content = Content(1);               // 3 节
        var grid = Grid(("1", ["语文", "数学"]));  // 模板 2 节

        var outcome = TimetableTemplateApplier.Apply(content, grid, 2);

        Assert.Single(outcome.PeriodMismatches);
        Assert.Equal("s-chinese,s-math,s-english", Slots(content));
    }

    [Fact]
    public void 没有课表时不改动任何东西()
    {
        var content = Content();
        content.ClassPlans.Clear();

        var outcome = TimetableTemplateApplier.Apply(content, Grid(("1", ["语文"])), 1);

        Assert.False(outcome.Changed);
        Assert.Empty(outcome.UpdatedPlans);
    }
}
