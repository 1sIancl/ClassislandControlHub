using System.Net;
using System.Text.Json;
using ControlHub.Shell.Services;

namespace ControlHub.Shell;

/// <summary>
/// 无界面自检（<c>--check</c>）与冒烟验证（<c>--smoke</c>）。
/// <para>
/// 冒烟验证会把「启动 A 端 → 健康检查 → 免登录引导 → 以本地控制台身份读 /admin/me → 停止」跑一遍，
/// 因此它可以放进自动化里验证整条链路，而不需要真的弹出一个窗口。
/// </para>
/// </summary>
internal static class ShellDiagnostics
{
    private const string WebViewRuntimeGlob = @"Microsoft\EdgeWebView\Application";

    /// <summary>环境自检。</summary>
    public static int RunCheck(ShellOptions options)
    {
        Console.WriteLine("=== 外壳环境自检 ===");
        Console.WriteLine($"外壳版本：{typeof(ShellDiagnostics).Assembly.GetName().Version}");
        Console.WriteLine($"日志文件：{ShellLog.Path}");
        Console.WriteLine($"监听端口：{options.Port}");
        Console.WriteLine();

        var problems = 0;

        // A 端位置
        var location = ServerLocator.Locate(options.ServerPath, options.DataDirectory);
        if (location is null)
        {
            Console.WriteLine("[✗] 未找到 A 端程序：请用 --server-path 指定目录（应包含 ControlHub.Server.exe 或 .dll）");
            problems++;
        }
        else
        {
            Console.WriteLine($"[✓] A 端可执行文件：{location.Executable}");
            Console.WriteLine($"    工作目录：{location.WorkingDirectory}");
            Console.WriteLine($"    数据目录：{location.DataDirectory}");
            var tokenPath = Path.Combine(location.DataDirectory, "local-shell.token");
            Console.WriteLine($"    外壳令牌：{(File.Exists(tokenPath) ? "已存在（免登录可用）" : "尚未生成（首次启动 A 端后出现）")}");
        }

        // .NET 运行时
        var runtime = RuntimeLocator.FindDotnetRoot();
        Console.WriteLine(runtime is null
            ? "[✗] 未找到 .NET 运行时：请安装 .NET 10 运行时（A 端需要 Microsoft.AspNetCore.App 10.x）"
            : $"[✓] .NET 运行时：{runtime}");
        if (runtime is null)
        {
            problems++;
        }

        // 端口
        try
        {
            var inUse = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(endpoint => endpoint.Port == options.Port);
            Console.WriteLine(inUse
                ? $"[!] 端口 {options.Port} 已被占用：外壳会直接复用该实例，不再启动新进程"
                : $"[✓] 端口 {options.Port} 空闲");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] 端口检测失败：{ex.Message}");
        }

        // WebView2 运行时（Windows 专有，缺失时内嵌浏览器不可用）
        if (OperatingSystem.IsWindows())
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), WebViewRuntimeGlob);
            var versions = Directory.Exists(root)
                ? Directory.GetDirectories(root).Select(Path.GetFileName).Where(v => v is not null).ToList()
                : [];
            Console.WriteLine(versions.Count > 0
                ? $"[✓] WebView2 运行时：{string.Join(", ", versions)}"
                : "[✗] 未检测到 WebView2 运行时：请安装 Microsoft Edge WebView2 Runtime（多数 Windows 10/11 已内置）");
            if (versions.Count == 0)
            {
                problems++;
            }
        }
        else
        {
            Console.WriteLine("[i] 非 Windows 平台：Linux/macOS 需要系统 WebView 组件（WebKitGTK / WKWebView）");
        }

        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "自检通过。" : $"自检发现 {problems} 个问题，请按上面的提示处理。");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>冒烟验证：无界面跑一遍完整链路。</summary>
    public static async Task<int> RunSmokeAsync(ShellOptions options)
    {
        Console.WriteLine("=== 外壳冒烟验证（不弹窗口）===");
        var lifecycle = new ServerLifecycleService(options);

        Console.WriteLine("1) 启动 A 端…");
        var started = await lifecycle.StartAsync();
        if (!started)
        {
            Console.WriteLine($"   [✗] 失败：{lifecycle.Error}");
            return 1;
        }

        Console.WriteLine($"   [✓] 状态={lifecycle.State}  版本={lifecycle.Version}  地址={lifecycle.BaseUrl}");
        Console.WriteLine($"   外壳令牌：{(string.IsNullOrEmpty(lifecycle.LocalShellToken) ? "未取得（将退回登录页）" : "已取得")}");

        // 用同一个 Cookie 容器模拟 WebView：引导 → 后续请求自动带 Cookie。
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        Console.WriteLine("2) 免登录引导（GET /api/v1/local-shell?shellToken=…）");
        var bootstrap = await client.GetAsync(lifecycle.EntryUrl);
        Console.WriteLine($"   HTTP {(int)bootstrap.StatusCode}  重定向到 {bootstrap.Headers.Location}");

        Console.WriteLine("3) 以本地控制台身份读取 /admin/me");
        var me = await client.GetAsync($"{lifecycle.BaseUrl}/api/v1/admin/me");
        var body = await me.Content.ReadAsStringAsync();
        Console.WriteLine($"   HTTP {(int)me.StatusCode}  {Summarize(body)}");

        Console.WriteLine("4) 对照：不带 Cookie 访问 /admin/me（应被拒）");
        using var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var denied = await anonymous.GetAsync($"{lifecycle.BaseUrl}/api/v1/admin/me");
        Console.WriteLine($"   HTTP {(int)denied.StatusCode}（401 表示本地信任没有泄漏给其它请求）");

        Console.WriteLine("5) 健康探针 /api/health");
        var health = await anonymous.GetAsync($"{lifecycle.BaseUrl}/api/health");
        Console.WriteLine($"   HTTP {(int)health.StatusCode}  {Summarize(await health.Content.ReadAsStringAsync())}");

        Console.WriteLine("6) 停止 A 端…");
        await lifecycle.StopAsync();
        Console.WriteLine($"   状态={lifecycle.State}");

        var passed = bootstrap.StatusCode == HttpStatusCode.Redirect
                     && me.IsSuccessStatusCode
                     && denied.StatusCode == HttpStatusCode.Unauthorized
                     && ((int)health.StatusCode == 200);

        Console.WriteLine();
        Console.WriteLine(passed ? "冒烟验证通过。" : "冒烟验证未通过，请查看上面的输出与日志文件。");
        return passed ? 0 : 1;
    }

    private static string Summarize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var parts = new List<string>();
                foreach (var property in data.EnumerateObject().Take(6))
                {
                    var value = property.Value.ValueKind == JsonValueKind.Array
                        ? $"[{property.Value.GetArrayLength()} 项]"
                        : property.Value.ToString();
                    parts.Add($"{property.Name}={value}");
                }

                return string.Join(" ", parts);
            }

            return json.Length > 160 ? json[..160] + "…" : json;
        }
        catch
        {
            return json.Length > 160 ? json[..160] + "…" : json;
        }
    }
}
