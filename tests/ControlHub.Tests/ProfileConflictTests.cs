using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Services;
using Microsoft.Data.Sqlite;

namespace ControlHub.Tests;

/// <summary>
/// 课表冲突检测（#44）的判定逻辑。
/// <para>为什么用单元测试而不是接口实测：**通过接口造不出这些坏数据**——
/// `ContentNormalizer` 会在保存时把悬空引用自动修好，而教师冲突要求科目里填了教师名
/// （现场演示数据一个都没填）。这里直接构造内容落库、绕过规范化，验证的才是「检测本身对不对」。
/// 每一类都配了负例（不该报的别报），否则「什么都报」也能让正例通过。</para>
/// </summary>
public sealed class ProfileConflictTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ch-conflict-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(dir, "hub.db");

    private HubStore NewStore() => new(DbPath, new SecretProtector(Path.Combine(dir, "secrets.key")));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    // ───────────────────────── 构造内容的小工具 ─────────────────────────

    private static SubjectDto Subject(string id, string name, string teacher = "") =>
        new() { Id = id, Name = name, TeacherName = teacher };

    private static TimeLayoutDto Layout(string id, string name, params (string Start, string End)[] items) =>
        new()
        {
            Id = id,
            Name = name,
            Items = items
                .Select(i => new TimeLayoutItemDto
                {
                    StartTime = i.Start,
                    EndTime = i.End,
                    Kind = TimeItemKind.Class,
                })
                .ToList(),
        };

    private static ClassPlanDto Plan(string id, string name, string layoutId, int[] days,
        params (int Index, string? Subject)[] slots) =>
        new()
        {
            Id = id,
            Name = name,
            TimeLayoutId = layoutId,
            IsEnabled = true,
            DaysOfWeek = days.ToList(),
            Slots = slots
                .Select(s => new ClassPlanSlotDto { Index = s.Index, SubjectId = s.Subject, IsEnabled = true })
                .ToList(),
        };

    private static async Task AddProfileAsync(HubStore store, string name, ContentBundleDto content)
    {
        await store.CreateProfileAsync(new ProfileRow
        {
            Id = HubChecksum.NewId(),
            Name = name,
            Code = name.Length >= 4 ? name[..4] : "TST",
            Revision = 1,
            Content = HubJson.Serialize(content),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>一个班：周一第 1 节 08:00-08:45，科目由 <paramref name="teacher"/> 任课。</summary>
    private static async Task AddClassAsync(HubStore store, string profileName, string teacher, string time = "08:00:00",
        string end = "08:45:00", string subjectId = "s1", string layoutId = "t1", string planId = "p1")
    {
        await AddProfileAsync(store, profileName, new ContentBundleDto
        {
            Subjects = [Subject(subjectId, "数学", teacher)],
            TimeLayouts = [Layout(layoutId, "夏季作息", (time, end))],
            ClassPlans = [Plan(planId, "周一", layoutId, [1], (0, subjectId))],
        });
    }

    // ───────────────────────── 教师冲突 ─────────────────────────

    [Fact]
    public async Task 同一教师同一时间出现在两个档案会被报出来()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await AddClassAsync(store, "高一(1)班", "张老师");
        await AddClassAsync(store, "高一(2)班", "张老师");

        var report = await store.DetectProfileConflictsAsync();

        var hit = Assert.Single(report.Conflicts, c => c.Kind == "teacher-overlap");
        // 合班上课、同名老师都属正常，所以只提示不阻塞。
        Assert.False(hit.Blocking);
        Assert.Contains("张老师", hit.Message, StringComparison.Ordinal);
        Assert.Equal(2, report.ProfileCount);
        Assert.Equal(2, report.ClassPlanCount);
    }

    [Fact]
    public async Task 不同教师在相同时间不算冲突()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await AddClassAsync(store, "高一(1)班", "张老师");
        await AddClassAsync(store, "高一(2)班", "李老师");

        var report = await store.DetectProfileConflictsAsync();
        Assert.Empty(report.Conflicts);
    }

    [Fact]
    public async Task 同一教师时间错开不算冲突()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await AddClassAsync(store, "高一(1)班", "张老师", time: "08:00:00", end: "08:45:00");
        // 第二节 08:55 开始，与上一节不重叠
        await AddClassAsync(store, "高一(2)班", "张老师", time: "08:55:00", end: "09:40:00");

        var report = await store.DetectProfileConflictsAsync();
        Assert.Empty(report.Conflicts);
    }

    [Fact]
    public async Task 同一档案内的时间重叠不算跨班冲突()
    {
        var store = NewStore();
        await store.InitializeAsync();

        // 同一个档案里两个课表都排了张老师同一时间——不是「被排到两个班」，不报。
        await AddProfileAsync(store, "高一(1)班", new ContentBundleDto
        {
            Subjects = [Subject("s1", "数学", "张老师"), Subject("s2", "语文", "张老师")],
            TimeLayouts = [Layout("t1", "夏季作息", ("08:00:00", "08:45:00"))],
            ClassPlans =
            [
                Plan("p1", "周一", "t1", [1], (0, "s1")),
                Plan("p2", "周一(备用)", "t1", [1], (0, "s2")),
            ],
        });

        var report = await store.DetectProfileConflictsAsync();
        Assert.DoesNotContain(report.Conflicts, c => c.Kind == "teacher-overlap");
    }

    [Fact]
    public async Task 生效星期为空视为每天都生效()
    {
        var store = NewStore();
        await store.InitializeAsync();

        // 甲班只在周一上课；乙班的课表没写 DaysOfWeek（= 每天都生效）→ 周一必然撞上。
        await AddClassAsync(store, "高一(1)班", "张老师");
        await AddProfileAsync(store, "高一(2)班", new ContentBundleDto
        {
            Subjects = [Subject("s2", "数学", "张老师")],
            TimeLayouts = [Layout("t2", "夏季作息", ("08:00:00", "08:45:00"))],
            ClassPlans = [Plan("p2", "全周", "t2", [], (0, "s2"))],
        });

        var report = await store.DetectProfileConflictsAsync();
        var hit = Assert.Single(report.Conflicts, c => c.Kind == "teacher-overlap");
        Assert.Contains("周一", hit.Message, StringComparison.Ordinal);
    }

    // ───────────────────────── 硬错误：引用与时间 ─────────────────────────

    [Fact]
    public async Task 课表引用不存在的科目会被报为必须处理()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await AddProfileAsync(store, "高一(1)班", new ContentBundleDto
        {
            Subjects = [Subject("s1", "数学", "张老师")],
            TimeLayouts = [Layout("t1", "夏季作息", ("08:00:00", "08:45:00"))],
            ClassPlans = [Plan("p1", "周一", "t1", [1], (0, "s1"), (1, "ghost"))],
        });

        var report = await store.DetectProfileConflictsAsync();
        var hit = Assert.Single(report.Conflicts, c => c.Kind == "missing-subject");
        Assert.True(hit.Blocking);
        Assert.Contains("第 2 节", hit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 课表引用不存在的时间表会被报为必须处理()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await AddProfileAsync(store, "高一(1)班", new ContentBundleDto
        {
            Subjects = [Subject("s1", "数学", "张老师")],
            TimeLayouts = [Layout("t1", "夏季作息", ("08:00:00", "08:45:00"))],
            ClassPlans = [Plan("p1", "周一", "t-not-exist", [1], (0, "s1"))],
        });

        var report = await store.DetectProfileConflictsAsync();
        var hit = Assert.Single(report.Conflicts, c => c.Kind == "missing-timelayout");
        Assert.True(hit.Blocking);
    }

    [Fact]
    public async Task 节次超出时间表节数会被报出来()
    {
        var store = NewStore();
        await store.InitializeAsync();

        // 时间表只有 1 节，课表却排到第 3 节（Index = 2）
        await AddProfileAsync(store, "高一(1)班", new ContentBundleDto
        {
            Subjects = [Subject("s1", "数学", "张老师")],
            TimeLayouts = [Layout("t1", "夏季作息", ("08:00:00", "08:45:00"))],
            ClassPlans = [Plan("p1", "周一", "t1", [1], (2, "s1"))],
        });

        var report = await store.DetectProfileConflictsAsync();
        var hit = Assert.Single(report.Conflicts, c => c.Kind == "slot-out-of-range");
        Assert.True(hit.Blocking);
        Assert.Contains("共 1 节", hit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 时间表内两节时间重叠会被报出来()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await AddProfileAsync(store, "高一(1)班", new ContentBundleDto
        {
            Subjects = [Subject("s1", "数学")],
            // 第 2 节 08:30 开始，与第 1 节（到 08:45）重叠
            TimeLayouts = [Layout("t1", "夏季作息", ("08:00:00", "08:45:00"), ("08:30:00", "09:15:00"))],
            ClassPlans = [Plan("p1", "周一", "t1", [1], (0, "s1"), (1, "s1"))],
        });

        var report = await store.DetectProfileConflictsAsync();
        var hit = Assert.Single(report.Conflicts, c => c.Kind == "timelayout-overlap");
        Assert.True(hit.Blocking);
        Assert.Contains("时间重叠", hit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 同一档案内两套默认课表同时生效会被报出来()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await AddProfileAsync(store, "高一(1)班", new ContentBundleDto
        {
            Subjects = [Subject("s1", "数学")],
            TimeLayouts = [Layout("t1", "夏季作息", ("08:00:00", "08:45:00"))],
            // 两套都「默认启用」（没有 DaysOfWeek、没有周次规则），客户端只能挑一套，结果不可预期。
            ClassPlans =
            [
                Plan("p1", "课表甲", "t1", [], (0, "s1")),
                Plan("p2", "课表乙", "t1", [], (0, "s1")),
            ],
        });

        var report = await store.DetectProfileConflictsAsync();
        var hit = Assert.Single(report.Conflicts, c => c.Kind == "duplicate-classplan");
        Assert.True(hit.Blocking);
        Assert.Contains("两套课表", hit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 靠星期区分课表是正常用法不该报()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await AddProfileAsync(store, "高一(1)班", new ContentBundleDto
        {
            Subjects = [Subject("s1", "数学")],
            TimeLayouts = [Layout("t1", "夏季作息", ("08:00:00", "08:45:00"))],
            ClassPlans =
            [
                Plan("p1", "周一课表", "t1", [1], (0, "s1")),
                Plan("p2", "周二课表", "t1", [2], (0, "s1")),
            ],
        });

        var report = await store.DetectProfileConflictsAsync();
        Assert.DoesNotContain(report.Conflicts, c => c.Kind == "duplicate-classplan");
    }

    [Fact]
    public async Task 干净档案不产生任何冲突()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await AddClassAsync(store, "高一(1)班", "张老师");
        await AddClassAsync(store, "高一(2)班", "李老师", time: "08:55:00", end: "09:40:00");

        var report = await store.DetectProfileConflictsAsync();
        Assert.Empty(report.Conflicts);
        Assert.False(report.Truncated);
    }
}
