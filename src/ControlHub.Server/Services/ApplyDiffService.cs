using System.Text.Json;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;

namespace ControlHub.Server.Services;

/// <summary>
/// 「配置下发差异预览」：对比设备当前实际应用的档案与即将下发的档案，按分区给出新增 / 删除 / 修改清单。
/// <para>只做**名称级**对比——时间表 / 课表 / 科目按名称匹配，自定义设置按键匹配。目的是让管理员在下发前
/// 知道「这次会动到什么」，不是逐字段 diff；同名项内容有变会落在「修改」里。</para>
/// </summary>
public static class ApplyDiffService
{
    /// <summary>比较「已应用档案」与「目标档案」，生成差异预览。</summary>
    /// <param name="appliedProfile">设备当前实际应用的档案，为空表示还没有基线。</param>
    /// <param name="appliedProfileRevision">设备应用该档案时的版本号。</param>
    /// <param name="appliedSnapshot">
    /// 该版本的历史快照（保存前留的那一份）。**必须有它才能算出真实差异**：
    /// 档案被编辑后，档案行本身已是新内容，直接拿它对比会得出「没有差异」的错误结论。
    /// </param>
    /// <param name="targetProfile">本次将下发的档案。</param>
    public static ApplyDiffDto Compare(ProfileRow? appliedProfile, long appliedProfileRevision,
        ProfileVersionRow? appliedSnapshot, ProfileRow targetProfile)
    {
        var target = HubJson.DeserializeOrDefault(targetProfile.Content, new ContentBundleDto());
        var result = new ApplyDiffDto
        {
            HasBaseline = appliedProfile is not null,
            AppliedProfileName = appliedProfile?.Name,
            AppliedRevision = appliedProfileRevision,
            TargetProfileName = targetProfile.Name,
            TargetRevision = targetProfile.Revision,
        };

        if (appliedProfile is null)
        {
            // 没有基线：不谎称「会改动」，而是如实说明「这是首次下发，下面是全部内容」。
            result.Note = "该设备还没有成功应用过配置，暂时没有可对比的基线；下面是本次将首次下发的全部内容。";
            result.Sections = DescribeAll(target);
            return result;
        }

        var sameProfile = string.Equals(appliedProfile.Id, targetProfile.Id, StringComparison.Ordinal);
        if (sameProfile && appliedProfileRevision == targetProfile.Revision)
        {
            result.Note = "设备当前应用的档案与本次要下发的档案是同一个、且版本一致，下发不会改变任何内容"
                          + "（仅刷新一次同步时间）。";
            return result;
        }

        // 基线内容优先取「应用当时的快照」；确实没有快照时才退化为档案当前内容，并在结论里说明。
        var applied = HubJson.DeserializeOrDefault(
            appliedSnapshot is null ? appliedProfile.Content : appliedSnapshot.Content, new ContentBundleDto());

        result.Sections = CompareBundles(applied, target);

        if (result.Sections.Any(s => s.Total > 0))
        {
            result.Note = sameProfile
                ? $"档案「{targetProfile.Name}」已从 v{appliedProfileRevision} 改到 v{targetProfile.Revision}，"
                  + "本次下发会把设备刷新到这个新版（差异如下）。"
                : $"设备当前应用的是另一个档案「{appliedProfile.Name}」，本次将切换为「{targetProfile.Name}」"
                  + "（差异如下）。";

            if (appliedSnapshot is null && !sameProfile)
            {
                result.Note += "　注：缺少旧档案当时的历史快照，这里是按它当前的内容对比，差异可能略有偏差。";
            }
        }
        else
        {
            result.Note = "两份内容的实质相同（可能只是被重新保存过），下发后界面上不会出现变化。";
        }

        return result;
    }

    // ────────────────────────────── 分区比较 ──────────────────────────────

    private static List<ApplyDiffSectionDto> CompareBundles(ContentBundleDto from, ContentBundleDto to) =>
    [
        CompareTimeLayouts(from, to),
        CompareClassPlans(from, to),
        CompareSubjects(from, to),
        CompareSettings(from.Settings ?? new ProfileSettingsDto(), to.Settings ?? new ProfileSettingsDto()),
    ];

    private static ApplyDiffSectionDto CompareTimeLayouts(ContentBundleDto from, ContentBundleDto to)
    {
        var section = new ApplyDiffSectionDto { Section = "时间表" };
        var old = ByName(from.TimeLayouts, x => x.Name);
        var now = ByName(to.TimeLayouts, x => x.Name);

        foreach (var (name, item) in now)
        {
            if (!old.TryGetValue(name, out var before))
            {
                section.Added.Add(DescribeTimeLayout(item));
            }
            else if (!Same(before, item))
            {
                section.Changed.Add($"{DescribeTimeLayout(item)}（原 {before.Items.Count} 个时间点 / "
                                    + $"{before.ClassItemCount} 节 → 现 {item.Items.Count} 个时间点 / {item.ClassItemCount} 节）");
            }
        }

        foreach (var name in old.Keys.Where(k => !now.ContainsKey(k)))
        {
            section.Removed.Add(name);
        }

        return section;
    }

