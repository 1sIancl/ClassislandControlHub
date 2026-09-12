using System.Security.Cryptography;
using System.Text;
using ClassIsland.Shared.ComponentModels;
using ClassIsland.Shared.IPC.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using Microsoft.Extensions.Logging;

namespace ControlHub.Plugin.Services;

/// <summary>
/// 配置应用选项。
/// </summary>
public sealed class ApplyOptions
{
    public bool TimeLayouts { get; init; } = true;
    public bool ClassPlans { get; init; } = true;
    public bool Subjects { get; init; } = true;
    public bool Settings { get; init; } = true;
    public bool LockLocalEditing { get; init; }
}

/// <summary>
/// 配置应用结果。
/// </summary>
public sealed class ApplyResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public List<string> AppliedSections { get; init; } = [];
    public string? CurrentClassPlanName { get; init; }
}

/// <summary>
/// 把集控下发的 <see cref="ContentBundleDto"/> 写入 ClassIsland 的当前档案。
/// <para>
/// 这是插件与 ClassIsland 本体交互的唯一入口，隔离了 ClassIsland 2.x 的模型细节：
/// 协议层的 DTO 与 ClassIsland 模型在此处互相转换。后续若 ClassIsland API 调整，
/// 只需修改本文件即可。
/// </para>
/// </summary>
public sealed class ClassIslandAdapter(IPublicProfileService profileService, ILogger<ClassIslandAdapter> logger)
{
    /// <summary>
    /// 应用一份配置包到当前档案并保存。
    /// </summary>
    public ApplyResult Apply(ContentBundleDto content, ApplyOptions options)
    {
        var profile = profileService.Profile ?? new Profile();
        var applied = new List<string>();

        try
        {
            // 科目先写入，供课表节点按 ID 关联。
            if (options.Subjects)
            {
                ReplaceSubjects(profile, content.Subjects);
                applied.Add(HubProtocol.Sections.Subjects);
            }

            // 时间表先于课表，供课表按 ID 关联时间点。
            if (options.TimeLayouts)
            {
                ReplaceTimeLayouts(profile, content.TimeLayouts);
                applied.Add(HubProtocol.Sections.TimeLayouts);
            }

            if (options.ClassPlans)
            {
                ReplaceClassPlans(profile, content.ClassPlans);
                applied.Add(HubProtocol.Sections.ClassPlans);
            }

            // 自定义设置分区不涉及档案结构，仅作为元数据记录，无需写入档案。
            if (options.Settings)
            {
                applied.Add(HubProtocol.Sections.Settings);
            }

            profileService.Profile = profile;
            profileService.SaveProfile();

            var planName = content.ClassPlans.Count > 0
                ? (content.ClassPlans.FirstOrDefault(c => c.IsEnabled)?.Name ?? content.ClassPlans[0].Name)
                : null;

            logger.LogInformation("已应用集控配置：{Sections}。", string.Join(",", applied));
            return new ApplyResult
            {
                Success = true,
                Message = "配置已应用。",
                AppliedSections = applied,
                CurrentClassPlanName = planName,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "应用集控配置失败。");
            return new ApplyResult
            {
                Success = false,
                Message = "应用配置失败：" + ex.Message,
                AppliedSections = applied,
            };
        }
    }

    private static void ReplaceSubjects(Profile profile, List<SubjectDto> subjects)
    {
        var map = new Dictionary<Guid, Subject>();
        foreach (var dto in subjects)
        {
            map[ToGuid(dto.Id)] = new Subject
            {
                Name = dto.Name,
                Initial = dto.Initial,
                TeacherName = dto.TeacherName,
                IsOutDoor = dto.IsOutDoor,
            };
        }

        profile.Subjects = ToOrderedDictionary(map);
    }

