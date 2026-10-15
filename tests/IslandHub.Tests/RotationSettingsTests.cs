using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Services;

namespace IslandHub.Tests;

/// <summary>
/// 档案级「多周轮换」设置的校验与规范化。
///
/// <para>这份测试守的是**静默失效**：档案说 3 周轮换、某张课表却写着第 5 周，或者课表标了周次
/// 而档案没启用轮换——两种情况下配置文件都下发成功、教室端也照常运行，只是那张课表永远不满足
/// 触发条件（某一天直接空堂），而且从界面上完全看不出来。</para>
/// </summary>
public sealed class RotationSettingsTests
{
    private const string LayoutId = "11111111-1111-4111-8111-111111111111";

    /// <summary>造一份能通过规范化的最小内容包（一张时间表 + 若干课表）。</summary>
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
            Subjects =
            [
                new SubjectDto { Id = "33333333-3333-4333-8333-333333333331", Name = "语文" },
            ],
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
            });
        }

        return bundle;
    }

    [Fact]
    public void 默认不轮换时课表周次保持为零()
    {
        var content = Bundle(("周一", 0, 0));

        var notes = ContentNormalizer.Normalize(content);

        Assert.Equal(1, content.Rotation.Weeks);
        Assert.Null(content.Rotation.SemesterStartDate);
        Assert.Equal(0, content.Rotation.Offset);
        Assert.Equal(0, content.ClassPlans[0].WeekInterval);
        Assert.Equal(0, content.ClassPlans[0].WeekOffset);
        Assert.DoesNotContain(notes, n => n.Contains("轮换"));
    }

    [Fact]
    public void 轮换周期越界被夹取并留痕()
    {
        var tooBig = Bundle(("周一", 0, 0));
        tooBig.Rotation.Weeks = 12;
        var notes = ContentNormalizer.Normalize(tooBig);

        Assert.Equal(9, tooBig.Rotation.Weeks);
        Assert.Contains(notes, n => n.Contains("轮换周期"));

        // 0 / 负数按「不轮换」处理，而不是当成 0 周。
        var zero = Bundle(("周一", 0, 0));
        zero.Rotation.Weeks = 0;
        ContentNormalizer.Normalize(zero);

        Assert.Equal(1, zero.Rotation.Weeks);
    }

    [Fact]
    public void 轮换偏移越界被夹取()
    {
        var content = Bundle(("周一", 2, 0));
        content.Rotation.Weeks = 2;
        content.Rotation.Offset = 30;

        ContentNormalizer.Normalize(content);

        Assert.Equal(9, content.Rotation.Offset);
    }

    [Fact]
    public void 轮换起点被规范成横杠日期()
    {
        var content = Bundle(("周一", 2, 0));
        content.Rotation.Weeks = 2;
        content.Rotation.SemesterStartDate = "2026-9-6";

        ContentNormalizer.Normalize(content);

        Assert.Equal("2026-09-06", content.Rotation.SemesterStartDate);
    }

    [Fact]
    public void 轮换起点认不出来时清空并说明()
    {
        var content = Bundle(("周一", 2, 0));
        content.Rotation.Weeks = 2;
        content.Rotation.SemesterStartDate = "开学第一周";

        var notes = ContentNormalizer.Normalize(content);

        Assert.Null(content.Rotation.SemesterStartDate);
        Assert.Contains(notes, n => n.Contains("轮换起点"));
    }

    [Fact]
    public void 课表周期与档案不一致时按档案周期调整()
    {
        var content = Bundle(("周一", 2, 1));
        content.Rotation.Weeks = 3;

        var notes = ContentNormalizer.Normalize(content);

        Assert.Equal(3, content.ClassPlans[0].WeekInterval);
        Assert.Equal(1, content.ClassPlans[0].WeekOffset);
        Assert.Contains(notes, n => n.Contains("轮换周期"));
    }

    [Fact]
    public void 课表周次超出周期时夹回范围内()
    {
        var content = Bundle(("周一", 3, 7));
        content.Rotation.Weeks = 3;

        var notes = ContentNormalizer.Normalize(content);

        Assert.Equal(3, content.ClassPlans[0].WeekInterval);
        Assert.Equal(2, content.ClassPlans[0].WeekOffset);
        Assert.Contains(notes, n => n.Contains("第 3 周"));
    }

    [Fact]
    public void 未启用轮换的档案会清掉课表上的周次()
    {
        var content = Bundle(("周一", 2, 1));

        var notes = ContentNormalizer.Normalize(content);

        Assert.Equal(0, content.ClassPlans[0].WeekInterval);
        Assert.Equal(0, content.ClassPlans[0].WeekOffset);
        Assert.Contains(notes, n => n.Contains("已清除 1 张课表"));
    }

    [Fact]
    public void 每周生效的课表在轮换档案里保持不动()
    {
        var content = Bundle(("每周都上的周二", 0, 0), ("单周周三", 3, 0));
        content.Rotation.Weeks = 3;

        ContentNormalizer.Normalize(content);

        Assert.Equal(0, content.ClassPlans[0].WeekInterval);
        Assert.Equal(3, content.ClassPlans[1].WeekInterval);
    }

    [Fact]
    public void 关闭自动修复时非法值直接报错()
    {
        var content = Bundle(("周一", 0, 0));
        content.Rotation.Weeks = 12;

        var ex = Assert.Throws<HubException>(() => ContentNormalizer.Normalize(content, autoFix: false));

        Assert.Contains("轮换周期", ex.Message);
    }

    [Fact]
    public void 轮换设置能原样往返JSON()
    {
        // 它要随档案内容落库、再原样下发给教室端，字段漏一个就会静默变成「不轮换」。
        var content = Bundle(("周一", 3, 0));
        content.Rotation = new RotationSettingsDto
        {
            Weeks = 3,
            SemesterStartDate = "2026-09-06",
            Offset = -1,
        };

        var clone = ContentNormalizer.Clone(content);

        Assert.Equal(3, clone.Rotation.Weeks);
        Assert.Equal("2026-09-06", clone.Rotation.SemesterStartDate);
        Assert.Equal(-1, clone.Rotation.Offset);
    }
}
