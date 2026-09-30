using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Shared;
using ControlHub.Protocol;

namespace ControlHub.Plugin.Services;

/// <summary>
/// 远程诊断采集器：抓取教室端画面、前台进程/窗口快照与诊断数据包。
/// <para>采集结果只通过命令回报或诊断上传通道回传 A 端，不在教室端留档。</para>
/// </summary>
public static class DiagnosticsCollector
{
    /// <summary>诊断数据包里携带的日志条数上限。</summary>
    private const int MaxLogLines = 60;

    /// <summary>进程快照里列出的进程数上限。</summary>
    private const int MaxProcesses = 20;

    /// <summary>
    /// 抓取 ClassIsland 主窗口画面（PNG 字节）。
    /// <para>
    /// 用 Avalonia 的渲染管线把窗口视觉树画进位图，而不是走 GDI 抓屏：教室端 ClassIsland 通常全屏显示，
    /// 窗口画面就是学生看到的内容；而 GDI 抓取硬件加速窗口时很容易得到全黑图。
    /// </para>
    /// </summary>
    /// <param name="note">采集说明（分辨率、失败原因等），无论成功与否都会填写。</param>
    public static byte[]? CaptureScreen(out string note)
    {
        note = string.Empty;
        try
        {
            var window = ResolveWindow(out var resolveNote);
            if (window is null)
            {
                note = resolveNote;
                return null;
            }

            var size = new PixelSize(
                Math.Max(1, (int)Math.Round(window.Bounds.Width)),
                Math.Max(1, (int)Math.Round(window.Bounds.Height)));

            // 渲染必须回到 UI 线程执行；从后台线程调用会在这里同步等待。
            var png = Dispatcher.UIThread.Invoke(() =>
            {
                var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
                try
                {
                    bitmap.Render(window);
                    using var stream = new MemoryStream();
                    // Avalonia 12 的新重载需要 BitmapEncoderOptions，这里沿用默认 PNG 编码的兼容重载。
#pragma warning disable CS0618
                    bitmap.Save(stream);
#pragma warning restore CS0618
                    return stream.ToArray();
                }
                finally
                {
                    bitmap.Dispose();
                }
            });

            note = $"{size.Width}×{size.Height}，{png.Length / 1024} KB；{DescribeForeground()}";
            return png;
        }
        catch (Exception ex)
        {
            note = $"截图失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>采集前台窗口与进程快照（纯文本，直接放进命令回报的 Output）。</summary>
    public static string CollectProcesses()
    {
        var text = new StringBuilder();
        text.AppendLine($"采集时间（UTC）：{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"系统运行时长：{Format(TimeSpan.FromMilliseconds(Environment.TickCount64))}");
        text.AppendLine($"前台窗口：{DescribeForeground()}");
        text.AppendLine();
        text.AppendLine($"进程（按内存占用取前 {MaxProcesses} 个）：");
        text.AppendLine($"{"进程",-28}{"PID",8}{"内存(MB)",12}  启动时间");

        var rows = new List<(string Name, int Pid, long Memory, string Started)>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var started = "—";
                try
                {
                    started = process.StartTime.ToString("MM-dd HH:mm:ss");
                }
                catch
                {
                    // 受保护的系统进程取不到启动时间，留空即可。
                }

                rows.Add((process.ProcessName, process.Id, process.WorkingSet64, started));
            }
            catch
            {
                // 进程可能刚好退出或无权读取，跳过。
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach (var (name, pid, memory, started) in rows.OrderByDescending(r => r.Memory).Take(MaxProcesses))
        {
            text.AppendLine($"{Trim(name, 28),-28}{pid,8}{memory / 1024 / 1024,12}  {started}");
        }

        text.AppendLine();
        text.AppendLine($"合计进程数：{rows.Count}");
        return text.ToString();
    }

    /// <summary>采集诊断数据包：系统环境、版本、集控运行状态、宿主诊断信息与最近日志。</summary>
    public static string CollectBundle(HubState state)
    {
        var text = new StringBuilder();
        text.AppendLine("=== 集控客户端诊断数据包 ===");
        text.AppendLine($"采集时间（UTC）：{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"插件版本：{PluginVersion()}");
        text.AppendLine($"系统：{RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）");
        text.AppendLine($"设备名：{Environment.MachineName}    当前用户：{Environment.UserName}");
        text.AppendLine($"运行时：.NET {Environment.Version}    进程内存：{Environment.WorkingSet / 1024 / 1024} MB");
        text.AppendLine($"系统运行时长：{Format(TimeSpan.FromMilliseconds(Environment.TickCount64))}");
        text.AppendLine();

        text.AppendLine("── 集控连接状态 ──");
        text.AppendLine($"状态：{state.Status}");
        text.AppendLine($"服务器：{state.ServerName}（{state.ServerUrl}）");
        text.AppendLine($"档案：{state.ProfileName}    配置版本：{state.Revision}");
        text.AppendLine($"最近同步：{FormatTime(state.LastSyncAt)}");
        text.AppendLine($"最近对时：{FormatTime(state.LastTimeSyncAt)}    {state.LastTimeSyncMessage}");
        text.AppendLine($"当前课表：{state.CurrentClassPlanName}");
        text.AppendLine($"最近错误：{(string.IsNullOrWhiteSpace(state.LastError) ? "无" : state.LastError)}");
        text.AppendLine();

        text.AppendLine("── ClassIsland 诊断信息 ──");
        text.AppendLine(TryGetHostDiagnosticInfo() ?? "（宿主未提供诊断服务，已跳过）");
        text.AppendLine();

        text.AppendLine($"── 最近插件日志（最多 {MaxLogLines} 条）──");
        var logs = SnapshotLogs(state);
        if (logs.Count == 0)
        {
            text.AppendLine("（暂无日志）");
        }
        else
        {
            foreach (var line in logs)
            {
                text.AppendLine(line.ToString());
            }
        }

        return text.ToString();
    }

    /// <summary>取一份本地日志快照，用于随诊断数据包一起上报。</summary>
    public static List<LogLine> SnapshotLogs(HubState state)
    {
        try
        {
            return state.Logs.Reverse().Take(MaxLogLines).Reverse().ToList();
        }
        catch
        {
            // 日志集合可能正被其它线程写入，取不到就当作没有日志。
            return [];
        }
    }

    // ────────────────────────────── 内部实现 ──────────────────────────────

    /// <summary>定位要抓取的窗口：优先 ClassIsland 主窗口，其次当前激活的窗口。</summary>
    private static Window? ResolveWindow(out string note)
    {
        note = string.Empty;
        try
        {
            var app = Application.Current as AppBase;
            if (app?.MainWindow is { IsVisible: true } main)
            {
                return main;
            }

            var active = app?.DesktopLifetime?.Windows
                .FirstOrDefault(w => w is { IsActive: true, IsVisible: true });
            if (active is not null)
            {
                return active;
            }

            note = "未取到可截图的窗口（ClassIsland 可能运行在托盘模式）。";
            return null;
        }
        catch (Exception ex)
        {
            note = $"无法获取窗口：{ex.Message}";
            return null;
        }
    }

    /// <summary>描述当前前台窗口（标题 + 进程 + PID），用于人工核对教室端正在显示什么。</summary>
    private static string DescribeForeground()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "（仅 Windows 支持前台窗口采集）";
        }

        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
            {
                return "（未取到前台窗口）";
            }

            GetWindowThreadProcessId(handle, out var pid);
            var title = new StringBuilder(512);
            GetWindowTextW(handle, title, title.Capacity);

            var name = "未知进程";
            if (pid > 0)
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    name = process.ProcessName;
                }
                catch
                {
                    // 进程可能刚好退出。
                }
            }

            var windowTitle = title.Length > 0 ? title.ToString() : "（无标题窗口）";
            return $"{windowTitle}（{name}，PID {pid}）";
        }
        catch (Exception ex)
        {
            return $"（前台窗口采集失败：{ex.Message}）";
        }
    }

    /// <summary>
    /// 读取宿主 ClassIsland 的「导出诊断数据」内容。
    /// <para>用反射调用 <c>ClassIsland.Services.DiagnosticService.GetDiagnosticInfo()</c>：
    /// 宿主未提供该服务（版本差异）时返回 <c>null</c>，不影响其余诊断内容。</para>
    /// </summary>
    private static string? TryGetHostDiagnosticInfo()
    {
        try
        {
            var type = Type.GetType("ClassIsland.Services.DiagnosticService, ClassIsland");
            var service = type is null ? null : IAppHost.Host?.Services.GetService(type);
            var method = type?.GetMethod("GetDiagnosticInfo", BindingFlags.Public | BindingFlags.Instance);
            return service is null || method is null ? null : method.Invoke(service, null) as string;
        }
        catch (Exception ex)
        {
            return $"（读取宿主诊断信息失败：{ex.Message}）";
        }
    }

    /// <summary>插件自身版本（取程序集版本，缺失时退回协议版本）。</summary>
    private static string PluginVersion()
    {
        var version = typeof(DiagnosticsCollector).Assembly.GetName().Version;
        return version is null ? HubProtocol.ProductVersion : version.ToString();
    }

    private static string Format(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays} 天 {span.Hours} 小时" : $"{span.Hours} 小时 {span.Minutes} 分";

    private static string FormatTime(DateTimeOffset? time) =>
        time.HasValue ? time.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—";

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
