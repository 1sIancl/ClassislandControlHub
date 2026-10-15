using IslandHub.Protocol.Dtos;

namespace IslandHub.Protocol;

/// <summary>
/// 「现在算第几节」（`#25` 走班制教室按时段切档案）。
///
/// <para><b>为什么用「下一节的开始时间」来判断一节的范围，而不是用 <c>EndTime</c></b>：
/// 时间表里 <c>EndTime</c> 允许留空（<c>00:00:00</c>），而且课间是单独的时间点类型、不进「上课」
/// 序列。用「本节开始 ≤ 现在 &lt; 下一节开始」判定，既不用管 <c>EndTime</c> 有没有填，
/// 也让课间自然归给刚上完的那一节——走班制教室在课间保持本节对应的班级档案是对的
/// （下一节打铃才换）。</para>
///
/// <para>纯函数：只读时间表与当前时刻，不做 IO，因此可以用单元测试把边界钉住。</para>
/// </summary>
public static class ProfileSlotResolver
{
    /// <summary>
    /// 取时间表里「上课」类型的时间点，按开始时间升序——**顺序即节次序号**，
    /// 与课表节点（<c>ClassPlanSlotDto.Index</c>）一一对应。
    /// </summary>
    public static List<TimeLayoutItemDto> ClassPeriods(TimeLayoutDto? layout)
    {
        if (layout is null)
        {
            return [];
        }

        return
        [
            .. layout.Items
                .Where(i => string.Equals(i.Kind, TimeItemKind.Class, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => ParseTime(i.StartTime) ?? TimeOnly.MaxValue)
        ];
    }

    /// <summary>
    /// 现在是第几节（0 基，与课表节点序号一致）；不在任何上课时段内时返回 <c>null</c>。
    /// </summary>
    public static int? ResolvePeriodIndex(TimeLayoutDto? layout, TimeOnly now)
    {
        var periods = ClassPeriods(layout);
        if (periods.Count == 0)
        {
            return null;
        }

        var starts = periods.Select(p => ParseTime(p.StartTime)).ToList();
        var firstStart = starts[0];
        if (firstStart is null || now < firstStart.Value)
        {
            return null;    // 第一节课还没开始
        }

        // 最后一节的结束边界：填了 EndTime 就用它，否则按「下一次没有」处理（一直有效到当天结束）。
        // ⚠️ `00:00:00` 是时间表 DTO 的**默认值**（表示没填），不是真的零点——当成零点的话，
        // 全天最后一节会在放学那一刻就失效，半夜的规则判定会整体错乱。
        var lastEnd = ParseEndTime(periods[^1].EndTime);
        for (var i = 0; i < periods.Count; i++)
        {
            var start = starts[i];
            if (start is null || now < start.Value)
            {
                continue;
            }

            var nextStart = i + 1 < starts.Count ? starts[i + 1] : null;
            if (nextStart is { } next)
            {
                if (now < next)
                {
                    return i;
                }

                continue;
            }

            // 最后一节：有结束时间就按它判，没有就认为到当天结束都算这一节。
            return lastEnd is null || now <= lastEnd.Value ? i : null;
        }

        return null;
    }

    /// <summary>解析「结束时间」：<c>00:00:00</c> 按**未填**处理（时间表 DTO 的默认值就是它）。</summary>
    public static TimeOnly? ParseEndTime(string? value)
    {
        var parsed = ParseTime(value);
        return parsed == TimeOnly.MinValue ? null : parsed;
    }

    /// <summary>解析 <c>HH:mm:ss</c> / <c>HH:mm</c>；解析不出来返回 <c>null</c>。</summary>
    public static TimeOnly? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        return TimeOnly.TryParse(text, out var parsed) ? parsed : null;
    }
}
