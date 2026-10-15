using IslandHub.Protocol.Dtos;
using IslandHub.Server.Services;

namespace IslandHub.Tests;

/// <summary>
/// 内容包合并（导入用）的回归测试。
///
/// <para>这里最要紧的是**去重键里必须有周次**：单双周本来就是「同一天、同一张时间表」的两张课表，
/// 只按「时间表 + 星期」去重会把它们并成一张——教室里单双周直接变成同一份课表，
/// 而导入过程一路「成功」，没有任何地方会报错。</para>
/// </summary>
public sealed class ContentMergerTests
{
    private const string LayoutId = "11111111-1111-4111-8111-111111111111";
    private const string SubjectId = "33333333-3333-4333-8333-333333333331";

    private static ContentBundleDto Bundle(params (string Name, int Interval, int Offset)[] plans)
    {
        var bundle = new ContentBundleDto
        {
            TimeLayouts =
            [
                new TimeLayoutDto
                {
                    Id = LayoutId,
                    Name = "夏季作息",
                    Items =
                    [
                        new TimeLayoutItemDto { StartTime = "08:00:00", EndTime = "08:45:00", Kind = TimeItemKind.Class },
                        new TimeLayoutItemDto { StartTime = "08:55:00", EndTime = "09:40:00", Kind = TimeItemKind.Class },
                    ],
                },
            ],
            Subjects = [new SubjectDto { Id = SubjectId, Name = "语文", Initial = "语" }],
        };

        for (var i = 0; i < plans.Length; i++)
        {
            bundle.ClassPlans.Add(new ClassPlanDto
            {
                Id = $"22222222-2222-4222-8222-2222222222{i:D2}",
                Name = plans[i].Name,
                TimeLayoutId = LayoutId,
                DaysOfWeek = [1],
                WeekInterval = plans[i].Interval,
                WeekOffset = plans[i].Offset,
                Slots = [new ClassPlanSlotDto { Index = 0, SubjectId = SubjectId, IsEnabled = true }],
            });
        }

        return bundle;
    }

    [Fact]
    public void 单双周的两张课表不会被合并成一张()
    {
        var target = Bundle(("周一课表", 0, 0));
        var incoming = Bundle(("周一课表（第 1 周）", 2, 0), ("周一课表（第 2 周）", 2, 1));

        var result = ContentMerger.Merge(target, incoming, includeClassPlans: true);

        Assert.Equal(2, result.AddedClassPlans);
        Assert.Equal(0, result.UpdatedClassPlans);
        Assert.Equal(3, target.ClassPlans.Count);
        Assert.Contains(target.ClassPlans, p => p.WeekInterval == 2 && p.WeekOffset == 0);
        Assert.Contains(target.ClassPlans, p => p.WeekInterval == 2 && p.WeekOffset == 1);
    }

    [Fact]
    public void 反复导入同一份不会产生重复课表()
    {
        var target = Bundle();
        var incoming = Bundle(("周一课表（第 1 周）", 2, 0), ("周一课表（第 2 周）", 2, 1));

        ContentMerger.Merge(target, incoming, includeClassPlans: true);
        var again = ContentMerger.Merge(target, incoming, includeClassPlans: true);

        Assert.Equal(0, again.AddedClassPlans);
        Assert.Equal(2, again.UpdatedClassPlans);
        Assert.Equal(2, target.ClassPlans.Count);
    }

    [Fact]
    public void 轮换周期取更长的那个且起点与偏移以导入包为准()
    {
        var target = Bundle(("周一课表（第 1 周）", 2, 0));
        target.Rotation.Weeks = 2;

        var incoming = Bundle(("周一课表（第 3 周）", 3, 2));
        incoming.Rotation.Weeks = 3;
        incoming.Rotation.SemesterStartDate = "2026-09-06";
        incoming.Rotation.Offset = -1;

        ContentMerger.Merge(target, incoming, includeClassPlans: true);

        Assert.Equal(3, target.Rotation.Weeks);
        Assert.Equal("2026-09-06", target.Rotation.SemesterStartDate);
        Assert.Equal(-1, target.Rotation.Offset);
        // 原来的 2 周课表 + 新导入的第 3 周课表，各占一份。
        Assert.Equal(2, target.ClassPlans.Count);
    }

    [Fact]
    public void 导入包没带起点时保留档案原有的轮换起点()
    {
        var target = Bundle(("周一课表", 0, 0));
        target.Rotation.SemesterStartDate = "2026-09-06";

        var incoming = Bundle(("周一课表（第 1 周）", 2, 0));
        incoming.Rotation.Weeks = 2;

        ContentMerger.Merge(target, incoming, includeClassPlans: true);

        Assert.Equal("2026-09-06", target.Rotation.SemesterStartDate);
        Assert.Equal(2, target.Rotation.Weeks);
    }

    [Fact]
    public void 每周的课表与单双周课表互不覆盖()
    {
        var target = Bundle(("周一课表", 0, 0));

        var incoming = Bundle(("周一课表", 0, 0));
        var result = ContentMerger.Merge(target, incoming, includeClassPlans: true);

        // 同一天、同周期、同周次才算同一张；这里正好命中「就地替换」。
        Assert.Equal(1, result.UpdatedClassPlans);
        Assert.Single(target.ClassPlans);
    }
}
