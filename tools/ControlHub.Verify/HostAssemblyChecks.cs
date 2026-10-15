using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using ControlHub.Plugin.Services;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Verify;

/// <summary>
/// 用**真实发行版的宿主程序集**验证「反射写宿主设置」这条契约。
///
/// <para>
/// 为什么值得单独跑：插件那边给宿主写设置全走反射（CI 用的是发行包参考程序集，直写属性会 CS1061），
/// 反射写错了**不会编译报错**，只会在真机上静默什么也没改——而这一段的属性名 / 类型是从源码抄的，
/// 一旦发行版与源码树对不上（改过名、换了类型），这里会立刻失败。
/// </para>
///
/// <para>
/// 不需要开 GUI：直接加载已发布的 <c>ClassIsland.dll</c>、构造一个 <c>Settings</c>，
/// 再用与真机完全相同的代码路径写一遍、读回来核对。
/// 找不到宿主目录时**跳过**（CI 上没有宿主，不能因此变红），目录可用 <c>HOST_PUBLISH_DIR</c> 覆盖。
/// </para>
/// </summary>
internal static class HostAssemblyChecks
{
    private static string HostDirectory =>
        Environment.GetEnvironmentVariable("HOST_PUBLISH_DIR")
        ?? @"E:\classisland集控\ClassIsland2_publish";

    public static void Run(CheckSink check)
    {
        Console.WriteLine();
        Console.WriteLine("宿主发行版程序集（反射写设置的契约）");

        var dll = Path.Combine(HostDirectory, "ClassIsland.dll");
        if (!File.Exists(dll))
        {
            Console.WriteLine($"  跳过：没找到已发布的宿主程序集（{dll}）。"
                + "把 HOST_PUBLISH_DIR 指到 ClassIsland 的发布目录即可启用这一段。");
            return;
        }

        // 必须在**隔离的加载上下文**里加载宿主程序集：验证台自己的 bin 里有一份参考用的
        // `ClassIsland.Core`（同版本号、但成员比发行版少），而「同标识的程序集只能加载一次」，
        // 直接 LoadFrom 会命中那份旧的，报
        // `Could not load type 'MainWindowBackgroundMaterialType' from assembly 'ClassIsland.Core'`。
        // 隔离上下文里全都从宿主目录解析，拿到的才是真机上的那一套类型。
        object settings;
        Type type;
        AssemblyLoadContext context;
        try
        {
            context = new HostLoadContext(HostDirectory);
            var assembly = context.LoadFromAssemblyPath(dll);
            type = assembly.GetType("ClassIsland.Models.Settings", throwOnError: true)!;
            settings = Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  跳过：加载 / 构造宿主 Settings 失败（{ex.GetType().Name}: {ex.Message}）。");
            return;
        }

        _ = context;

        // ① 三个属性的名字与类型必须与发行版一致（反射写错就写不进任何东西）。
        var startProp = type.GetProperty("SingleWeekStartTime");
        var offsetProp = type.GetProperty("MultiWeekRotationOffset");
        var maxProp = type.GetProperty("MultiWeekRotationMaxCycle");
        check("发行版 Settings 里三个轮换属性的名字与类型都与插件反射用的一致",
            startProp?.PropertyType == typeof(DateTime)
            && maxProp?.PropertyType == typeof(int)
            && offsetProp?.GetValue(settings) is IList<int>,
            $"start={startProp?.PropertyType.Name} offset={offsetProp?.GetValue(settings)?.GetType().Name} max={maxProp?.PropertyType.Name}");

        // ② 走真机同一条代码路径写一遍，再读回来核对。
        var nineWeek = new ClassPlanDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "九周轮换",
            TimeLayoutId = Guid.NewGuid().ToString(),
            DaysOfWeek = [1],
            WeekInterval = 9,
            WeekOffset = 0,
        };

        var result = HostRotationSettings.ApplyTo(
            settings,
            new RotationSettingsDto { Weeks = 9, SemesterStartDate = "2026-09-06", Offset = 1 },
            [nineWeek]);

        var writtenStart = (DateTime)startProp!.GetValue(settings)!;
        var writtenMax = (int)maxProp!.GetValue(settings)!;
        var writtenOffsets = (offsetProp!.GetValue(settings) as IList<int>)!;

        check("在真实宿主 Settings 上写入成功（起点 / 最大周期 / 手动偏移都落进去）",
            result.Changed
            && writtenStart.Date == new DateTime(2026, 9, 6)
            && writtenMax == 9
            && writtenOffsets.Count > 9 && writtenOffsets[9] == 1,
            $"{result.Note}｜start={writtenStart:yyyy-MM-dd} max={writtenMax} offsets={string.Join(",", writtenOffsets)}");

        check("补位的档位是 0（发行版对超出长度的档位取 GetValueOrDefault = 0，填 -1 会偏移 -1 周）",
            writtenOffsets.Skip(5).Take(4).All(v => v == 0),
            string.Join(",", writtenOffsets));

        // ③ 写一次：同样的设置再写一遍应当零改动（避免与宿主自己保存 Settings.json 撞车）。
        var again = HostRotationSettings.ApplyTo(
            settings,
            new RotationSettingsDto { Weeks = 9, SemesterStartDate = "2026-09-06", Offset = 1 },
            [nineWeek]);
        check("在真实宿主 Settings 上重复下发为零改动（写一次原则）",
            !again.Changed, $"changed={again.Changed} note=「{again.Note}」");
    }

    /// <summary>只从宿主发布目录解析依赖的加载上下文（不落到验证台自己那份参考程序集上）。</summary>
    private sealed class HostLoadContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null)
            {
                return null;
            }

            var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
