using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Services;

namespace ControlHub.Tests;

/// <summary>
/// 「下发前差异预览」的回归测试。
/// <para>这段逻辑最容易出的错是**静默地报「没有差异」**：档案被编辑后，如果拿档案行本身当基线，
/// 对比双方会指向同一份最新内容，于是永远看不出变化——界面上看起来一切正常，但功能是废的。
/// 下面这些用例就是为了钉住「必须用应用时的快照当基线」这个行为。</para>
/// </summary>
public sealed class ApplyDiffServiceTests
{
    private static ProfileRow Profile(string id, string name, long revision, ContentBundleDto content) => new()
    {
        Id = id,
        Name = name,
        Revision = revision,
        Content = HubJson.Serialize(content),
    };

    private static ProfileVersionRow Snapshot(ProfileRow profile) => new()
    {
        Id = "ver-" + profile.Revision,
        ProfileId = profile.Id,
        Revision = profile.Revision,
        Name = profile.Name,
        Content = profile.Content,
    };

    private static ContentBundleDto Bundle(
        string? subjectName = null,
        string? teacher = null,
        string? planName = null,
        string? layoutName = null,
        Dictionary<string, string>? values = null) => new()
    {
        TimeLayouts = layoutName is null ? [] : [new TimeLayoutDto { Id = "tl-1", Name = layoutName }],
        ClassPlans = planName is null ? [] : [new ClassPlanDto { Id = "cp-1", Name = planName, TimeLayoutId = "tl-1" }],
        Subjects = subjectName is null ? [] : [new SubjectDto { Id = "s-1", Name = subjectName, TeacherName = teacher ?? string.Empty }],
        Settings = new ProfileSettingsDto { Values = values ?? [] },
    };

    [Fact]
    public void 没有基线时如实说明并为首次下发列出全部内容()
    {
        var target = Profile("p1", "夏季作息", 3, Bundle("语文", "张老师", "周一至周五", "夏季作息"));

        var diff = ApplyDiffService.Compare(null, 0, null, target);

        Assert.False(diff.HasBaseline);
        Assert.Contains("还没有成功应用过配置", diff.Note);
        Assert.Equal("夏季作息", diff.TargetProfileName);

        // 不能谎称「会改动」，但要把本次会下发的内容列出来。
        Assert.Contains(diff.Sections, s => s.Section == "科目" && s.Added.Count == 1);
        Assert.All(diff.Sections, s => Assert.Empty(s.Removed));
    }

    [Fact]
    public void 同档案同版本时明确告知不会改变任何内容()
    {
        var profile = Profile("p1", "夏季作息", 3, Bundle("语文", "张老师"));

        var diff = ApplyDiffService.Compare(profile, 3, Snapshot(profile), profile);

        Assert.True(diff.HasBaseline);
        Assert.Contains("版本一致", diff.Note);
        Assert.All(diff.Sections, s => Assert.Equal(0, s.Total));
        Assert.DoesNotContain(diff.Sections, s => s.Total > 0);
    }

    [Fact]
    public void 档案改版后用快照当基线识别新增删除与修改()
    {
        // 设备应用的是 v2 的内容（快照），管理员之后把档案改成了 v3。
        var appliedContent = Bundle("语文", "张老师", "回归课表", "回归作息",
            new Dictionary<string, string> { ["ui.theme"] = "dark" });
        var applied = Profile("p1", "夏季作息", 2, appliedContent);

        var newContent = Bundle("数学", "王老师", null, "回归作息",
            new Dictionary<string, string> { ["ui.theme"] = "dark", ["ui.density"] = "compact" });
        var target = Profile("p1", "夏季作息", 3, newContent);

        var diff = ApplyDiffService.Compare(applied, 2, Snapshot(applied), target);

        Assert.True(diff.HasBaseline);
        Assert.Equal(2, diff.AppliedRevision);
        Assert.Equal(3, diff.TargetRevision);
        Assert.Contains("已从 v2 改到 v3", diff.Note);

        var subjects = diff.Sections.Single(s => s.Section == "科目");
        Assert.Single(subjects.Removed);   // 语文被换成数学
        Assert.Single(subjects.Added);     // 数学是新增
        Assert.Contains("数学", subjects.Added[0]);

        var plans = diff.Sections.Single(s => s.Section == "课表");
        Assert.Single(plans.Removed);      // 课表被删掉

        var settings = diff.Sections.Single(s => s.Section == "自定义设置");
        Assert.Single(settings.Added);     // ui.density 新增
        Assert.Contains("ui.density", settings.Added[0]);

        // 时间表两版一致，不应出现在差异里。
        var layouts = diff.Sections.Single(s => s.Section == "时间表");
        Assert.Equal(0, layouts.Total);
    }

    [Fact]
    public void 内容实质相同时不报告差异()
    {
        var content = Bundle("语文", "张老师", "回归课表", "回归作息");
        var applied = Profile("p1", "夏季作息", 2, content);
        var target = Profile("p1", "夏季作息", 5, content); // 只被重新保存过，内容一样

        var diff = ApplyDiffService.Compare(applied, 2, Snapshot(applied), target);

        Assert.All(diff.Sections, s => Assert.Equal(0, s.Total));
        Assert.Contains("实质相同", diff.Note);
    }

    [Fact]
    public void 切换到另一个档案时说明是切换并给出差异()
    {
        var applied = Profile("p1", "夏季作息", 1, Bundle("语文", "张老师"));
        var target = Profile("p2", "冬季作息", 1, Bundle("语文", "李老师"));

        var diff = ApplyDiffService.Compare(applied, 1, null, target);

        Assert.Contains("另一个档案", diff.Note);
        Assert.Equal("夏季作息", diff.AppliedProfileName);
        Assert.Equal("冬季作息", diff.TargetProfileName);

        var subjects = diff.Sections.Single(s => s.Section == "科目");
        Assert.Single(subjects.Changed);   // 同名科目换了老师
    }

    [Fact]
    public void 同名项内容变化计入修改()
    {
        var applied = Profile("p1", "夏季作息", 1, Bundle("语文", "张老师", "回归课表", "回归作息"));
        var target = Profile("p1", "夏季作息", 2, Bundle("语文", "王老师", "回归课表", "回归作息"));

        var diff = ApplyDiffService.Compare(applied, 1, Snapshot(applied), target);

        var subjects = diff.Sections.Single(s => s.Section == "科目");
        Assert.Empty(subjects.Added);
        Assert.Empty(subjects.Removed);
        Assert.Single(subjects.Changed);
        Assert.Contains("王老师", subjects.Changed[0]);
    }
}