    private static ApplyDiffSectionDto CompareClassPlans(ContentBundleDto from, ContentBundleDto to)
    {
        var section = new ApplyDiffSectionDto { Section = "课表" };
        var layouts = ByName(to.TimeLayouts, x => x.Id);
        var old = ByName(from.ClassPlans, x => x.Name);
        var now = ByName(to.ClassPlans, x => x.Name);

        foreach (var (name, item) in now)
        {
            if (!old.TryGetValue(name, out var before))
            {
                section.Added.Add(DescribePlan(item, layouts));
            }
            else if (!Same(before, item))
            {
                section.Changed.Add(DescribePlan(item, layouts));
            }
        }

        foreach (var (name, item) in old.Where(kv => !now.ContainsKey(kv.Key)))
        {
            section.Removed.Add(DescribePlan(item, layouts));
        }

        return section;
    }

    private static ApplyDiffSectionDto CompareSubjects(ContentBundleDto from, ContentBundleDto to)
    {
        var section = new ApplyDiffSectionDto { Section = "科目" };
        var old = ByName(from.Subjects, x => x.Name);
        var now = ByName(to.Subjects, x => x.Name);

        foreach (var (name, item) in now)
        {
            if (!old.TryGetValue(name, out var before))
            {
                section.Added.Add(DescribeSubject(item));
            }
            else if (!Same(before, item))
            {
                section.Changed.Add(DescribeSubject(item));
            }
        }

        foreach (var name in old.Keys.Where(k => !now.ContainsKey(k)))
        {
            section.Removed.Add(name);
        }

        return section;
    }

    private static ApplyDiffSectionDto CompareSettings(ProfileSettingsDto from, ProfileSettingsDto to)
    {
        var section = new ApplyDiffSectionDto { Section = "自定义设置" };
        var old = from.Values ?? [];
        var now = to.Values ?? [];

        foreach (var (key, value) in now)
        {
            if (!old.TryGetValue(key, out var before))
            {
                section.Added.Add($"{key} = {value}");
            }
            else if (!string.Equals(before, value, StringComparison.Ordinal))
            {
                section.Changed.Add($"{key}：{before} → {value}");
            }
        }

        foreach (var (key, value) in old.Where(kv => !now.ContainsKey(kv.Key)))
        {
            section.Removed.Add($"{key} = {value}");
        }

        if (from.LockLocalEditing != to.LockLocalEditing)
        {
            section.Changed.Add($"锁定客户端本地编辑：{(from.LockLocalEditing ? "开" : "关")} → "
                                + $"{(to.LockLocalEditing ? "开" : "关")}");
        }

        if (!string.Equals(from.Announcement ?? string.Empty, to.Announcement ?? string.Empty, StringComparison.Ordinal))
        {
            section.Changed.Add(to.Announcement is null || to.Announcement.Length == 0
                ? "清空下发提示信息"
                : $"下发提示信息：{Shorten(to.Announcement)}");
        }

        return section;
    }

    /// <summary>无基线时：把目标内容全部列为「将下发」，让管理员仍能看到这次会带过去什么。</summary>
    private static List<ApplyDiffSectionDto> DescribeAll(ContentBundleDto target) =>
    [
        new ApplyDiffSectionDto
        {
            Section = "时间表",
            Added = target.TimeLayouts.Select(DescribeTimeLayout).ToList(),
        },
        new ApplyDiffSectionDto
        {
            Section = "课表",
            Added = target.ClassPlans.Select(p => DescribePlan(p, ByName(target.TimeLayouts, x => x.Id))).ToList(),
        },
        new ApplyDiffSectionDto
        {
            Section = "科目",
            Added = target.Subjects.Select(DescribeSubject).ToList(),
        },
        new ApplyDiffSectionDto
        {
            Section = "自定义设置",
            Added = (target.Settings?.Values ?? []).Select(kv => $"{kv.Key} = {kv.Value}").ToList(),
        },
    ];

    // ────────────────────────────── 小工具 ──────────────────────────────

    private static string DescribeTimeLayout(TimeLayoutDto layout) =>
        $"{layout.Name}（{layout.Items.Count} 个时间点 / {layout.ClassItemCount} 节）";

    private static string DescribePlan(ClassPlanDto plan, Dictionary<string, TimeLayoutDto> layouts)
    {
        var layout = layouts.TryGetValue(plan.TimeLayoutId, out var found) ? found.Name : null;
        var suffix = layout is null ? string.Empty : $"（使用时间表：{layout}）";
        return plan.Name + suffix;
    }

    private static string DescribeSubject(SubjectDto subject) =>
        string.IsNullOrWhiteSpace(subject.TeacherName) ? subject.Name : $"{subject.Name}（{subject.TeacherName}）";

    private static string Shorten(string text) => text.Length <= 30 ? text : text[..30] + "…";

    private static Dictionary<string, T> ByName<T>(IEnumerable<T> items, Func<T, string> nameOf)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            map.TryAdd(nameOf(item), item);
        }

        return map;
    }

    private static bool Same<T>(T left, T right) =>
        string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);
}
