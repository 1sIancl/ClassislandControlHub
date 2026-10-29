using System.IO.Compression;
using System.Text;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Services;

namespace IslandHub.Tests;

/// <summary>
/// `.cidata`（ClassIsland 数据迁移包）解析与映射。
///
/// <para>这份测试的价值主要在「**不静默改语义**」上：包里的东西 IslandHub 装不下时（日历调休、自动化、
/// 日期型课表规则、临时层课表），要么跳过、要么只在文案里降级，都必须留下痕迹——
/// 悄悄按星期几塞进去的话，管理员要到上课那天才发现整周的课表都错了。</para>
/// </summary>
public sealed class CidataImporterTests
{
    private const string LayoutId = "11111111-1111-4111-8111-111111111111";
    private const string PlanId = "22222222-2222-4222-8222-222222222221";
    private const string ChineseId = "33333333-3333-4333-8333-333333333331";
    private const string GhostId = "33333333-3333-4333-8333-333333333399";
    private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";
    private const string DefaultGroupId = "acaf4ef0-e261-4262-b941-34ea93cb4369";

    // ── 夹具 ──

    /// <summary>一份内容齐全的档案：6 个时间点（3 上課 + 课间 + 分割线 + 行动）、1 张周一课表、1 个科目。</summary>
    private static string RichProfileJson() => $$"""
    {
      "Name": "示例中学",
      "TimeLayouts": {
        "{{LayoutId}}": {
          "Name": "夏季作息",
          "Layouts": [
            { "StartTime": "08:00:00", "EndTime": "08:45:00", "TimeType": 0, "BreakName": "", "IsHideDefault": false,
              "DefaultClassId": "{{ChineseId}}", "AttachedObjects": {}, "IsActive": false },
            { "StartTime": "08:45:00", "EndTime": "08:55:00", "TimeType": 1, "BreakName": "大课间" },
            { "StartTime": "08:55:00", "EndTime": "09:40:00", "TimeType": 0, "BreakName": "" },
            { "StartTime": "09:40:00", "EndTime": "09:40:00", "TimeType": 2 },
            { "StartTime": "09:40:00", "EndTime": "10:25:00", "TimeType": 0 },
            { "StartTime": "10:25:00", "EndTime": "10:30:00", "TimeType": 3, "BreakName": "眼保健操",
              "Action": { "Id": "44444444-4444-4444-8444-444444444441", "Name": "课前提醒", "Type": "Notification",
                          "Settings": { "Title": "准备上课", "Content": "请回到座位" } } }
          ]
        }
      },
      "ClassPlans": {
        "{{PlanId}}": {
          "Name": "周一",
          "TimeLayoutId": "{{LayoutId}}",
          "IsEnabled": true,
          "AssociatedGroup": "{{DefaultGroupId}}",
          "TimeRule": { "Type": 0, "WeekDay": 1, "WeekCountDiv": 0 },
          "Classes": [
            { "SubjectId": "{{ChineseId}}", "IsEnabled": true },
            { "SubjectId": "{{EmptyGuid}}", "IsEnabled": true },
            { "SubjectId": "{{GhostId}}", "IsEnabled": false }
          ]
        }
      },
      "Subjects": {
        "{{ChineseId}}": { "Name": "语文", "Initial": "语", "TeacherName": "张老师", "IsOutDoor": false }
      },
      "ClassPlanGroups": {
        "{{DefaultGroupId}}": { "Name": "默认", "IsGlobal": false },
        "{{EmptyGuid}}": { "Name": "全局课表群", "IsGlobal": true }
      },
      "OrderedSchedules": { "2026-10-01T00:00:00": { "ClassPlanId": "{{PlanId}}" } }
    }
    """;

