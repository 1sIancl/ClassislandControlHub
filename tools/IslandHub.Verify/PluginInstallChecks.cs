using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace IslandHub.Verify;

/// <summary>
/// B 端（教室端）安装检查：宿主 <c>Plugins</c> 目录里那份插件**能不能真的被加载起来**。
///
/// <para>
/// 为什么单独验：A 端所有测试都跑在「插件已经正常工作」的前提下，而教室里最常见的故障其实是
/// **装上了但没起来**——少一个依赖 dll、清单里的 <c>entranceAssembly</c> 写错、插件包里混进了
/// 改名前的旧程序集（两个同名类型各加载一份，宿主挑到旧的那份）。这些在 A 端一点症状都没有。
/// </para>
///
/// <para>
/// 做法：用**隔离的加载上下文**从两个目录解析依赖（插件目录 → 宿主目录），加载入口程序集并
/// 枚举全部类型（枚举会强制解析基类与接口，缺依赖立刻暴露）。找不到安装目录时跳过——
/// CI 上没有教室端，不能因此变红。
/// </para>
/// </summary>
internal static class PluginInstallChecks
{
    private static string HostDirectory =>
        Environment.GetEnvironmentVariable("HOST_PUBLISH_DIR")
        ?? @"E:\classisland集控\ClassIsland2_publish";

    /// <summary>插件安装目录（<c>Plugins</c> 下的那个文件夹）；可用 <c>PLUGIN_INSTALL_DIR</c> 覆盖。</summary>
    private static string PluginDirectory =>
        Environment.GetEnvironmentVariable("PLUGIN_INSTALL_DIR")
        ?? Path.Combine(HostDirectory, "Plugins", "ControlHub.Plugin");

    public static void Run(CheckSink check)
    {
        Console.WriteLine();
        Console.WriteLine("教室端安装（B 端 Plugins 目录里的那份插件）");

        var manifestPath = Path.Combine(PluginDirectory, "manifest.yml");
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine($"  跳过：没找到已安装的插件（{manifestPath}）。"
                + "把 PLUGIN_INSTALL_DIR 指到教室端的插件目录即可启用这一段。");
            return;
        }

        var manifest = File.ReadAllText(manifestPath);
        var entrance = Regex.Match(manifest, @"entranceAssembly:\s*""?([^""\r\n]+)""?").Groups[1].Value.Trim();
        var manifestVersion = Regex.Match(manifest, @"(?m)^version:\s*""?([^""\r\n]+)""?").Groups[1].Value.Trim();

        check("清单里有 entranceAssembly（宿主靠它找入口程序集）", entrance.Length > 0, entrance);

        var dll = Path.Combine(PluginDirectory, entrance);
        check("清单指向的入口程序集确实在安装目录里", File.Exists(dll), entrance);
        if (!File.Exists(dll))
        {
            return;
        }

        AssemblyLoadContext context;
        Assembly assembly;
        try
        {
            context = new PluginLoadContext(PluginDirectory, HostDirectory);
            assembly = context.LoadFromAssemblyPath(dll);
        }
        catch (Exception ex)
        {
            check("插件程序集能被加载", false, $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        check("插件程序集能被加载", true, assembly.GetName().Name);

        // 枚举全部类型 = 强制解析所有基类与接口引用：缺一个依赖 dll 这里就会抛。
        Type[] types;
        string? loadError = null;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
            loadError = ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message;
        }
        catch (Exception ex)
        {
            types = [];
            loadError = $"{ex.GetType().Name}: {ex.Message}";
        }

        check("插件里全部类型都能枚举（说明依赖闭包完整）", loadError is null,
            loadError ?? $"{types.Length} 个类型");

        // 宿主靠 PluginBase / IPlugin 认插件；基类链或接口名任一命中即可（不写死命名空间）。
        var entry = types.FirstOrDefault(t => t.IsClass && !t.IsAbstract
            && (ImplementsPlugin(t) || HasPluginBase(t)));
        check("找得到插件入口类（PluginBase / IPlugin 的实现）",
            entry is not null, entry?.FullName ?? "(没找到)");

        var assemblyVersion = assembly.GetName().Version?.ToString();
        check("程序集版本与清单 version 一致",
            string.Equals(assemblyVersion, manifestVersion, StringComparison.OrdinalIgnoreCase),
            $"dll={assemblyVersion} manifest={manifestVersion}");

        // 旧名残留：改名前的程序集留在安装目录里，宿主可能挑到旧的那份类型。
        var stale = new[] { "ControlHub.Plugin.dll", "ControlHub.Protocol.dll" }
            .Where(name => File.Exists(Path.Combine(PluginDirectory, name)))
            .ToList();
        check("安装目录里没有改名前的旧程序集", stale.Count == 0, string.Join(",", stale));

        // 逐个解析引用的非框架程序集（框架程序集由默认上下文提供，不必在这里找）。
        var missing = new List<string>();
        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            if (reference.Name is null || IsFrameworkAssembly(reference.Name))
            {
                continue;
            }

            try
            {
                context.LoadFromAssemblyName(reference);
            }
            catch
            {
                missing.Add(reference.Name);
            }
        }

        check("插件引用的非框架程序集都能解析（插件目录或宿主目录里有）",
            missing.Count == 0, string.Join(",", missing));
    }

    private static bool ImplementsPlugin(Type type) =>
        type.GetInterfaces().Any(i => (i.Name == "IPlugin" || i.Name == "IPluginBase"));

    private static bool HasPluginBase(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name is "PluginBase" or "PluginBase<T>")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>框架自带的程序集（由默认上下文负责，不该在插件/宿主目录里找）。</summary>
    private static bool IsFrameworkAssembly(string name) =>
        name.StartsWith("System", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal)
        || name is "netstandard" or "mscorlib" or "WindowsBase" or "PresentationCore" or "PresentationFramework";

    /// <summary>先插件目录、后宿主目录的加载上下文（找不到就交回默认上下文）。</summary>
    private sealed class PluginLoadContext(string pluginDirectory, string hostDirectory)
        : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null)
            {
                return null;
            }

            foreach (var directory in new[] { pluginDirectory, hostDirectory })
            {
                var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return LoadFromAssemblyPath(candidate);
                }
            }

            return null;
        }
    }
}