    private static void ReplaceTimeLayouts(Profile profile, List<TimeLayoutDto> layouts)
    {
        var map = new Dictionary<Guid, TimeLayout>();
        foreach (var dto in layouts)
        {
            var layout = new TimeLayout { Name = dto.Name };
            foreach (var item in dto.Items)
            {
                layout.Layouts.Add(new TimeLayoutItem
                {
                    StartTime = HubTime.Parse(item.StartTime),
                    EndTime = HubTime.Parse(item.EndTime),
                    TimeType = TimeItemKind.ToTimeType(item.Kind),
                    BreakName = item.BreakName ?? string.Empty,
                    IsHideDefault = item.IsHideDefault,
                    DefaultClassId = string.IsNullOrWhiteSpace(item.DefaultSubjectId)
                        ? Guid.Empty
                        : ToGuid(item.DefaultSubjectId),
                });
            }

            map[ToGuid(dto.Id)] = layout;
        }

        profile.TimeLayouts = ToOrderedDictionary(map);
    }

    private static void ReplaceClassPlans(Profile profile, List<ClassPlanDto> plans)
    {
        var map = new Dictionary<Guid, ClassPlan>();
        foreach (var dto in plans)
        {
            var planId = ToGuid(dto.Id);
            var timeLayoutId = ToGuid(dto.TimeLayoutId);
            var layout = profile.TimeLayouts.TryGetValue(timeLayoutId, out var found) ? found : null;

            var plan = new ClassPlan
            {
                Name = dto.Name,
                TimeLayoutId = timeLayoutId,
                IsEnabled = dto.IsEnabled,
                IsOverlay = dto.IsOverlay,
                OverlaySourceId = string.IsNullOrWhiteSpace(dto.OverlaySourceId)
                    ? null
                    : ToGuid(dto.OverlaySourceId),
                AssociatedGroup = string.IsNullOrWhiteSpace(dto.GroupId)
                    ? ClassPlanGroup.DefaultGroupGuid
                    : ToGuid(dto.GroupId),
                TimeRule = BuildTimeRule(dto),
            };

            // 依据时间表中的「上课」时间点构建课程节点。
            if (layout is not null)
            {
                var classItems = layout.Layouts.Where(i => i.TimeType == 0).ToList();
                var slotMap = (dto.Slots ?? []).GroupBy(s => s.Index).ToDictionary(g => g.Key, g => g.First());

                for (var index = 0; index < classItems.Count; index++)
                {
                    slotMap.TryGetValue(index, out var slot);
                    var classInfo = new ClassInfo
                    {
                        Index = index,
                        CurrentTimeLayout = layout,
                        IsEnabled = slot?.IsEnabled ?? true,
                        SubjectId = string.IsNullOrWhiteSpace(slot?.SubjectId)
                            ? Guid.Empty
                            : ToGuid(slot.SubjectId),
                    };
                    plan.Classes.Add(classInfo);
                }
            }

            map[planId] = plan;
        }

        profile.ClassPlans = ToOrderedDictionary(map);
    }

    private static TimeRule BuildTimeRule(ClassPlanDto dto)
    {
        var rule = new TimeRule
        {
            Type = TimeRule.TimeRuleType.Weekly,
        };

        // 单张课表对应一个星期几；多天课表请在下发端拆分为多张课表。
        if (dto.DaysOfWeek is { Count: > 0 })
        {
            rule.WeekDay = dto.DaysOfWeek[0];
        }

        if (dto.WeekInterval >= 2)
        {
            rule.WeekCountDiv = dto.WeekOffset + 1;
            rule.WeekCountDivTotal = dto.WeekInterval;
        }

        return rule;
    }

    /// <summary>把字典转换为 ClassIsland 的有序字典（按当前顺序保持稳定）。</summary>
    private static ObservableOrderedDictionary<Guid, T> ToOrderedDictionary<T>(Dictionary<Guid, T> source)
        where T : class
    {
        var result = new ObservableOrderedDictionary<Guid, T>();
        foreach (var (key, value) in source)
        {
            result[key] = value;
        }

        return result;
    }

    /// <summary>把协议中的 ID 字符串转换为稳定的 GUID。</summary>
    internal static Guid ToGuid(string? id)
    {
        if (Guid.TryParse(id, out var parsed))
        {
            return parsed;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return Guid.NewGuid();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return new Guid(hash.AsSpan(0, 16));
    }
}
