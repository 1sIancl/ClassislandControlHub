using IslandHub.Plugin.Services;
using IslandHub.Protocol.Dtos;

namespace IslandHub.Verify;

/// <summary>
/// 多周轮换设置下发（<see cref="HostRotationSettings"/>）的验证。
///
/// <para>
/// 这一段只在教室端成立：A 端下发的是「轮换起点 / 最大周期 / 手动偏移」三个**宿主设置**，
/// 没有 HTTP 接口能验到它们有没有真的写进 ClassIsland。所以用一个「假设置对象」跑真实的反射分支——
/// 属性名与类型照宿主 <c>Settings</c> 抄，宿主换名字这里会立刻失败。
/// </para>
///
/// <para>
/// 最要紧的一条是**最大轮换周期**：宿主会把「周期比 MaxCycle 还长」的课表直接判为不生效
/// （<c>LessonsService.IsClassPlanEnabledAt</c>），而默认只有 4。不把它抬上去，
/// 5 周以上的轮换在教室里就是**静默失效**（配置文件下发成功、课表也在，就是永远不显示）。
/// </para>
/// </summary>
internal static class RotationSettingsChecks
{
    /// <summary>宿主 <c>Settings</c> 的替身（属性名 / 类型 / 默认值与宿主一致）。</summary>
    private sealed class FakeSettings
    {
        /// <summary>轮换起点。宿主默认是「本周的周日」。</summary>
        public DateTime SingleWeekStartTime { get; set; } = new(2026, 8, 30);

        /// <summary>多周轮换偏移：**下标就是周期长度**（2-first），<c>-1</c> 表示该周期没设置。</summary>
        public List<int> MultiWeekRotationOffset { get; set; } = [-1, -1, 0, 0, 0];

        /// <summary>最大轮换周期，宿主默认 4。</summary>
        public int MultiWeekRotationMaxCycle { get; set; } = 4;
    }

    /// <summary>老版本宿主的替身：只有起点与偏移，没有「最大轮换周期」。</summary>
    private sealed class OldHostSettings
    {
        public DateTime SingleWeekStartTime { get; set; } = new(2026, 8, 30);
        public List<int> MultiWeekRotationOffset { get; set; } = [-1, -1];
    }