    /// <summary>按宿主的导出逻辑拼一个 .cidata（ZIP）。</summary>
    private static MemoryStream BuildCidata(params (string Path, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static CidataProfileEntry Convert(string json = "")
    {
        var archive = CidataImporter.Inspect(BuildCidata(("Profiles/Default.json", json == "" ? RichProfileJson() : json)));
        return archive.Profiles.Single();
    }

    // ── 包级行为 ──

    [Fact]
    public void 能读出包里的档案与包级信息()
    {
        using var cidata = BuildCidata(
            ("Settings.json", """{"Scale":1.0}"""),
            ("Profiles/Default.json", RichProfileJson()),
            ("Config/Automations/Default.json", "[]"),
            ("Config/ComponentLayouts/Default.json", "{}"),
            ("Plugins/controlhub.client/settings.json", "{}"));

        var archive = CidataImporter.Inspect(cidata);

        Assert.Single(archive.Profiles);
        Assert.True(archive.HasSettings);
        Assert.Equal(2, archive.ConfigEntryCount);
        Assert.Equal(1, archive.PluginEntryCount);

        // 「没有落点」的部分必须如实说出来，而不是让管理员以为整包都进来了。
        var notes = archive.NotImportedNotes();
        Assert.Equal(3, notes.Count);
        Assert.Contains(notes, n => n.Contains("应用设置"));
        Assert.Contains(notes, n => n.Contains("Config"));
        Assert.Contains(notes, n => n.Contains("Plugins"));
    }

    [Fact]
    public void 一份包里可以有多个档案()
    {
        using var cidata = BuildCidata(
            ("Profiles/Default.json", RichProfileJson()),
            ("Profiles/夏季作息.json", RichProfileJson().Replace("示例中学", "夏季作息")));

        var archive = CidataImporter.Inspect(cidata);

        Assert.Equal(2, archive.Profiles.Count);
        Assert.Contains(archive.Profiles, p => p.FileName == "Default.json");
        Assert.Contains(archive.Profiles, p => p.FileName == "夏季作息.json");
    }

    [Fact]
    public void 不是ZIP时给出可照做的提示()
    {
        using var notZip = new MemoryStream(Encoding.UTF8.GetBytes("这不是一个 zip"));

        var ex = Assert.Throws<InvalidDataException>(() => CidataImporter.Inspect(notZip));
        Assert.Contains("有效的 .cidata", ex.Message);
    }

    [Fact]
    public void 包里没有档案时说清是没勾选项()
    {
        using var cidata = BuildCidata(("Settings.json", "{}"));

        var ex = Assert.Throws<InvalidDataException>(() => CidataImporter.Inspect(cidata));
        Assert.Contains("Profiles/", ex.Message);
        Assert.Contains("课表与档案", ex.Message);
    }

    [Fact]
    public void 嵌套目录下的json不会被当成档案()
    {
        using var cidata = BuildCidata(
            ("Profiles/Default.json", RichProfileJson()),
            ("Profiles/sub/Nested.json", RichProfileJson()));

        Assert.Single(CidataImporter.Inspect(cidata).Profiles);
    }

    [Fact]
    public void 档案数量超上限时拒绝()
    {
        var entries = Enumerable.Range(0, CidataImporter.MaxProfileCount + 1)
            .Select(i => ($"Profiles/P{i}.json", """{"Name":"x"}"""))
            .ToArray();

        using var cidata = BuildCidata(entries);

        var ex = Assert.Throws<InvalidDataException>(() => CidataImporter.Inspect(cidata));
        Assert.Contains("超过", ex.Message);
    }

    [Fact]
    public void 档案不是合法json时报错并带上文件名()
    {
        using var cidata = BuildCidata(("Profiles/Broken.json", "{ 这不是 json"));

        var ex = Assert.Throws<InvalidDataException>(() => CidataImporter.Inspect(cidata));
        Assert.Contains("Broken.json", ex.Message);
    }

    [Fact]
    public void 档案名为空时用文件名()
    {
        var entry = Convert($$"""
        { "Name": "", "TimeLayouts": {}, "ClassPlans": {}, "Subjects": {}, "ClassPlanGroups": {} }
        """);

        Assert.Equal("Default", entry.ProfileName);
    }

    // ── 时间表 ──

    [Fact]
    public void 时间点按类型映射不做错位()
    {
        var entry = Convert();
        var layout = entry.Content.TimeLayouts.Single();

        Assert.Equal("夏季作息", layout.Name);
        Assert.Equal(6, layout.Items.Count);
        Assert.Equal(
            [TimeItemKind.Class, TimeItemKind.Break, TimeItemKind.Class, TimeItemKind.Separator, TimeItemKind.Class, TimeItemKind.Action],
            layout.Items.Select(i => i.Kind));

        Assert.Equal("08:00:00", layout.Items[0].StartTime);
        Assert.Equal("08:45:00", layout.Items[0].EndTime);
        Assert.Equal("大课间", layout.Items[1].BreakName);
        Assert.Equal("眼保健操", layout.Items[5].BreakName);

        // 默认科目：宿主用全零 GUID 表示「没指定」，IslandHub 这边必须是 null（不是空字符串、不是全零串）。
        Assert.Equal(ChineseId, layout.Items[0].DefaultSubjectId);
        Assert.Null(layout.Items[1].DefaultSubjectId);

        // 只有「上课」项算节数。
        Assert.Equal(3, layout.ClassItemCount);
    }

    [Fact]
    public void 课间与分割线的起止时间原样保留()
    {
        var entry = Convert();
        var items = entry.Content.TimeLayouts.Single().Items;

        // 分割线的起止时间是同一个点，这是宿主的表达方式，不要「修」成别的。
        Assert.Equal(items[3].StartTime, items[3].EndTime);
    }

    [Fact]
    public void 分割线与行动是独立时间点并被单独统计()
    {
        var entry = Convert();

        // 两者都是 Layouts 数组里的独立元素（TimeType 2 / 3），不是上课节点的附属属性。
        Assert.Single(entry.Separators);
        Assert.Equal("09:40", entry.Separators[0].Time);
        Assert.Equal("夏季作息", entry.Separators[0].Layout);

        Assert.Single(entry.Actions);
        Assert.Equal("课前提醒", entry.Actions[0].Name);
        Assert.Equal("Notification", entry.Actions[0].Type);
        Assert.Equal(1, entry.ActionTypeCounts["Notification"]);
    }

    [Fact]
    public void 行动内容不随包导入时明确写在预览里()
    {
        var entry = Convert();

        // 关键不是"能不能导入"，而是**不能导入的那部分有没有被说出来**：
        // 位置导进去了、内容没跟着走，如果不说，教室端到点什么都不做且没人知道为什么。
        Assert.Contains(entry.Warnings, w => w.Contains("「行动」时间点已按原位置导入"));
        Assert.Contains(entry.Warnings, w => w.Contains("不随包导入"));
    }

    [Fact]
    public void 未知的行动类型降级为普通时间点并提示()
    {
        var entry = Convert($$"""
        {
          "Name": "插件行动",
          "TimeLayouts": {
            "{{LayoutId}}": {
              "Name": "插件作息",
              "Layouts": [
                { "StartTime": "08:00:00", "EndTime": "08:45:00", "TimeType": 0 },
                { "StartTime": "09:00:00", "EndTime": "09:00:00", "TimeType": 3,
                  "Action": { "Id": "55555555-5555-4555-8555-555555555551", "Name": "第三方动作", "Type": "PluginThing" } }
              ]
            }
          },
          "ClassPlans": {},
          "Subjects": {}
        }
        """);

        // 未知 Type 不能抛异常、也不能把整个时间点丢掉——按「到点的行动节点」导入，内容留给人工确认。
        Assert.Single(entry.Actions);
        Assert.Equal("PluginThing", entry.Actions[0].Type);
        Assert.Contains(entry.Warnings, w => w.Contains("无法识别"));
        Assert.Equal(
            [TimeItemKind.Class, TimeItemKind.Action],
            entry.Content.TimeLayouts.Single().Items.Select(i => i.Kind));
    }

    [Fact]
    public void 分割线结束时间与开始时间不一致时保留原值并提示()
    {
        var entry = Convert($$"""
        {
          "Name": "脏数据",
          "TimeLayouts": {
            "{{LayoutId}}": {
              "Name": "脏作息",
              "Layouts": [
                { "StartTime": "08:00:00", "EndTime": "08:45:00", "TimeType": 0 },
                { "StartTime": "09:00:00", "EndTime": "09:30:00", "TimeType": 2 }
              ]
            }
          },
          "ClassPlans": {},
          "Subjects": {}
        }
        """);

        // 历史数据里确实存在这种"分割线有长度"的脏数据：原样保留，但要说一句。
        var separator = entry.Content.TimeLayouts.Single().Items[1];
        Assert.Equal(TimeItemKind.Separator, separator.Kind);
        Assert.Equal("09:00:00", separator.StartTime);
        Assert.Equal("09:30:00", separator.EndTime);
        Assert.Contains(entry.Warnings, w => w.Contains("结束时间与开始时间不一致"));
    }

    [Fact]
    public void 兼容旧版StartSecond字段()
    {
        var entry = Convert($$"""
        {
          "Name": "老档案",
          "TimeLayouts": {
            "{{LayoutId}}": {
              "Name": "旧作息",
              "Layouts": [
                { "StartSecond": "2024-05-25T08:00:00", "EndSecond": "2024-05-25T08:45:00", "TimeType": 0 }
              ]
            }
          },
          "ClassPlans": {}, "Subjects": {}, "ClassPlanGroups": {}
        }
        """);

        var item = entry.Content.TimeLayouts.Single().Items.Single();
        Assert.Equal("08:00:00", item.StartTime);
        Assert.Equal("08:45:00", item.EndTime);
    }

    [Fact]
    public void 时间点解析不出来时整张时间表跳过并连带课表()
    {
        var entry = Convert($$"""
        {
          "Name": "坏档案",
          "TimeLayouts": {
            "{{LayoutId}}": {
              "Name": "坏作息",
              "Layouts": [ { "StartTime": "上午八点", "EndTime": "08:45:00", "TimeType": 0 } ]
            }
          },
          "ClassPlans": {
            "{{PlanId}}": {
              "Name": "周一", "TimeLayoutId": "{{LayoutId}}", "IsEnabled": true,
              "TimeRule": { "WeekDay": 1 },
              "Classes": [ { "SubjectId": "{{EmptyGuid}}" } ]
            }
          },
          "Subjects": {}, "ClassPlanGroups": {}
        }
        """);

        Assert.Empty(entry.Content.TimeLayouts);
        Assert.Empty(entry.Content.ClassPlans);
        Assert.Contains(entry.Warnings, w => w.Contains("坏作息") && w.Contains("无法解析"));
        Assert.Contains(entry.Warnings, w => w.Contains("一并跳过"));
    }

    // ── 科目与课表 ──

    [Fact]
    public void 科目字段原样映射()
    {
        var subject = Convert().Content.Subjects.Single();

        Assert.Equal(ChineseId, subject.Id);
        Assert.Equal("语文", subject.Name);
        Assert.Equal("语", subject.Initial);
        Assert.Equal("张老师", subject.TeacherName);
        Assert.False(subject.IsOutDoor);
    }

    [Fact]
    public void 一张课表对应一个星期几而不是合并整周()
    {
        var plan = Convert().Content.ClassPlans.Single();

        // 宿主「一张课表 = 一天」，IslandHub 也是（DaysOfWeek 取单个值），不要合并成周一至周五。
        Assert.Equal([1], plan.DaysOfWeek);
        Assert.Equal("周一", plan.Name);
        Assert.Equal(LayoutId, plan.TimeLayoutId);
        Assert.True(plan.IsEnabled);
    }

    [Fact]
    public void 逐格课程按上课时间点对齐且跳过课间分割线()
    {
        var plan = Convert().Content.ClassPlans.Single();

        Assert.Equal(3, plan.Slots.Count);
        Assert.Equal([0, 1, 2], plan.Slots.Select(s => s.Index));

        // 槽位时间是第 1/3/5 个上课时间点，而不是时间表里的前三项。
        Assert.Equal(["08:00:00", "08:55:00", "09:40:00"], plan.Slots.Select(s => s.StartTime));

        Assert.Equal(ChineseId, plan.Slots[0].SubjectId);
        Assert.True(plan.Slots[0].IsEnabled);

        // 全零 GUID = 空堂，必须落成 null。
        Assert.Null(plan.Slots[1].SubjectId);

        // 指向包内不存在的科目：留空而不是留一个悬空 ID（悬空 ID 会让教室端显示一格空白且查不出原因）。
        Assert.Null(plan.Slots[2].SubjectId);
        Assert.False(plan.Slots[2].IsEnabled);
    }

    [Fact]
    public void 单双周映射到周次间隔与偏移()
    {
        var odd = Convert(WithRotation(div: 1, total: 2)).Content.ClassPlans.Single();
        Assert.Equal(2, odd.WeekInterval);
        Assert.Equal(0, odd.WeekOffset);

        var even = Convert(WithRotation(div: 2, total: 2)).Content.ClassPlans.Single();
        Assert.Equal(2, even.WeekInterval);
        Assert.Equal(1, even.WeekOffset);

        // 每周都生效：两个字段都为 0。
        var weekly = Convert(WithRotation(div: 0, total: 2)).Content.ClassPlans.Single();
        Assert.Equal(0, weekly.WeekInterval);
        Assert.Equal(0, weekly.WeekOffset);

        // 三周轮换：第 2 周 → 周期 3、偏移 1。
        var three = Convert(WithRotation(div: 2, total: 3)).Content.ClassPlans.Single();
        Assert.Equal(3, three.WeekInterval);
        Assert.Equal(1, three.WeekOffset);
    }

    [Fact]
    public void 字段省略时按每周生效处理()
    {
        // 宿主对默认值会省略字段（TimeRule/WeekCountDiv 默认 0、WeekCountDivTotal 默认 2）。
        var plan = Convert($$"""
        {
          "Name": "省略字段",
          "TimeLayouts": {
            "{{LayoutId}}": { "Name": "作息", "Layouts": [ { "StartTime": "08:00:00", "EndTime": "08:45:00", "TimeType": 0 } ] }
          },
          "ClassPlans": {
            "{{PlanId}}": { "Name": "周二", "TimeLayoutId": "{{LayoutId}}", "IsEnabled": true,
              "Classes": [ { "SubjectId": "{{EmptyGuid}}" } ] }
          },
          "Subjects": {}, "ClassPlanGroups": {}
        }
        """).Content.ClassPlans.Single();

        // WeekDay 省略 → 0 = 周日（宿主的编码就是 0=周日，这里不做「默认周一」的猜测）。
        Assert.Equal([0], plan.DaysOfWeek);
        Assert.Equal(0, plan.WeekInterval);
        Assert.Equal(0, plan.WeekOffset);
    }

    [Fact]
    public void 日期型与循环型触发规则的课表被跳过()
    {
        foreach (var type in new[] { 1, 2 })
        {
            var entry = Convert(WithRotation(div: 0, total: 2, ruleType: type));

            Assert.Empty(entry.Content.ClassPlans);
            Assert.Contains(entry.Warnings, w => w.Contains("日期型") && w.Contains("周一"));
        }
    }

    [Fact]
    public void 节数与时间表不一致时给出警告但不越界()
    {
        var entry = Convert($$"""
        {
          "Name": "错位档案",
          "TimeLayouts": {
            "{{LayoutId}}": { "Name": "作息", "Layouts": [ { "StartTime": "08:00:00", "EndTime": "08:45:00", "TimeType": 0 } ] }
          },
          "ClassPlans": {
            "{{PlanId}}": { "Name": "周一", "TimeLayoutId": "{{LayoutId}}", "IsEnabled": true,
              "TimeRule": { "WeekDay": 1 },
              "Classes": [ { "SubjectId": "{{ChineseId}}" }, { "SubjectId": "{{ChineseId}}" } ] }
          },
          "Subjects": { "{{ChineseId}}": { "Name": "语文" } },
          "ClassPlanGroups": {}
        }
        """);

        // 时间表只有 1 个上课点 → 只取第 1 格；多出来的那格不能造出一个对不上的节次。
        Assert.Single(entry.Content.ClassPlans.Single().Slots);
        Assert.Contains(entry.Warnings, w => w.Contains("对不上"));
    }

    [Fact]
    public void 生效日期范围与启用日期都会提示()
    {
        var entry = Convert($$"""
        {
          "Name": "范围档案",
          "TimeLayouts": {
            "{{LayoutId}}": { "Name": "作息", "Layouts": [ { "StartTime": "08:00:00", "EndTime": "08:45:00", "TimeType": 0 } ] }
          },
          "ClassPlans": {
            "{{PlanId}}": { "Name": "周一", "TimeLayoutId": "{{LayoutId}}", "IsEnabled": true,
              "TimeRule": { "WeekDay": 1, "RestrictsEnableRange": true, "RangeStart": "2026-09-01", "RangeEnd": "2026-10-31",
                            "EnableDates": [ "2026-10-08" ] },
              "Classes": [ { "SubjectId": "{{EmptyGuid}}" } ] }
          },
          "Subjects": {}, "ClassPlanGroups": {}
        }
        """);

        Assert.Single(entry.Content.ClassPlans);
        Assert.Contains(entry.Warnings, w => w.Contains("生效日期范围"));
        Assert.Contains(entry.Warnings, w => w.Contains("启用日期"));
    }

    // ── 课表群 ──

    [Fact]
    public void 默认课表群留空全局课表群与自定义群原样保留()
    {
        var entry = Convert($$"""
        {
          "Name": "群档案",
          "TimeLayouts": {
            "{{LayoutId}}": { "Name": "作息", "Layouts": [ { "StartTime": "08:00:00", "EndTime": "08:45:00", "TimeType": 0 } ] }
          },
          "ClassPlans": {
            "22222222-2222-4222-8222-222222222221": {
              "Name": "默认群课表", "TimeLayoutId": "{{LayoutId}}", "IsEnabled": true, "TimeRule": { "WeekDay": 1 },
              "AssociatedGroup": "{{DefaultGroupId}}", "Classes": [] },
            "22222222-2222-4222-8222-222222222222": {
              "Name": "全局群课表", "TimeLayoutId": "{{LayoutId}}", "IsEnabled": true, "TimeRule": { "WeekDay": 2 },
              "AssociatedGroup": "{{EmptyGuid}}", "Classes": [] },
            "22222222-2222-4222-8222-222222222223": {
              "Name": "走班课表", "TimeLayoutId": "{{LayoutId}}", "IsEnabled": true, "TimeRule": { "WeekDay": 3 },
              "AssociatedGroup": "44444444-4444-4444-8444-444444444444", "Classes": [] }
          },
          "Subjects": {},
          "ClassPlanGroups": {
            "{{DefaultGroupId}}": { "Name": "默认", "IsGlobal": false },
            "{{EmptyGuid}}": { "Name": "全局课表群", "IsGlobal": true }
          }
        }
        """);

        var byName = entry.Content.ClassPlans.ToDictionary(p => p.Name, p => p.GroupId);

        // 默认群：留空，教室端按自己的默认群处理。
        Assert.Null(byName["默认群课表"]);
        // 全局群是真实存在的群，必须原样带上（留空会变成「默认群」，属于改语义）。
        Assert.Equal(EmptyGuid, byName["全局群课表"]);
        Assert.Equal("44444444-4444-4444-8444-444444444444", byName["走班课表"]);

        Assert.Equal(2, entry.Groups.Count);
        Assert.Contains(entry.Groups, g => g.IsGlobal && g.Name == "全局课表群");
    }

    // ── 装不下的部分 ──

    [Fact]
    public void 日历调休与临时层课表不导入但如实统计()
    {
        var entry = Convert($$"""
        {
          "Name": "调休档案",
          "TimeLayouts": {
            "{{LayoutId}}": { "Name": "作息", "Layouts": [ { "StartTime": "08:00:00", "EndTime": "08:45:00", "TimeType": 0 } ] }
          },
          "ClassPlans": {
            "{{PlanId}}": { "Name": "周一", "TimeLayoutId": "{{LayoutId}}", "IsEnabled": true, "TimeRule": { "WeekDay": 1 },
              "Classes": [ { "SubjectId": "{{EmptyGuid}}" } ] },
            "22222222-2222-4222-8222-222222222299": { "Name": "临时层", "TimeLayoutId": "{{LayoutId}}", "IsEnabled": true,
              "IsOverlay": true, "TimeRule": { "WeekDay": 1 }, "Classes": [] }
          },
          "Subjects": {}, "ClassPlanGroups": {},
          "OrderedSchedules": {
            "2026-10-01T00:00:00": { "ClassPlanId": "{{PlanId}}" },
            "2026-10-02T00:00:00": { "ClassPlanId": "{{PlanId}}" }
          }
        }
        """);

        Assert.Single(entry.Content.ClassPlans);
        Assert.Equal(2, entry.SkippedOrderedSchedules);
        Assert.Equal(1, entry.SkippedOverlayPlans);
        Assert.Contains(entry.Warnings, w => w.Contains("调休") && w.Contains("2 条"));
        Assert.Contains(entry.Warnings, w => w.Contains("临时层"));
    }

    [Fact]
    public void 内容为空时不报错只是没有东西()
    {
        // 真实的 Default.json 就是这样：科目、时间表、课表全空，只有两个课表群。
        var entry = Convert("""
        {
          "Name": "", "TimeLayouts": {}, "ClassPlans": {}, "Subjects": {},
          "ClassPlanGroups": {
            "acaf4ef0-e261-4262-b941-34ea93cb4369": { "Name": "默认", "IsGlobal": false },
            "00000000-0000-0000-0000-000000000000": { "Name": "全局课表群", "IsGlobal": true }
          },
          "OrderedSchedules": {}, "Id": "7da32b95-0000-0000-0000-000000000000", "IsActive": false
        }
        """);

        Assert.Empty(entry.Content.Subjects);
        Assert.Empty(entry.Content.TimeLayouts);
        Assert.Empty(entry.Content.ClassPlans);
        Assert.Equal(2, entry.Groups.Count);
        Assert.Empty(entry.Warnings);
    }

    // ── 多周轮换（轮换起点 / 手动偏移来自包内的 Settings.json）──

    [Fact]
    public void 多周课表会连轮换起点与手动偏移一起导入()
    {
        using var cidata = BuildCidata(
            ("Settings.json", """
            {
              "SingleWeekStartTime": "2026-09-06T00:00:00",
              "MultiWeekRotationOffset": [-1, -1, 1, 0, 0],
              "Theme": 2
            }
            """),
            ("Profiles/Default.json", WithRotation(div: 1, total: 2)));

        var archive = CidataImporter.Inspect(cidata);
        var entry = archive.Profiles.Single();

        Assert.True(archive.HasSettings);
        Assert.NotNull(archive.HostRotation);
        Assert.Equal(2, entry.Content.Rotation.Weeks);
        Assert.Equal("2026-09-06", entry.Content.Rotation.SemesterStartDate);
        // 下标 = 周期长度：2 周这一档是 1（临时调过一周）。
        Assert.Equal(1, entry.Content.Rotation.Offset);

        // 起点必须提示核对：宿主机没设置过的话，那只是「导出那天所在周的周日」，照它算会整学期错位。
        Assert.Contains(entry.Warnings, w => w.Contains("轮换起点") && w.Contains("2026-09-06"));
        Assert.Contains(entry.Warnings, w => w.Contains("手动偏移"));
    }

    [Fact]
    public void 没有多周课表时不动档案的轮换设置()
    {
        using var cidata = BuildCidata(
            ("Settings.json", """{ "SingleWeekStartTime": "2026-10-11T00:00:00" }"""),
            ("Profiles/Default.json", RichProfileJson()));

        var entry = CidataImporter.Inspect(cidata).Profiles.Single();

        Assert.Equal(1, entry.Content.Rotation.Weeks);
        Assert.Null(entry.Content.Rotation.SemesterStartDate);
    }

    [Fact]
    public void 设置文件坏掉也不影响课表导入()
    {
        using var cidata = BuildCidata(
            ("Settings.json", "{ 这不是 JSON"),
            ("Profiles/Default.json", WithRotation(div: 2, total: 2)));

        var archive = CidataImporter.Inspect(cidata);
        var entry = archive.Profiles.Single();

        Assert.Null(archive.HostRotation);
        Assert.Single(entry.Content.ClassPlans);
        Assert.Equal(2, entry.Content.Rotation.Weeks);
        Assert.Null(entry.Content.Rotation.SemesterStartDate);
    }

    /// <summary>把夹具改成指定周次轮换 / 触发规则类型。</summary>
    private static string WithRotation(int div, int total, int ruleType = 0) =>
        RichProfileJson()
            .Replace(
                """{ "Type": 0, "WeekDay": 1, "WeekCountDiv": 0 }""",
                $$"""{ "Type": {{ruleType}}, "WeekDay": 1, "WeekCountDiv": {{div}}, "WeekCountDivTotal": {{total}} }""");
}
