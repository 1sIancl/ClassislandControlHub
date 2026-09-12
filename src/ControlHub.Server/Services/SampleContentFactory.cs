using ControlHub.Protocol.Dtos;

namespace ControlHub.Server.Services;

/// <summary>
/// 示例内容工厂。
/// <para>
/// 首次部署时，管理员往往不清楚「一套可用的档案长什么样」。
/// 这里提供一份符合中学作息习惯的样板数据（8 节课 + 5 个工作日课表 + 常用科目），
/// 便于一键创建后直接在此基础上修改。
/// </para>
/// </summary>
public static class SampleContentFactory
{
    /// <summary>样板时间表 ID（固定值，便于识别与替换）。</summary>
    public const string SampleTimeLayoutId = "11111111-1111-4111-8111-111111111111";

    /// <summary>科目定义：名称、简称、是否户外、任课教师。</summary>
    private static readonly (string Name, string Initial, bool Outdoor, string Teacher)[] Subjects =
    [
        ("语文", "语", false, ""),
        ("数学", "数", false, ""),
        ("英语", "英", false, ""),
        ("物理", "物", false, ""),
        ("化学", "化", false, ""),
        ("生物", "生", false, ""),
        ("思想政治", "政", false, ""),
        ("历史", "史", false, ""),
        ("地理", "地", false, ""),
        ("体育", "体", true, ""),
        ("音乐", "音", false, ""),
        ("美术", "美", false, ""),
        ("信息技术", "信", false, ""),
        ("自习", "自", false, ""),
    ];

    /// <summary>作息表：<c>(开始, 结束, 类型, 名称)</c>。类型 0 为上课、1 为课间。</summary>
    private static readonly (string Start, string End, string Kind, string? Name)[] Schedule =
    [
        ("08:00:00", "08:45:00", TimeItemKind.Class, null),
        ("08:45:00", "08:55:00", TimeItemKind.Break, null),
        ("08:55:00", "09:40:00", TimeItemKind.Class, null),
        ("09:40:00", "10:10:00", TimeItemKind.Break, "大课间"),
        ("10:10:00", "10:55:00", TimeItemKind.Class, null),
        ("10:55:00", "11:05:00", TimeItemKind.Break, null),
        ("11:05:00", "11:50:00", TimeItemKind.Class, null),
        ("11:50:00", "14:00:00", TimeItemKind.Break, "午休"),
        ("14:00:00", "14:45:00", TimeItemKind.Class, null),
        ("14:45:00", "14:55:00", TimeItemKind.Break, null),
        ("14:55:00", "15:40:00", TimeItemKind.Class, null),
        ("15:40:00", "15:50:00", TimeItemKind.Break, null),
        ("15:50:00", "16:35:00", TimeItemKind.Class, null),
        ("16:35:00", "16:45:00", TimeItemKind.Break, null),
        ("16:45:00", "17:30:00", TimeItemKind.Class, null),
    ];

    /// <summary>一周五天、每天 8 节课的科目安排（按科目名称书写，索引与上课时间点一致）。</summary>
    private static readonly string[][] WeekTemplate =
    [
        // 周一
        ["语文", "数学", "英语", "物理", "化学", "体育", "历史", "自习"],
        // 周二
        ["数学", "英语", "语文", "生物", "地理", "音乐", "化学", "自习"],
        // 周三
        ["英语", "物理", "数学", "语文", "思想政治", "美术", "信息技术", "自习"],
        // 周四
        ["化学", "语文", "生物", "数学", "体育", "历史", "地理", "自习"],
        // 周五
        ["物理", "数学", "英语", "语文", "信息技术", "思想政治", "音乐", "自习"],
    ];

    private static readonly string[] DayNames = ["周一", "周二", "周三", "周四", "周五"];

    /// <summary>
    /// 生成示例内容包。
    /// </summary>
    public static ContentBundleDto Create()
    {
        var content = new ContentBundleDto();

        // 科目：ID 采用稳定的可读 GUID，方便人工核对。
        var subjectIds = new Dictionary<string, string>();
        for (var i = 0; i < Subjects.Length; i++)
        {
            var (name, initial, outdoor, teacher) = Subjects[i];
            var id = $"22222222-2222-4222-8222-{i + 1:D12}";
            subjectIds[name] = id;
            content.Subjects.Add(new SubjectDto
            {
                Id = id,
                Name = name,
                Initial = initial,
                IsOutDoor = outdoor,
                TeacherName = teacher,
            });
        }

        // 时间表
        var layout = new TimeLayoutDto
        {
            Id = SampleTimeLayoutId,
            Name = "标准作息（8 节）",
        };
        layout.Items.AddRange(Schedule.Select(s => new TimeLayoutItemDto
        {
            StartTime = s.Start,
            EndTime = s.End,
            Kind = s.Kind,
            BreakName = s.Name,
        }));
        content.TimeLayouts.Add(layout);

        // 课表：每天一张
        for (var day = 0; day < WeekTemplate.Length; day++)
        {
            var plan = new ClassPlanDto
            {
                Id = $"33333333-3333-4333-8333-{day + 1:D12}",
                Name = DayNames[day],
                TimeLayoutId = SampleTimeLayoutId,
                IsEnabled = true,
                DaysOfWeek = [ToDayOfWeek(day)],
            };

            for (var period = 0; period < WeekTemplate[day].Length; period++)
            {
                var subjectName = WeekTemplate[day][period];
                plan.Slots.Add(new ClassPlanSlotDto
                {
                    Index = period,
                    SubjectId = subjectIds.GetValueOrDefault(subjectName),
                    IsEnabled = true,
                });
            }

            content.ClassPlans.Add(plan);
        }

        content.Settings = new ProfileSettingsDto
        {
            LockLocalEditing = false,
            Announcement = "这是由集控系统生成的示例配置，请按实际情况调整后下发。",
            Values = new Dictionary<string, string>
            {
                ["示例.说明"] = "此键值对演示了自定义配置项的下发方式，插件可按需读取。",
            },
        };

        return content;
    }

    /// <summary>把 0=周一 映射为 <see cref="DayOfWeek"/> 的取值（0=周日）。</summary>
    private static int ToDayOfWeek(int mondayBasedIndex) => (mondayBasedIndex + 1) % 7;
}
