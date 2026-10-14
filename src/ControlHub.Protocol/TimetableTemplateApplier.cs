using ControlHub.Protocol.Dtos;

namespace ControlHub.Protocol;

/// <summary>
/// 把课表模板套用到一份档案内容上（`#9` 的「模板套用」）。
///
/// <para><b>为什么按科目名称匹配</b>：模板里存的是科目**名称**（见 <c>TimetableTemplateDto</c>），
/// 因为科目 ID 是档案内的 GUID，跨档案必然对不上——按名称匹配，找不到的名字会被**跳过并计数**，
/// 而不是悄悄塞进一个无效 ID（那会在大屏上表现为「这节课没有科目」，且没人知道为什么）。</para>
///
/// <para><b>按天匹配的规则</b>：模板是「天 × 节次」的网格，课表则是「一张课表覆盖若干天」。
/// 所以对每张课表看它覆盖的那几天在模板里是否**一致**：一致就套用；不一致（例如一张课表同时覆盖
/// 周一到周五，而模板里周一与周二不同）就**跳过并计入冲突**，提示管理员把课表按天拆开——
/// 这里宁可少做，也不能替管理员猜「以哪天为准」而把课表改错。</para>
///
/// <para>纯函数：只读传入的内容、写回同一对象，不做 IO（因此可以用单元测试把上面两条规则钉住）。</para>
/// </summary>
public static class TimetableTemplateApplier
{
    /// <summary>套用结果。</summary>
    public sealed class ApplyOutcome
    {
        /// <summary>被套用的课表名（已改写的）。</summary>
        public List<string> UpdatedPlans { get; } = [];

        /// <summary>被套用的天（星期几）。</summary>
        public List<int> UpdatedDays { get; } = [];

        /// <summary>因为「一张课表覆盖多天而模板里这几天不一致」被跳过的课表名。</summary>
        public List<string> ConflictedPlans { get; } = [];

        /// <summary>模板里有、但目标档案里找不到同名的科目名称（去重）。</summary>
        public List<string> MissingSubjects { get; } = [];

        /// <summary>模板里给了课、但没有课表覆盖那几天的星期（去重）。</summary>
        public List<int> UncoveredDays { get; } = [];

        /// <summary>节数不一致的提示（模板节数 vs 课表节点数）。</summary>
        public List<string> PeriodMismatches { get; } = [];

        /// <summary>是否真的改动了内容。</summary>
        public bool Changed => UpdatedPlans.Count > 0;
    }

    /// <summary>
    /// 把 <paramref name="grid"/> 套用到 <paramref name="content"/>（就地改写）。
    /// </summary>
    /// <param name="content">目标档案内容。</param>
    /// <param name="grid">模板网格：键为星期（<c>"0"</c>=周日 … <c>"6"</c>=周六）。</param>
    /// <param name="periodCount">模板声明的节数（用于校验；0 表示不校验）。</param>
    public static ApplyOutcome Apply(ContentBundleDto content, Dictionary<string, List<string?>> grid,
        int periodCount)
    {
        var outcome = new ApplyOutcome();

        // 科目名称 → ID。同名取第一个（档案里正常不会有重名，真重了也以先出现的为准）。
        var subjectIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subject in content.Subjects)
        {
            var name = (subject.Name ?? string.Empty).Trim();
            if (name.Length > 0)
            {
                subjectIds.TryAdd(name, subject.Id);
            }
        }

        // 只处理「非临时层」的课表：临时层是换课用的，套模板会把它覆盖掉。
        var plans = content.ClassPlans.Where(p => !p.IsOverlay).ToList();
        if (plans.Count == 0)
        {
            return outcome;
        }

        // 模板里「有课的天」→ 归一化后的列（去掉首尾空白；null 与空串都表示没课）。
        var dayColumns = new Dictionary<int, List<string?>>();
        foreach (var (key, column) in grid)
        {
            if (!int.TryParse(key, out var day) || day is < 0 or > 6)
            {
                continue;
            }

            var normalized = column.Select(s => string.IsNullOrWhiteSpace(s) ? null : s.Trim()).ToList();
            if (normalized.All(s => s is null))
            {
                continue;   // 整天没课：不参与匹配（否则会把课表清空）
            }

            dayColumns[day] = normalized;
        }

        if (dayColumns.Count == 0)
        {
            return outcome;
        }

        // 记下「有课的天」是否被任何一张课表覆盖，用于提示。
        var coveredDays = new HashSet<int>();

        foreach (var plan in plans)
        {
            // 课表覆盖的天：DaysOfWeek 为空表示不限（每天都生效）→ 用模板里有课的天去衡量。
            var planDays = plan.DaysOfWeek.Count > 0
                ? plan.DaysOfWeek.Where(dayColumns.ContainsKey).Distinct().OrderBy(d => d).ToList()
                : dayColumns.Keys.OrderBy(d => d).ToList();

            if (planDays.Count == 0)
            {
                continue;
            }

            foreach (var day in planDays)
            {
                coveredDays.Add(day);
            }

            // 这几天在模板里的列必须一致，否则这张课表无法忠实套用（宁可跳过也不猜）。
            var first = dayColumns[planDays[0]];
            var conflicted = planDays.Any(day => !SameColumn(first, dayColumns[day]));
            if (conflicted)
            {
                outcome.ConflictedPlans.Add(plan.Name);
                continue;
            }

            if (periodCount > 0 && periodCount != plan.Slots.Count)
            {
                outcome.PeriodMismatches.Add(
                    $"{plan.Name}：模板 {periodCount} 节，课表 {plan.Slots.Count} 节");
            }

            for (var i = 0; i < plan.Slots.Count; i++)
            {
                var slot = plan.Slots[i];
                if (i >= first.Count)
                {
                    // 模板没覆盖到这一节（模板节数比课表少）：**保持原样**，与「找不到同名科目」
                    // 同一原则——模板没说的部分，不替管理员清空（否则套一个 7 节课表的模板会把
                    // 第 8 节后的课全抹掉，而且事后看不出来）。
                    continue;
                }

                var name = first[i];
                if (name is null)
                {
                    slot.SubjectId = null;      // 模板明确留空 → 该节设为无课
                    continue;
                }

                if (subjectIds.TryGetValue(name, out var id))
                {
                    slot.SubjectId = id;
                }
                else
                {
                    // 找不到同名科目：**保留原科目**，只记一笔——清空它比留着更糟
                    //（管理员少建一个科目，不该让那节课直接变成空堂）。
                    if (!outcome.MissingSubjects.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        outcome.MissingSubjects.Add(name);
                    }
                }
            }

            outcome.UpdatedPlans.Add(plan.Name);
            foreach (var day in planDays.Where(d => !outcome.UpdatedDays.Contains(d)))
            {
                outcome.UpdatedDays.Add(day);
            }
        }

        foreach (var day in dayColumns.Keys.Where(d => !coveredDays.Contains(d)).OrderBy(d => d))
        {
            outcome.UncoveredDays.Add(day);
        }

        return outcome;
    }

    /// <summary>两列是否等价（长度不同按较长为基准，缺的位置视作没课）。</summary>
    private static bool SameColumn(List<string?> a, List<string?> b)
    {
        var length = Math.Max(a.Count, b.Count);
        for (var i = 0; i < length; i++)
        {
            var left = i < a.Count ? a[i] : null;
            var right = i < b.Count ? b[i] : null;
            if (!string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
