using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;

namespace IslandHub.Tests;

/// <summary>
/// 「现在算第几节」（`#25`）。
/// <para>这段逻辑的边界最容易出错：上课前、课间、最后一节之后。用「下一节开始时间」判定时，
/// 课间应当归属**刚上完的那一节**（走班教室在课间保持本节对应的班级档案）。</para>
/// </summary>
public sealed class ProfileSlotResolverTests
{
    private static TimeLayoutDto Layout() => new()
    {
        Id = "tl",
        Name = "夏季作息",
        Items =
        [
            new TimeLayoutItemDto { StartTime = "08:00:00", EndTime = "08:45:00", Kind = TimeItemKind.Class },
            new TimeLayoutItemDto { StartTime = "08:45:00", EndTime = "08:55:00", Kind = TimeItemKind.Break },
            new TimeLayoutItemDto { StartTime = "08:55:00", EndTime = "09:40:00", Kind = TimeItemKind.Class },
            new TimeLayoutItemDto { StartTime = "09:40:00", EndTime = "10:10:00", Kind = TimeItemKind.Break },
            new TimeLayoutItemDto { StartTime = "10:10:00", EndTime = "10:55:00", Kind = TimeItemKind.Class },
        ],
    };

    [Fact]
    public void 只把上课类型算作节次()
    {
        // 课间（Break）不该占一个节次序号，否则课表节点会整体错位。
        Assert.Equal(3, ProfileSlotResolver.ClassPeriods(Layout()).Count);
    }

    [Theory]
    [InlineData("07:59", null)]      // 还没上课
    [InlineData("08:00", 0)]         // 第一节开始
    [InlineData("08:44", 0)]         // 第一节中
    [InlineData("08:50", 0)]         // 课间 → 仍算第一节
    [InlineData("08:55", 1)]         // 第二节开始
    [InlineData("09:30", 1)]
    [InlineData("10:10", 2)]         // 第三节
    [InlineData("23:00", null)]      // 放学后（超过最后一节的结束时间）：不判定、保持现状
                                     // （实际效果就是教室里还是最后一节那套档案，次日第一节再切）
    public void 按下一节开始时间判定当前节次(string time, int? expected)
    {
        var now = TimeOnly.Parse(time);

        Assert.Equal(expected, ProfileSlotResolver.ResolvePeriodIndex(Layout(), now));
    }

    [Fact]
    public void 时间表为空或时间格式不对时不判定()
    {
        Assert.Null(ProfileSlotResolver.ResolvePeriodIndex(null, new TimeOnly(9, 0)));
        Assert.Null(ProfileSlotResolver.ResolvePeriodIndex(new TimeLayoutDto(), new TimeOnly(9, 0)));

        var broken = new TimeLayoutDto
        {
            Items = [new TimeLayoutItemDto { StartTime = "八点", Kind = TimeItemKind.Class }],
        };
        Assert.Null(ProfileSlotResolver.ResolvePeriodIndex(broken, new TimeOnly(9, 0)));
    }

    [Fact]
    public void 最后一节没有结束时间时整天都算这一节()
    {
        var layout = new TimeLayoutDto
        {
            Items = [new TimeLayoutItemDto { StartTime = "15:00", EndTime = "00:00:00", Kind = TimeItemKind.Class }],
        };

        Assert.Equal(0, ProfileSlotResolver.ResolvePeriodIndex(layout, new TimeOnly(23, 30)));
        Assert.Null(ProfileSlotResolver.ResolvePeriodIndex(layout, new TimeOnly(14, 30)));
    }
}