    private static ClassPlanDto Plan(int week, int weeks) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = $"第 {week} 周课表",
        TimeLayoutId = Guid.NewGuid().ToString(),
        DaysOfWeek = [1],
        WeekInterval = weeks,
        WeekOffset = week - 1,
    };

    public static void Run(CheckSink check)
    {
        Console.WriteLine();
        Console.WriteLine("多周轮换设置下发（HostRotationSettings）");

        // ① 不轮换：宿主的本地设置一律不动（否则会把教室自己调好的起点改掉，还不触发落盘）。
        var idleHost = new FakeSettings();
        var idle = HostRotationSettings.ApplyTo(idleHost, new RotationSettingsDto { Weeks = 1 }, [Plan(1, 0)]);
        check("不轮换时不动宿主设置、也不触发落盘",
            !idle.Changed && idleHost.SingleWeekStartTime == new DateTime(2026, 8, 30)
            && idleHost.MultiWeekRotationMaxCycle == 4,
            $"changed={idle.Changed} max={idleHost.MultiWeekRotationMaxCycle}");

        // ② 2 周轮换：写起点；2 周这一档偏移本来就是 0，不该产生多余改动。
        var twoWeekHost = new FakeSettings();
        var twoWeek = HostRotationSettings.ApplyTo(
            twoWeekHost,
            new RotationSettingsDto { Weeks = 2, SemesterStartDate = "2026-09-06" },
            [Plan(1, 2), Plan(2, 2)]);
        check("2 周轮换把轮换起点写进宿主（写一次原则：值相同才跳过）",
            twoWeekHost.SingleWeekStartTime == new DateTime(2026, 9, 6) && twoWeek.Changed,
            $"start={twoWeekHost.SingleWeekStartTime:yyyy-MM-dd} note={twoWeek.Note}");
        check("2 周轮换不动最大周期（宿主默认 4 周够用）",
            twoWeekHost.MultiWeekRotationMaxCycle == 4);

        // ③ 9 周轮换：默认最大周期 4 → 必须抬到 9，偏移集合也要按周期长度补位。
        var nineWeekHost = new FakeSettings();
        var nineWeek = HostRotationSettings.ApplyTo(
            nineWeekHost,
            new RotationSettingsDto { Weeks = 9, SemesterStartDate = "2026-09-06", Offset = 1 },
            [Plan(1, 9)]);
        check("9 周轮换把「最大轮换周期」从 4 抬到 9（否则长周期课表在教室端不生效）",
            nineWeekHost.MultiWeekRotationMaxCycle == 9 && nineWeek.Note.Contains("最大轮换周期"),
            $"max={nineWeekHost.MultiWeekRotationMaxCycle} note={nineWeek.Note}");
        check("手动偏移写进「对应周期」那一档（下标 = 周期长度）",
            nineWeekHost.MultiWeekRotationOffset.Count > 9
            && nineWeekHost.MultiWeekRotationOffset[9] == 1,
            $"count={nineWeekHost.MultiWeekRotationOffset.Count} [9]={nineWeekHost.MultiWeekRotationOffset.ElementAtOrDefault(9)}");
        check("偏移集合只补到需要的长度，不无脑清空其它档位",
            nineWeekHost.MultiWeekRotationOffset[2] == 0
            && nineWeekHost.MultiWeekRotationOffset[3] == 0,
            $"[2]={nineWeekHost.MultiWeekRotationOffset[2]} [3]={nineWeekHost.MultiWeekRotationOffset[3]}");

        // ④ 写一次：同样的设置再下发一次应当「零改动」（否则会反复与宿主的保存撞车）。
        var again = HostRotationSettings.ApplyTo(
            nineWeekHost,
            new RotationSettingsDto { Weeks = 9, SemesterStartDate = "2026-09-06", Offset = 1 },
            [Plan(1, 9)]);
        check("同样的设置重复下发为零改动（写一次原则，避免与宿主抢写 Settings.json）",
            !again.Changed && again.Note.Length == 0,
            $"changed={again.Changed} note=「{again.Note}」");

        // ⑤ 档案级没启用轮换，但课表自带 5 周轮换（导入进来的档案就这样）→ 周期仍要按课表算。
        var importedHost = new FakeSettings();
        var imported = HostRotationSettings.ApplyTo(importedHost, new RotationSettingsDto { Weeks = 1 }, [Plan(1, 5)]);
        check("档案级没设周期时按课表自带的周期算（5 周 → 上限抬到 5）",
            importedHost.MultiWeekRotationMaxCycle == 5 && imported.Changed,
            $"max={importedHost.MultiWeekRotationMaxCycle} note={imported.Note}");

        // ⑥ 老宿主没有「最大轮换周期」：如实说明「不会被启用」，而不是假装下发成功。
        var oldHost = new OldHostSettings();
        var old = HostRotationSettings.ApplyTo(
            oldHost,
            new RotationSettingsDto { Weeks = 9, SemesterStartDate = "2026-09-06" },
            [Plan(1, 9)]);
        check("老宿主缺「最大轮换周期」时如实回报（不假装成功、也不改其它设置）",
            !old.Changed && old.Note.Contains("不会被启用")
            && oldHost.SingleWeekStartTime == new DateTime(2026, 8, 30),
            $"changed={old.Changed} note=「{old.Note}」");

        // ⑦ 起点认不出来：忽略它，而不是按错的日子算周次。
        var badStartHost = new FakeSettings();
        var badStart = HostRotationSettings.ApplyTo(
            badStartHost,
            new RotationSettingsDto { Weeks = 2, SemesterStartDate = "开学第一周" },
            [Plan(1, 2)]);
        check("认不出来的轮换起点被忽略（不写错误日期）",
            !badStart.Changed && badStartHost.SingleWeekStartTime == new DateTime(2026, 8, 30),
            $"changed={badStart.Changed} start={badStartHost.SingleWeekStartTime:yyyy-MM-dd}");

        // ⑧ 扩展偏移集合时补位的值必须是 0：宿主对超出列表长度的档位取 GetValueOrDefault = 0（不偏移），
        //    补 -1（宿主的「未设置」哨兵）会让那些周期长度整体偏移 -1 周，属于悄悄改坏别的档位。
        check("扩展偏移集合时补位填 0 而不是宿主的「未设置」哨兵 -1",
            nineWeekHost.MultiWeekRotationOffset.Count > 9
            && nineWeekHost.MultiWeekRotationOffset.Skip(5).Take(4).All(v => v == 0)
            && nineWeekHost.MultiWeekRotationOffset[9] == 1,
            string.Join(",", nineWeekHost.MultiWeekRotationOffset));

        // ⑨ 上报给管理端的「当前第几周」必须与教室端算法一致
        //    （复刻 LessonsService.GetCyclePositionsByDate：floor 计周 + 负数取模补正 + 1 起）。
        var weekStart = new DateOnly(2026, 9, 6);
        check("轮换周次：起点当天是第 1 周，第 7 天仍是第 1 周、第 8 天进第 2 周",
            HostRotationSettings.ResolveWeek(weekStart, weekStart, 0, 2) == 1
            && HostRotationSettings.ResolveWeek(weekStart.AddDays(6), weekStart, 0, 2) == 1
            && HostRotationSettings.ResolveWeek(weekStart.AddDays(7), weekStart, 0, 2) == 2);
        check("轮换周次：按周期循环（3 周一轮：第 3 周之后回到第 1 周）",
            HostRotationSettings.ResolveWeek(weekStart.AddDays(14), weekStart, 0, 3) == 3
            && HostRotationSettings.ResolveWeek(weekStart.AddDays(21), weekStart, 0, 3) == 1);
        check("轮换周次：手动偏移整体平移（-1 → 起点那天算第 2 周）",
            HostRotationSettings.ResolveWeek(weekStart, weekStart, -1, 2) == 2);
        check("轮换周次：起点之前也要与宿主一致（floor 而不是向零截断）",
            HostRotationSettings.ResolveWeek(weekStart.AddDays(-1), weekStart, 0, 3) == 3,
            $"得到 {HostRotationSettings.ResolveWeek(weekStart.AddDays(-1), weekStart, 0, 3)}");
        check("不轮换时不报周次（周期 <2 → 0）",
            HostRotationSettings.ResolveWeek(weekStart, weekStart, 0, 1) == 0);
    }
}
