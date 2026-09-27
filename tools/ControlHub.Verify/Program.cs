using ClassIsland.Shared.IPC.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using ControlHub.Plugin.Services;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlHub.Verify;

/// <summary>
/// B 端映射验证工具。
/// <para>
/// 直接驱动插件中真实使用的 <see cref="ClassIslandAdapter"/>，把一份集控下发的
/// <see cref="ContentBundleDto"/> 应用到一个假的档案服务上，然后逐项断言映射结果，
/// 覆盖「科目 / 时间表 / 课表节点对齐 / 触发规则（周几 + 单双周轮换）」。
/// </para>
/// <para>
/// 之所以需要它：单双周轮换映射（<c>WeekInterval/WeekOffset → WeekCountDiv/WeekCountDivTotal</c>）
/// 是按 ClassIsland 源码语义推导的，缺少自动化覆盖。本工具把这段语义固化为可重复执行的回归验证。
/// </para>
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Console.WriteLine("=== ControlHub B 端映射验证 ===");
        Console.WriteLine();

        var service = new FakeProfileService();
        var adapter = new ClassIslandAdapter(service, NullLogger<ClassIslandAdapter>.Instance);

        var layoutId = Guid.NewGuid();
        var chineseId = Guid.NewGuid();
        var mathId = Guid.NewGuid();
        var weeklyPlanId = Guid.NewGuid();
        var oddWeekPlanId = Guid.NewGuid();
        var evenWeekPlanId = Guid.NewGuid();
        var threeWeekPlanId = Guid.NewGuid();

        var content = new ContentBundleDto
        {
            Subjects =
            [
                new SubjectDto { Id = chineseId.ToString(), Name = "语文", Initial = "语", TeacherName = "张老师" },
                new SubjectDto { Id = mathId.ToString(), Name = "数学", Initial = "数", TeacherName = "李老师", IsOutDoor = true },
            ],
            TimeLayouts =
            [
                new TimeLayoutDto
                {
                    Id = layoutId.ToString(),
                    Name = "夏季作息",
                    Items =
                    [
                        new TimeLayoutItemDto { StartTime = "08:00:00", EndTime = "08:45:00", Kind = TimeItemKind.Class },
                        new TimeLayoutItemDto { StartTime = "08:45:00", EndTime = "08:55:00", Kind = TimeItemKind.Break, BreakName = "课间" },
                        new TimeLayoutItemDto { StartTime = "08:55:00", EndTime = "09:40:00", Kind = TimeItemKind.Class },
                        new TimeLayoutItemDto { StartTime = "09:40:00", EndTime = "09:40:00", Kind = TimeItemKind.Separator },
                        new TimeLayoutItemDto { StartTime = "09:50:00", EndTime = "10:35:00", Kind = TimeItemKind.Class },
                    ],
                },
            ],
            ClassPlans =
            [
                // 每周都生效（不轮换）。
                new ClassPlanDto
                {
                    Id = weeklyPlanId.ToString(),
                    Name = "周一（每周）",
                    TimeLayoutId = layoutId.ToString(),
                    DaysOfWeek = [1],
                    WeekInterval = 0,
                    Slots = [new ClassPlanSlotDto { Index = 0, SubjectId = chineseId.ToString() }],
                },
                // 单周：WeekInterval=2 + WeekOffset=0。
                new ClassPlanDto
                {
                    Id = oddWeekPlanId.ToString(),
                    Name = "周一（单周）",
                    TimeLayoutId = layoutId.ToString(),
                    DaysOfWeek = [1],
                    WeekInterval = 2,
                    WeekOffset = 0,
                    Slots =
                    [
                        new ClassPlanSlotDto { Index = 0, SubjectId = chineseId.ToString() },
                        new ClassPlanSlotDto { Index = 1, SubjectId = mathId.ToString(), IsEnabled = false },
                        new ClassPlanSlotDto { Index = 2, SubjectId = chineseId.ToString() },
                    ],
                },
                // 双周：WeekInterval=2 + WeekOffset=1。
                new ClassPlanDto
                {
                    Id = evenWeekPlanId.ToString(),
                    Name = "周一（双周）",
                    TimeLayoutId = layoutId.ToString(),
                    DaysOfWeek = [1],
                    WeekInterval = 2,
                    WeekOffset = 1,
                    Slots = [new ClassPlanSlotDto { Index = 0, SubjectId = mathId.ToString() }],
                },
                // 三周轮换的第 3 周。
                new ClassPlanDto
                {
                    Id = threeWeekPlanId.ToString(),
                    Name = "周一（三周-第3周）",
                    TimeLayoutId = layoutId.ToString(),
                    DaysOfWeek = [1],
                    WeekInterval = 3,
                    WeekOffset = 2,
                    Slots = [new ClassPlanSlotDto { Index = 0, SubjectId = mathId.ToString() }],
                },
            ],
            Settings = new ProfileSettingsDto { Announcement = "验证用配置" },
        };

        var result = adapter.Apply(content, new ApplyOptions());
        Check("应用配置成功", result.Success, result.Message);
        Check("已调用 SaveProfile", service.Saved);

        var profile = service.Profile;
        if (!profile.ClassPlans.ContainsKey(oddWeekPlanId))
        {
            Console.WriteLine("课表未写入档案，终止后续断言。");
            return Report();
        }

        // ── 科目映射 ──────────────────────────────────────────
        Check("科目数量为 2", profile.Subjects.Count == 2, $"实际 {profile.Subjects.Count}");
        Check("科目「语文」名称映射", profile.Subjects[chineseId].Name == "语文");
        Check("科目「语文」简称映射", profile.Subjects[chineseId].Initial == "语");
        Check("科目「数学」户外标记映射", profile.Subjects[mathId].IsOutDoor);

        // ── 时间表映射 ────────────────────────────────────────
        var layout = profile.TimeLayouts[layoutId];
        Check("时间表时间点数量为 5", layout.Layouts.Count == 5, $"实际 {layout.Layouts.Count}");
        Check("TimeType 映射 class→0", layout.Layouts[0].TimeType == 0);
        Check("TimeType 映射 break→1", layout.Layouts[1].TimeType == 1);
        Check("TimeType 映射 separator→2", layout.Layouts[3].TimeType == 2);
        Check("开始时间解析 08:00", layout.Layouts[0].StartTime == new TimeSpan(8, 0, 0),
            layout.Layouts[0].StartTime.ToString());
        Check("课间名称映射", layout.Layouts[1].BreakName == "课间");

        // ── 课表节点对齐 ──────────────────────────────────────
        var oddPlan = profile.ClassPlans[oddWeekPlanId];
        Check("课表节点数与上课时间点数一致（3）", oddPlan.Classes.Count == 3, $"实际 {oddPlan.Classes.Count}");
        Check("节点0 科目=语文", oddPlan.Classes[0].SubjectId == chineseId);
        Check("节点1 科目=数学", oddPlan.Classes[1].SubjectId == mathId);
        Check("节点1 遵循禁用标记", oddPlan.Classes[1].IsEnabled == false);
        Check("节点2 科目=语文", oddPlan.Classes[2].SubjectId == chineseId);
        Check("节点序号连续", oddPlan.Classes.Select(c => c.Index).SequenceEqual([0, 1, 2]));
        Check("默认课表群", oddPlan.AssociatedGroup == ClassPlanGroup.DefaultGroupGuid);
        Check("课表关联到时间表", oddPlan.TimeLayoutId == layoutId);

        // ── 触发规则：周几 ────────────────────────────────────
        Check("触发类型为每周（Weekly）", oddPlan.TimeRule.Type == TimeRule.TimeRuleType.Weekly);
        Check("星期几映射 DaysOfWeek[0]→WeekDay", oddPlan.TimeRule.WeekDay == 1);

        // ── 触发规则：轮换（本次待办的核心）───────────────────
        var weeklyPlan = profile.ClassPlans[weeklyPlanId];
        Check("每周课表 WeekCountDiv=0（不轮换）", weeklyPlan.TimeRule.WeekCountDiv == 0,
            $"实际 {weeklyPlan.TimeRule.WeekCountDiv}");

        var evenPlan = profile.ClassPlans[evenWeekPlanId];
        Check("单周 WeekCountDiv=1", oddPlan.TimeRule.WeekCountDiv == 1, $"实际 {oddPlan.TimeRule.WeekCountDiv}");
        Check("单周 WeekCountDivTotal=2", oddPlan.TimeRule.WeekCountDivTotal == 2, $"实际 {oddPlan.TimeRule.WeekCountDivTotal}");
        Check("双周 WeekCountDiv=2", evenPlan.TimeRule.WeekCountDiv == 2, $"实际 {evenPlan.TimeRule.WeekCountDiv}");
        Check("双周 WeekCountDivTotal=2", evenPlan.TimeRule.WeekCountDivTotal == 2, $"实际 {evenPlan.TimeRule.WeekCountDivTotal}");

        var threeWeekPlan = profile.ClassPlans[threeWeekPlanId];
        Check("三周轮换第3周 WeekCountDiv=3", threeWeekPlan.TimeRule.WeekCountDiv == 3,
            $"实际 {threeWeekPlan.TimeRule.WeekCountDiv}");
        Check("三周轮换 WeekCountDivTotal=3", threeWeekPlan.TimeRule.WeekCountDivTotal == 3,
            $"实际 {threeWeekPlan.TimeRule.WeekCountDivTotal}");

        // ── 远程指令载荷契约 ──
        // A 端用 camelCase 序列化 payload，B 端必须用 HubJson（大小写不敏感）解析。
        // 一旦有人改回默认的 JsonSerializer，远程命令行 / 提醒 / 外观 / 插件启停就会整体失效，这里会立刻失败。
        Console.WriteLine();
        Console.WriteLine("远程指令载荷（camelCase）");

        var shellJson = HubJson.Serialize(new ShellPayloadSample { Command = "ipconfig /all", Args = "/v" });
        var shellBack = HubJson.Deserialize<ShellPayloadSample>(shellJson);
        Check("shell 载荷能按 camelCase 还原",
            shellBack?.Command == "ipconfig /all" && shellBack.Args == "/v",
            shellBack?.Command ?? "null");

        var notifyBack = HubJson.Deserialize<NotifyRequestDto>(
            """{"title":"紧急通知","message":"内容","speak":true,"durationSeconds":30}""");
        Check("notify 字段绑定成功（含语音开关）",
            notifyBack?.Title == "紧急通知" && notifyBack.Message == "内容"
            && notifyBack.Speak && notifyBack.DurationSeconds == 30,
            $"title={notifyBack?.Title} speak={notifyBack?.Speak}");

        var toggleBack = HubJson.Deserialize<PluginToggleSample>(
            """{"pluginId":"ClassIsland.Demo","enabled":false}""");
        Check("plugin.toggle 字段绑定成功（插件 ID 不为空）",
            toggleBack?.PluginId == "ClassIsland.Demo" && !toggleBack.Enabled,
            $"pluginId={toggleBack?.PluginId ?? "null"}");

        var appearanceBack = HubJson.Deserialize<AppearanceConfigDto>(
            """{"theme":"dark","accentColor":"#7C5CFF"}""");
        Check("appearance 字段绑定成功",
            appearanceBack?.Theme == "dark" && appearanceBack.AccentColor == "#7C5CFF",
            $"theme={appearanceBack?.Theme} accent={appearanceBack?.AccentColor}");

        // 反证：默认 JsonSerializer 区分大小写，正是它让 payload 内层字段全部变成 null。
        var caseSensitive = System.Text.Json.JsonSerializer.Deserialize<ShellPayloadSample>(
            """{"command":"echo hi"}""");
        Check("默认 JsonSerializer 区分大小写（因此必须用 HubJson）", caseSensitive?.Command is null);

        return Report();
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  [通过] {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [失败] {name}{(string.IsNullOrEmpty(detail) ? "" : $"（{detail}）")}");
        }
    }

    private static int Report()
    {
        Console.WriteLine();
        Console.WriteLine($"=== 结果：{_passed} 项通过，{_failed} 项失败 ===");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>shell 指令载荷（与插件内的 ShellPayload 同构，用于验证 camelCase 绑定）。</summary>
    private sealed class ShellPayloadSample
    {
        public string? Command { get; set; }
        public string? Args { get; set; }
    }

    /// <summary>插件启停载荷（与插件内的 PluginTogglePayload 同构）。</summary>
    private sealed class PluginToggleSample
    {
        public string? PluginId { get; set; }
        public bool Enabled { get; set; }
    }

    /// <summary>验证用的假档案服务，仅保留适配器真正会用到的成员。</summary>
    private sealed class FakeProfileService : IPublicProfileService
    {
        public string CurrentProfilePath { get; set; } = string.Empty;

        public Profile Profile { get; set; } = new();

        public bool Saved { get; private set; }

        public bool IsCurrentProfileTrusted => true;

        public void SaveProfile() => Saved = true;

        public void SaveProfile(string filename) => Saved = true;

        public Guid? CreateTempClassPlan(Guid id, Guid? timeLayoutId = null, DateTime? enableDateTime = null) => null;

        public Guid? CreateTempClassPlan(Guid id, Guid? timeLayoutId, DateTime? enableDateTime, bool createTempTimeLayout) => null;

        public void ClearTempClassPlan()
        {
        }

        public void ConvertToStdClassPlan()
        {
        }

        public void ConvertToStdClassPlan(Guid id)
        {
        }

        public void SetupTempClassPlanGroup(Guid key, DateTime? expireTime = null)
        {
        }

        public void ClearTempClassPlanGroup()
        {
        }
    }
}
