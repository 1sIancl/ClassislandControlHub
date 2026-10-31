using System.Collections;
using System.Diagnostics;
using System.Globalization;
// 宿主设置项用反射读写（不绑死宿主的 Settings 类型，宿主升级后不会加载失败）。
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Services;
using ClassIsland.Shared;
using IslandHub.Plugin.Models;
using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using Microsoft.Extensions.Logging;

namespace IslandHub.Plugin.Services;

/// <summary>
/// 远程指令执行器：执行 A 端下发的指令并返回结果。
/// <para>支持：shell（命令行）、notify（提醒）、plugin.refresh（上报插件列表）、
/// plugin.toggle/uninstall（插件启停/卸载）、appearance.apply（应用主题/强调色）、restart（重启）。</para>
/// <para><b>载荷解析必须走 <see cref="HubJson"/>。</b>A 端用 camelCase 序列化 payload，
/// 而 <c>JsonSerializer</c> 默认区分大小写，直接用默认选项会让 payload 内层字段全部为 null
/// （表现为：命令被当成整段 JSON 执行、提醒内容为空、外观配置全空、插件 ID 缺失）。</para>
/// </summary>
public sealed class RemoteCommandExecutor(
    ILogger<RemoteCommandExecutor> logger,
    HubState state,
    HubSettingsStore settings,
    LessonBridge lessonBridge)
{
    /// <summary>单条指令回报的输出上限，避免超大输出占满上行链路。</summary>
    private const int MaxOutputLength = 24 * 1024;

    /// <summary>Ping 默认次数（#107）：4 次够看出抖动与丢包，又不会让管理员等太久。</summary>
    private const int DefaultPingCount = 4;

    /// <summary>Ping 次数上限：再多也只是拖时间（一次 1 秒超时）。</summary>
    private const int MaxPingCount = 10;

    /// <summary>路由追踪的最大跃点数（与 Windows 默认的 30 相比收窄，避免跨网段排查时等太久）。</summary>
    private const int MaxTraceHops = 16;

    /// <summary>探测类输出在报告里保留的长度（原文用处是「看路径 / 看丢在哪一跳」）。</summary>
    private const int MaxProbeOutputLength = 4000;

    /// <summary>ClassIsland 用插件目录下的这个文件标记「已禁用」。</summary>
    private const string DisabledMarker = ".disabled";

    /// <summary>插件上报回调（由插件入口注入，负责把插件列表回传 A 端）。</summary>
    public Func<List<PluginInfoDto>, Task>? OnPluginsReported { get; set; }

    /// <summary>
    /// 插件包下载回调（由插件入口注入，用设备令牌从 A 端的代理缓存取包，#121）。
    /// <para>与其它上报回调不同，这里的异常**不能吞**：取不到包就没法安装，必须如实回报。</para>
    /// </summary>
    public Func<string, CancellationToken, Task<byte[]>>? OnDownloadPackage { get; set; }

    /// <summary>课表目录上报回调（由插件入口注入，负责把课表目录回传 A 端）。</summary>
    public Func<LessonCatalogDto, Task>? OnLessonCatalogRead { get; set; }

    /// <summary>诊断工件上传回调（截图等二进制内容经此回传 A 端）。</summary>
    public Func<DiagnosticUploadRequest, Task>? OnDiagnosticUploaded { get; set; }

    /// <summary>日志上报回调（采集诊断数据包时顺带把本地日志推给 A 端，供设备日志页查看）。</summary>
    public Func<List<LogEntryDto>, Task>? OnLogsRequested { get; set; }

    /// <summary>
    /// 通知回执上报回调（#7）。参数为（指令 ID, 阶段），阶段是 <c>shown</c> / <c>closed</c>。
    /// <para>与指令回报是**两件事**：指令 done 表示「插件调用了展示」，
    /// 而回执表示「通知真的开始显示 / 真的结束了」。后者才是管理员要的到达率。</para>
    /// </summary>
    public Func<string, string, Task>? OnNotificationReceipt { get; set; }

    /// <summary>执行一条指令。</summary>
    public async Task<CommandReportRequest> ExecuteAsync(RemoteCommandDto command)
    {
        logger.LogInformation("执行远程指令 {Kind}（{Id}）。", command.Kind, command.Id);
        try
        {
            // 注意：带界面的指令必须经 UiThread 切到 UI 线程——本方法运行在同步循环的后台线程上，
            // 直接操作外观 / 提醒 / 截图 / 插件启停会抛 Avalonia 的 VerifyAccess 异常。
            // shell 与电源类刻意**不**切线程：它们是耗时操作，放 UI 线程会把教室机的界面卡住。
            return command.Kind switch
            {
                RemoteCommandKinds.Shell => await RunShellAsync(command),
                RemoteCommandKinds.Notify => UiThread.Run(() => RunNotify(command)),
                // ↑ RunNotify 改成实例方法了（要触发回执回调），见下方实现。
                RemoteCommandKinds.PluginRefresh => await ReportPluginsAsync(command),
                // 插件安装（#121）：下载 + 校验 + 落盘，都是普通 IO，刻意不切 UI 线程。
                RemoteCommandKinds.PluginInstall => await RunInstallPluginAsync(command),
                RemoteCommandKinds.PluginToggle => UiThread.Run(() => TogglePlugin(command)),
                RemoteCommandKinds.PluginUninstall => UiThread.Run(() => UninstallPlugin(command)),
                RemoteCommandKinds.AppearanceApply => UiThread.Run(() => ApplyAppearance(command)),
                RemoteCommandKinds.Restart => UiThread.Run(() => Restart(command)),
                RemoteCommandKinds.PowerShutdown => RunPowerCommand(command, "/s", "正在关机。"),
                RemoteCommandKinds.PowerRestart => RunPowerCommand(command, "/r", "正在重启计算机。"),
                RemoteCommandKinds.PowerSleep => Sleep(command),
                RemoteCommandKinds.AutomationList => UiThread.Run(() => ReportAutomations(command)),
                RemoteCommandKinds.AutomationTrigger => UiThread.Run(() => TriggerAutomation(command)),
                RemoteCommandKinds.DiagnosticScreenshot => await UiThread.RunAsync(() => CaptureScreenshotAsync(command)),
                RemoteCommandKinds.DiagnosticProcesses => Ok(command, DiagnosticsCollector.CollectProcesses()),
                RemoteCommandKinds.DiagnosticBundle => await CollectDiagnosticBundleAsync(command),
                // 课表类指令：LessonBridge 自己会切 UI 线程（宿主档案被课表控件订阅），
                // 所以这里**不再**套一层 UiThread.Run——嵌套没有意义，还会让日志更难读。
                RemoteCommandKinds.LessonSwap => RunLessonSwap(command),
                RemoteCommandKinds.LessonAdjust => RunLessonAdjust(command),
                RemoteCommandKinds.LessonCatalog => await ReportLessonCatalogAsync(command),
                // 音量 / 亮度：都是**不涉及界面**的系统调用，刻意留在后台线程——
                // COM 与显示器接口都可能阻塞几百毫秒，放 UI 线程会把教室大屏卡住。
                RemoteCommandKinds.SetVolume => RunSetVolume(command),
                RemoteCommandKinds.SetBrightness => RunSetBrightness(command),
                // 主界面显隐 / 全屏（#57）：改的是窗口与宿主设置，必须回 UI 线程。
                RemoteCommandKinds.SetWindow => UiThread.Run(() => RunSetWindow(command)),
                // 远程诊断扩展（#107）：ping / 路由追踪 / 磁盘空间，都是普通 IO，不切 UI 线程。
                RemoteCommandKinds.Ping => await RunPingAsync(command),
                RemoteCommandKinds.Traceroute => await RunTracerouteAsync(command),
                RemoteCommandKinds.DiskSpace => RunDiskSpace(command),
                // 宿主设置项：读写都要碰宿主的 Settings 对象，必须回 UI 线程。
                RemoteCommandKinds.SettingsList => UiThread.Run(() => ReportSettings(command)),
                RemoteCommandKinds.SettingsSet => UiThread.Run(() => SetSetting(command)),
                // 配置文件：纯文件 IO，不碰界面，刻意不切 UI 线程。
                RemoteCommandKinds.ConfigList => ReportConfigFiles(command),
                RemoteCommandKinds.ConfigRead => ReadConfigFile(command),
                RemoteCommandKinds.ConfigWrite => WriteConfigFile(command),
                _ => Fail(command, CommandErrorCodes.Unsupported, $"不支持的指令类型：{command.Kind}"),
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "执行远程指令失败：{Kind}", command.Kind);
            return Fail(command, CommandErrorCodes.ExecutionFailed, ex.Message);
        }
    }

    // ────────────────────────────── shell ──────────────────────────────

    private static async Task<CommandReportRequest> RunShellAsync(RemoteCommandDto command)
    {
        var text = ResolveShellText(command, out var error);
        if (text is null)
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, error!);
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            Arguments = isWindows ? $"/c {text}" : $"-c \"{text.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed, "无法启动进程。");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = (stdout + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : "\n" + stderr)).Trim();
        if (output.Length > MaxOutputLength)
        {
            output = output[..MaxOutputLength] + "\n…（输出过长，客户端已截断）";
        }

        return new CommandReportRequest
        {
            CommandId = command.Id,
            Success = process.ExitCode == 0,
            Output = output,
            ExitCode = process.ExitCode,
            Error = process.ExitCode == 0 ? null : stderr,
            // 刻意**不设 ErrorCode**：shell 的「失败」是被调起的那个进程的退出码，
            // 不是教室端自身的问题（例如老师跑 `net use X:` 失败）。管理员要看的是退出码 + stderr，
            // 硬塞一个「教室端执行失败」反而会误导成插件出问题。
        };
    }

    /// <summary>从载荷里取出要执行的命令文本；解析不出来时返回 null 并给出原因。</summary>
    private static string? ResolveShellText(RemoteCommandDto command, out string? error)
    {
        error = null;
        var raw = command.Payload ?? string.Empty;

        ShellPayload? payload = null;
        var isJson = true;
        try
        {
            payload = HubJson.Deserialize<ShellPayload>(raw);
        }
        catch (JsonException)
        {
            isJson = false;
        }

        if (!isJson)
        {
            // 非 JSON 载荷：当作纯命令文本执行（兼容手工调用接口的场景）。
            var literal = raw.Trim();
            if (literal.Length == 0)
            {
                error = "命令为空。";
                return null;
            }

            return literal;
        }

        var text = (payload?.Command ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(payload?.Args))
        {
            text = (text + " " + payload.Args).Trim();
        }

        if (text.Length == 0)
        {
            error = "命令为空：载荷里没有 command 字段。";
            return null;
        }

        return text;
    }

    private sealed class ShellPayload
    {
        public string? Command { get; set; }
        public string? Args { get; set; }
    }

    // ────────────────────────────── notify ──────────────────────────────

    private CommandReportRequest RunNotify(RemoteCommandDto command)
    {
        var req = HubJson.Deserialize<NotifyRequestDto>(command.Payload) ?? new NotifyRequestDto();
        if (string.IsNullOrWhiteSpace(req.Title) && string.IsNullOrWhiteSpace(req.Message))
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, "提醒内容为空。");
        }

        var provider = HubNotificationProviderHolder.Current;
        if (provider is null)
        {
            return Fail(command, CommandErrorCodes.HostApiUnavailable, "提醒提供方尚未就绪，请稍后重试。");
        }

        TimeSpan? duration = req.DurationSeconds > 0 ? TimeSpan.FromSeconds(req.DurationSeconds) : null;
        var request = provider.Show(req.Title, req.Message, req.Speak, duration, req.Emphasize);

        // 回执（#7）：Show 返回即「已交给通知系统」，记为 shown。
        // 这与下面返回的「指令成功」不是同一件事——指令回报说的是「我执行了」，
        // 回执说的是「它开始显示了」，后者是到达率的分子。
        ReportReceipt(command.Id, "shown");

        // 通知结束（超时或被关掉）再补一条 closed。
        // Completed 可能在任意线程触发，所以回调里**不碰 UI**，只发网络请求。
        request.Completed += (_, _) => ReportReceipt(command.Id, "closed");

        // 回报里写清是哪种展示：#22 之后「全屏强调」与「角落提示」是两件事，
        // 管理员在指令原文里就能看出这次到底有没有打断课堂。
        return Ok(command,
            $"提醒已展示（{(req.Emphasize ? "全屏强调" : "角落提示")}，语音播报：{(req.Speak ? "开" : "关")}）");
    }

    /// <summary>
    /// 上报通知回执（#7）。失败只记调试日志、绝不抛出。
    /// <para>刻意 fire-and-forget：回执是**观测数据**，而「通知已经显示在教室大屏上」
    /// 是既成事实。为了上报一条统计把展示流程拖住（或让它报错），是本末倒置。</para>
    /// </summary>
    private void ReportReceipt(string commandId, string stage)
    {
        var handler = OnNotificationReceipt;
        if (handler is null)
        {
            return;
        }

        _ = handler(commandId, stage).ContinueWith(
            t => logger.LogDebug(t.Exception, "通知回执上报失败（{Stage}）。", stage),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // ────────────────────────────── plugins ──────────────────────────────

    private async Task<CommandReportRequest> ReportPluginsAsync(RemoteCommandDto command)
    {
        var plugins = CollectPlugins();
        if (OnPluginsReported is not null)
        {
            await OnPluginsReported(plugins);
        }

        return new CommandReportRequest
        {
            CommandId = command.Id,
            Success = true,
            Output = $"已上报 {plugins.Count} 个插件。",
        };
    }

    /// <summary>
    /// 收集插件信息。遍历插件目录（含被禁用的插件），并用 ClassIsland 已加载的插件信息补全细节。
    /// <para><see cref="PluginInfoDto.Id"/> 用插件目录名，管理端的启停/卸载都以此定位。</para>
    /// </summary>
    public static List<PluginInfoDto> CollectPlugins()
    {
        var loaded = new Dictionary<string, ClassIsland.Core.Models.Plugin.PluginInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in IPluginService.LoadedPlugins)
            {
                loaded[p.Manifest.Id] = p;
            }
        }
        catch
        {
            // 忽略：插件服务未就绪时退化为只读插件目录。
        }

        var result = new List<PluginInfoDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in PluginRoots())
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                var dirName = Path.GetFileName(dir);
                if (!seen.Add(dirName))
                {
                    continue;
                }

                var manifest = ReadManifestSummary(dir);
                var manifestId = string.IsNullOrEmpty(manifest.Id) ? dirName : manifest.Id;
                loaded.TryGetValue(manifestId, out var info);

                result.Add(new PluginInfoDto
                {
                    Id = dirName,
                    Name = info?.Manifest.Name ?? manifest.Name ?? dirName,
                    Version = info?.Manifest.Version.ToString() ?? manifest.Version ?? string.Empty,
                    IsEnabled = !File.Exists(Path.Combine(dir, DisabledMarker)),
                    Author = info?.Manifest.Author ?? manifest.Author ?? string.Empty,
                    Description = info?.Manifest.Description ?? manifest.Description ?? string.Empty,
                });
            }
        }

        // 兜底：已加载但不在扫描到的目录里（例如目录名与插件 ID 不一致）时也要能看到。
        foreach (var (id, info) in loaded)
        {
            if (result.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new PluginInfoDto
            {
                Id = id,
                Name = info.Manifest.Name,
                Version = info.Manifest.Version.ToString(),
                IsEnabled = true,
                Author = info.Manifest.Author,
                Description = info.Manifest.Description,
            });
        }

        return result;
    }

    private static CommandReportRequest TogglePlugin(RemoteCommandDto command)
    {
        var payload = HubJson.Deserialize<PluginTogglePayload>(command.Payload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.PluginId))
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, "缺少插件 ID。");
        }

        var dir = FindPluginDir(payload.PluginId);
        if (dir is null)
        {
            return Fail(command, CommandErrorCodes.TargetNotFound, $"未找到插件目录：{payload.PluginId}");
        }

        var marker = Path.Combine(dir, DisabledMarker);
        if (payload.Enabled)
        {
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
        else if (!File.Exists(marker))
        {
            File.WriteAllText(marker, "disabled by IslandHub");
        }

        return Ok(command,
            $"插件 {Path.GetFileName(dir)} 已{(payload.Enabled ? "启用" : "禁用")}，需重启 ClassIsland 生效。");
    }

    private static CommandReportRequest UninstallPlugin(RemoteCommandDto command)
    {
        var payload = HubJson.Deserialize<PluginTogglePayload>(command.Payload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.PluginId))
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, "缺少插件 ID。");
        }

        var dir = FindPluginDir(payload.PluginId);
        if (dir is null)
        {
            return Fail(command, CommandErrorCodes.TargetNotFound, $"未找到插件目录：{payload.PluginId}");
        }

        if (string.Equals(Path.GetFileName(dir), "IslandHub.Plugin", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(command, CommandErrorCodes.DisabledByPolicy, "不允许卸载 IslandHub 插件本身。");
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed, "卸载失败：" + ex.Message);
        }

        return Ok(command, $"插件 {Path.GetFileName(dir)} 已卸载，需重启 ClassIsland 生效。");
    }

    /// <summary>
    /// 安装插件（#121）：从 A 端的代理缓存取包，校验 SHA256 与包内 manifest 后，
    /// 放进宿主的待安装目录——**重启 ClassIsland 才会真正装上去**（宿主在启动时解压该目录）。
    /// </summary>
    private async Task<CommandReportRequest> RunInstallPluginAsync(RemoteCommandDto command)
    {
        var payload = HubJson.Deserialize<PluginInstallPayload>(command.Payload);
        if (payload is null
            || string.IsNullOrWhiteSpace(payload.PluginId)
            || string.IsNullOrWhiteSpace(payload.CipxUrl))
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, "载荷缺少插件 ID 或下载地址。");
        }

        if (OnDownloadPackage is null)
        {
            return Fail(command, CommandErrorCodes.HostApiUnavailable,
                "当前插件版本没有接入插件包下载通道，请先升级 IslandHub 插件。");
        }

        byte[] package;
        try
        {
            package = await OnDownloadPackage(payload.CipxUrl, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed, "从 IslandHub 服务器取插件包失败：" + ex.Message);
        }

        var outcome = PluginPackageInstaller.Install(package, payload.PluginId.Trim(),
            (payload.Version ?? string.Empty).Trim(), payload.Sha256 ?? string.Empty);

        return outcome.Ok
            ? Ok(command, outcome.Message)
            : Fail(command, outcome.ErrorCode ?? CommandErrorCodes.ExecutionFailed, outcome.Message);
    }

    private static IEnumerable<string> PluginRoots()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { "Plugins", "plugins" })
        {
            var root = Path.Combine(baseDir, candidate);
            if (Directory.Exists(root))
            {
                yield return root;
            }
        }
    }

    /// <summary>按插件 ID（目录名或 manifest 中的 id）定位插件目录。</summary>
    private static string? FindPluginDir(string pluginId)
    {
        foreach (var root in PluginRoots())
        {
            var direct = Path.Combine(root, pluginId);
            if (Directory.Exists(direct))
            {
                return direct;
            }

            foreach (var dir in Directory.GetDirectories(root))
            {
                var manifest = ReadManifestSummary(dir);
                if (string.Equals(manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                {
                    return dir;
                }
            }
        }

        return null;
    }

    /// <summary>读取 manifest.yml 里的基础字段（扁平结构，逐行解析即可，不引入 YAML 依赖）。</summary>
    private static (string? Id, string? Name, string? Version, string? Author, string? Description)
        ReadManifestSummary(string pluginDir)
    {
        var path = Path.Combine(pluginDir, "manifest.yml");
        if (!File.Exists(path))
        {
            return (null, null, null, null, null);
        }

        string? id = null, name = null, version = null, author = null, description = null;
        try
        {
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var idx = line.IndexOf(':');
                if (idx <= 0)
                {
                    continue;
                }

                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim().Trim('"', '\'');
                switch (key)
                {
                    case "id": id = value; break;
                    case "name": name = value; break;
                    case "version": version = value; break;
                    case "author": author = value; break;
                    case "description": description = value; break;
                }
            }
        }
        catch
        {
            // 读不到就当作没有清单，交由调用方兜底。
        }

        return (id, name, version, author, description);
    }

    // ────────────────────────────── appearance ──────────────────────────────

    private static CommandReportRequest ApplyAppearance(RemoteCommandDto command)
    {
        var cfg = HubJson.Deserialize<AppearanceConfigDto>(command.Payload);
        if (cfg is null)
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, "外观配置为空。");
        }

        // 走 ClassIsland 自己的设置服务，而不是 IThemeService.SetTheme：
        // ① SetTheme 只改运行时主题、**不写设置**，宿主随后会用 Settings.Theme / ColorSource / PrimaryColor
        //    覆盖回去（表现为「提示已应用、界面没变化，重启就还原」）；
        // ② 改 Settings.* 会立即生效并自动落盘，与宿主自己的设置页行为一致。
        var settings = IAppHost.TryGetService<SettingsService>();
        if (settings is null)
        {
            return Fail(command, CommandErrorCodes.HostApiUnavailable, "无法获取 ClassIsland 设置服务（IAppHost 未就绪）。");
        }

        var s = settings.Settings;
        var applied = new List<string>();
        var skipped = new List<string>();

        // 主题：ClassIsland 的语义是 0=跟随系统 / 1=浅色 / 2=深色。
        // （IThemeService.CurrentRealThemeMode 的注释写成「0 浅色 1 深色」，与实现不符，这里按实现为准。）
        if (!string.IsNullOrWhiteSpace(cfg.Theme))
        {
            switch (cfg.Theme.Trim().ToLowerInvariant())
            {
                case "system":
                    s.Theme = 0;
                    applied.Add("主题=跟随系统");
                    break;
                case "light":
                    s.Theme = 1;
                    applied.Add("主题=浅色");
                    break;
                case "dark":
                    s.Theme = 2;
                    applied.Add("主题=深色");
                    break;
                default:
                    skipped.Add($"主题取值无法识别（{cfg.Theme}，可用 light / dark / system）");
                    break;
            }
        }

        if (TryParseColor(cfg.AccentColor, out var accent))
        {
            // 取色来源必须切到「自定义」：默认来源是「系统色」，不改它的话强调色会被覆盖掉。
            s.ColorSource = 0;
            s.PrimaryColor = accent;
            applied.Add($"强调色={cfg.AccentColor!.Trim()}");
        }
        else if (!string.IsNullOrWhiteSpace(cfg.AccentColor))
        {
            skipped.Add($"强调色格式不正确（{cfg.AccentColor}，需 #RRGGBB）");
        }

        if (TryParseColor(cfg.SecondaryColor, out var secondary))
        {
            s.ColorSource = 0;
            s.SecondaryColor = secondary;
            applied.Add($"第二色={cfg.SecondaryColor!.Trim()}");
        }
        else if (!string.IsNullOrWhiteSpace(cfg.SecondaryColor))
        {
            skipped.Add($"第二色格式不正确（{cfg.SecondaryColor}，需 #RRGGBB）");
        }

        if (!string.IsNullOrWhiteSpace(cfg.FontFamily))
        {
            s.MainWindowFont = cfg.FontFamily.Trim();
            applied.Add($"字体={s.MainWindowFont}");
        }

        var sizeLabels = new List<string>();
        foreach (var (key, size) in cfg.FontSizes ?? [])
        {
            if (size is <= 0 or > 200)
            {
                skipped.Add($"字号 {key}={size} 超出范围（1~200）");
                continue;
            }

            switch (key.Trim().ToLowerInvariant())
            {
                case "secondary":
                    s.MainWindowSecondaryFontSize = size;
                    sizeLabels.Add($"次级 {size:0.#}");
                    break;
                case "body":
                    s.MainWindowBodyFontSize = size;
                    sizeLabels.Add($"正文 {size:0.#}");
                    break;
                case "emphasized":
                    s.MainWindowEmphasizedFontSize = size;
                    sizeLabels.Add($"强调 {size:0.#}");
                    break;
                case "large":
                    s.MainWindowLargeFontSize = size;
                    sizeLabels.Add($"大号 {size:0.#}");
                    break;
                default:
                    skipped.Add($"未知字号键（{key}，可用 secondary / body / emphasized / large）");
                    break;
            }
        }

        if (sizeLabels.Count > 0)
        {
            applied.Add("字号=" + string.Join("/", sizeLabels));
        }

        if (cfg.Radius is { } radius)
        {
            if (radius is >= 0 and <= 64)
            {
                s.RadiusX = radius;
                s.RadiusY = radius;
                applied.Add($"圆角={radius:0.#}");
            }
            else
            {
                skipped.Add($"圆角 {radius} 超出范围（0~64）");
            }
        }

        if (cfg.Opacity is { } opacity)
        {
            if (opacity is >= 0.1 and <= 1)
            {
                s.Opacity = opacity;
                applied.Add($"背景不透明度={opacity:0.##}");
            }
            else
            {
                skipped.Add($"背景不透明度 {opacity} 超出范围（0.1~1）");
            }
        }

        if (cfg.Scale is { } scale)
        {
            if (scale is >= 0.5 and <= 3)
            {
                s.Scale = scale;
                applied.Add($"界面缩放={scale:0.##}");
            }
            else
            {
                skipped.Add($"界面缩放 {scale} 超出范围（0.5~3）");
            }
        }

        if (!string.IsNullOrWhiteSpace(cfg.BackgroundMaterial))
        {
            switch (cfg.BackgroundMaterial.Trim().ToLowerInvariant())
            {
                case "off":
                    // 与开启分支同样走反射：该开关在部分宿主版本里不存在（CI 已实测报 CS1061）。
                    applied.Add(TrySetSetting(s, "IsMainWindowBackgroundMaterialEnabled", false)
                        ? "背景材质=关闭"
                        : "背景材质=关闭（当前 ClassIsland 版本不支持该设置，未生效）");
                    break;
                case "acrylic":
                    applied.Add(ApplyBackgroundMaterial(settings, 0, "亚克力"));
                    break;
                case "liquidglass":
                    applied.Add(ApplyBackgroundMaterial(settings, 1, "Liquid Glass"));
                    break;
                case "mica":
                    applied.Add(ApplyBackgroundMaterial(settings, 2, "Mica"));
                    break;
                default:
                    skipped.Add($"背景材质取值无法识别（{cfg.BackgroundMaterial}，可用 off / acrylic / liquidglass / mica）");
                    break;
            }
        }

        if (cfg.SeparatedIsland is { } separated)
        {
            s.IsIslandSeperated = separated;
            applied.Add(separated ? "分体主界面=开" : "分体主界面=关");
        }

        if (TryParseColor(cfg.ForegroundColor, out var foreground))
        {
            // 开关同样按反射写：部分宿主版本没有这个设置项（CI 上实测编译失败过一次）。
            var enabled = TrySetSetting(s, "IsCustomForegroundColorEnabled", true);
            s.CustomForegroundColor = foreground;
            applied.Add(enabled
                ? $"前景色={cfg.ForegroundColor!.Trim()}"
                : $"前景色={cfg.ForegroundColor!.Trim()}（当前版本无「启用自定义前景色」开关，已直接设色）");
        }
        else if (!string.IsNullOrWhiteSpace(cfg.ForegroundColor))
        {
            skipped.Add($"前景色格式不正确（{cfg.ForegroundColor}，需 #RRGGBB）");
        }

        if (!string.IsNullOrWhiteSpace(cfg.ComponentProfileJson))
        {
            // 如实说明而不是静默忽略：组件布局是另一套配置（ComponentSettings + CurrentComponentConfig），
            // 目前只保留协议字段，尚未实现下发。
            skipped.Add("组件布局暂未支持（本次未应用）");
        }

        if (applied.Count == 0)
        {
            // 两种原因要分开报码：载荷里根本没东西可应用（管理端配错了）与教室端不支持这些项
            // （插件 / 宿主版本低）——管理员要做的事完全不同，混成一个码等于没说。
            return skipped.Count > 0
                ? Fail(command, CommandErrorCodes.Unsupported, "没有应用任何外观：" + string.Join("；", skipped))
                : Fail(command, CommandErrorCodes.PayloadInvalid, "没有应用任何外观：配置里没有可应用的项。");
        }

        // 属性变更已触发宿主自动保存；这里再显式保存一次作为双保险（幂等）。
        settings.SaveSettings("IslandHub 下发外观");

        // 留档，便于设置页展示「最近一次下发的外观」。
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClassIsland", "IslandHub");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "appearance.json"), HubJson.Serialize(cfg, indented: true));
        }
        catch
        {
            // 留档失败不影响外观本身已经应用。
        }

        var text = "已应用外观：" + string.Join("，", applied) + "。";
        if (skipped.Count > 0)
        {
            text += "　未应用：" + string.Join("；", skipped) + "。";
        }

        return Ok(command, text);
    }

    /// <summary>
    /// 通过反射写入 ClassIsland 的设置项。
    /// <para>**为什么必须这样**：不同版本的 ClassIsland 设置模型并不完全一致——例如
    /// 「背景材质」「自定义前景色开关」在部分宿主版本里不存在，直接写属性名会让插件
    /// 在 CI（用发行包作参考程序集）上编译失败（CS1061，已实测踩到）。
    /// 反射写还有一个好处：老宿主上不会崩，只是如实回报「该版本不支持」。</para>
    /// </summary>
    /// <returns>属性存在且可写时为 true；否则 false。</returns>
    private static bool TrySetSetting(object settings, string propertyName, object value)
    {
        var property = settings.GetType().GetProperty(propertyName);
        if (property is null || !property.CanWrite)
        {
            return false;
        }

        property.SetValue(settings, value);
        return true;
    }

    /// <summary>解析 <c>#RRGGBB</c> / <c>#AARRGGBB</c> 颜色；留空返回 false（表示不修改）。</summary>
    private static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        return !string.IsNullOrWhiteSpace(text) && Color.TryParse(text.Trim(), out color);
    }

    /// <summary>
    /// 启用并设置主界面背景材质。
    /// <para>材质枚举（Acrylic / LiquidGlass / Mica）在不同版本的 ClassIsland SDK 里所在程序集不同，
    /// 直接写类型名会编译失败（CS7069），因此按数值写入：0=亚克力、1=Liquid Glass、2=Mica。
    /// 宿主不支持该设置时**如实写进结果**，而不是假装成功。</para>
    /// </summary>
    private static string ApplyBackgroundMaterial(SettingsService settings, int value, string label)
    {
        var s = settings.Settings;
        if (!TrySetSetting(s, "IsMainWindowBackgroundMaterialEnabled", true))
        {
            return $"背景材质={label}（当前 ClassIsland 版本不支持该设置，未生效）";
        }

        var property = s.GetType().GetProperty("MainWindowBackgroundMaterialType");
        if (property is null)
        {
            return $"背景材质={label}（当前 ClassIsland 版本不支持该设置，未生效）";
        }

        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        property.SetValue(s, Enum.ToObject(type, value));
        return $"背景材质={label}";
    }

    // ────────────────────────────── restart ──────────────────────────────

    private static CommandReportRequest Restart(RemoteCommandDto command)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return Fail(command, CommandErrorCodes.ExecutionFailed, "无法确定可执行文件路径。");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
            });
            Environment.Exit(0);
            return Ok(command, "正在重启 ClassIsland。");
        }
        catch (Exception ex)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed, "重启失败：" + ex.Message);
        }
    }

    // ────────────────────────────── 电源管理 ──────────────────────────────

    /// <summary>关机 / 重启：调用 Windows 自带的 shutdown 命令，立即执行且不弹确认。</summary>
    private static CommandReportRequest RunPowerCommand(RemoteCommandDto command, string mode, string okText)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Fail(command, CommandErrorCodes.PlatformUnsupported, "电源管理仅支持 Windows 终端。");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = $"{mode} /t 0",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            return Ok(command, okText);
        }
        catch (Exception ex)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed, "电源指令执行失败：" + ex.Message);
        }
    }

    /// <summary>睡眠：调用 Windows 的 SetSuspendState（休眠 = false）。</summary>
    private static CommandReportRequest Sleep(RemoteCommandDto command)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Fail(command, CommandErrorCodes.PlatformUnsupported, "电源管理仅支持 Windows 终端。");
        }

        try
        {
            // bHibernate=false 即睡眠；bForce=false 允许应用程序否决（避免强断未保存的板书/文档）。
            var ok = SetSuspendState(false, false, false);
            return ok ? Ok(command, "正在进入睡眠。") : Fail(command, CommandErrorCodes.ExecutionFailed, "睡眠请求被系统拒绝。");
        }
        catch (Exception ex)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed, "睡眠失败：" + ex.Message);
        }
    }

    // ────────────────────── 教室端设备控制：音量 / 亮度（#23 / #24）──────────────────────

    /// <summary>设置系统主音量（#23）。</summary>
    private static CommandReportRequest RunSetVolume(RemoteCommandDto command)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Fail(command, CommandErrorCodes.PlatformUnsupported, "音量控制目前只在 Windows 教室端可用。");
        }

        var payload = HubJson.Deserialize<VolumePayload>(command.Payload) ?? new VolumePayload();
        if (payload.Percent is null && payload.Mute is null)
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid,
                "载荷里既没有 percent 也没有 mute（两个都空等于什么也没要求）。");
        }

        var notes = new List<string>();
        if (payload.Percent is { } percent)
        {
            if (percent is < 0 or > 100)
            {
                return Fail(command, CommandErrorCodes.PayloadInvalid, $"音量应在 0~100 之间，收到 {percent}。");
            }

            if (!AudioVolume.TrySetPercent(percent, out var setError))
            {
                return Fail(command, CommandErrorCodes.ExecutionFailed, "设置音量失败：" + setError);
            }

            notes.Add($"音量 {percent}%");
        }

        if (payload.Mute is { } mute)
        {
            if (!AudioVolume.TrySetMute(mute, out var muteError))
            {
                return Fail(command, CommandErrorCodes.ExecutionFailed, "切换静音失败：" + muteError);
            }

            notes.Add(mute ? "已静音" : "已取消静音");
        }

        // 回读一次再回报：教室机上常有「调完被别的程序改回去」的情况，
        // 只回「已设置」会把这类问题掩盖成成功。
        var readBack = AudioVolume.TryGet(out var current, out var muted, out _)
            ? $"，回读 {current}%{(muted ? "（静音中）" : string.Empty)}"
            : string.Empty;
        return Ok(command, $"已设置{string.Join("、", notes)}{readBack}。");
    }

    /// <summary>设置屏幕亮度（#24）。</summary>
    /// <summary>
    /// 远程设置教室端主界面的显示状态（#57）。
    /// <para><b>显隐</b>走宿主自己的开关 <c>Settings.IsMainWindowVisible</c>——托盘菜单里那项
    /// 「显示 / 隐藏主界面」用的就是它；如果只对窗口 Hide()，宿主设置与窗口状态会对不上，
    /// 下一次刷新又把它弹回来。拿到窗口时再顺手 Show/Hide 一次兜底。</para>
    /// <para><b>全屏</b>是尽力而为：宿主在窗口位置变化 / 档案刷新等时机会把 WindowState 拉回普通，
    /// 插件侧拦不住，所以成功回报里写明这一点，而不是承诺一直保持。</para>
    /// </summary>
    private static CommandReportRequest RunSetWindow(RemoteCommandDto command)
    {
        var payload = HubJson.Deserialize<WindowSetPayload>(command.Payload);
        if (payload is null || (payload.Visible is null && payload.Fullscreen is null))
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid,
                "载荷里既没有 visible 也没有 fullscreen，不知道要改什么。");
        }

        var window = (Application.Current as AppBase)?.MainWindow;
        var applied = new List<string>();

        if (payload.Visible is { } visible)
        {
            var settings = IAppHost.TryGetService<SettingsService>();
            if (settings is null)
            {
                return Fail(command, CommandErrorCodes.HostApiUnavailable,
                    "拿不到宿主的设置服务，无法改主界面显示状态。");
            }

            settings.Settings.IsMainWindowVisible = visible;
            applied.Add(visible ? "显示" : "隐藏");

            // 设置变更由宿主自己应用；窗口已经建好时再兜一下（避免设置生效慢半拍）。
            if (window is not null)
            {
                if (visible)
                {
                    window.Show();
                }
                else
                {
                    window.Hide();
                }
            }
        }

        if (payload.Fullscreen is { } fullscreen)
        {
            if (window is null)
            {
                return Fail(command, CommandErrorCodes.HostApiUnavailable,
                    "宿主的窗口还没创建，切换不了全屏。");
            }

            window.WindowState = fullscreen ? WindowState.FullScreen : WindowState.Normal;
            applied.Add(fullscreen ? "全屏" : "退出全屏");
        }

        var note = payload.Fullscreen is true
            ? "（全屏由宿主窗口状态决定，档案刷新或窗口位置变化可能会退出全屏）"
            : string.Empty;
        return Ok(command, $"主界面已{string.Join("、", applied)}{note}");
    }

    private static CommandReportRequest RunSetBrightness(RemoteCommandDto command)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Fail(command, CommandErrorCodes.PlatformUnsupported, "亮度控制目前只在 Windows 教室端可用。");
        }

        var payload = HubJson.Deserialize<BrightnessPayload>(command.Payload) ?? new BrightnessPayload();
        if (payload.Percent is not { } percent)
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, "载荷里缺少 percent（0~100）。");
        }

        if (percent is < 0 or > 100)
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, $"亮度应在 0~100 之间，收到 {percent}。");
        }

        var result = DisplayBrightness.SetPercent(percent);
        if (result.Total == 0)
        {
            return Fail(command, CommandErrorCodes.PlatformUnsupported,
                "系统没有报告任何显示器，无法调整亮度（这台机器是不是没接显示输出？）。");
        }

        if (result.Adjustable == 0)
        {
            return Fail(command, CommandErrorCodes.PlatformUnsupported,
                $"检测到 {result.Total} 台显示器，但都不支持软件调亮度——外接显示器多为这种情况，"
                + "请用显示器本身的按键或厂商工具调整。");
        }

        if (result.Applied == 0)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed,
                $"亮度接口可用，但设置被系统拒绝（{result.Adjustable} 台可调显示器一台都没改成功）。");
        }

        var partial = result.Applied < result.Adjustable
            ? $"（{result.Total} 台显示器里成功 {result.Applied} 台，其余不支持软件调亮度）"
            : string.Empty;
        return Ok(command, $"已把亮度设为 {percent}%{partial}。");
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    // ────────────────────────────── 自动化 ──────────────────────────────

    /// <summary>
    /// 上报本机可被远程触发的自动化信号（来自各工作流的「信号触发器」）。
    /// <para>全程用反射读取 ClassIsland 的自动化模型：不同版本的自动化类型可能变化，
    /// 反射可以在缺类型时优雅降级，不影响插件的其它功能。</para>
    /// </summary>
    private static CommandReportRequest ReportAutomations(RemoteCommandDto command)
    {
        try
        {
            var service = ResolveAutomationService();
            if (service is null)
            {
                return Fail(command, CommandErrorCodes.HostApiUnavailable, "当前 ClassIsland 未提供自动化服务，无法远程触发自动化。");
            }

            var signals = new List<string>();
            if (service.GetType().GetProperty("Workflows")?.GetValue(service) is IEnumerable workflows)
            {
                foreach (var workflow in workflows)
                {
                    var triggers = workflow?.GetType().GetProperty("Triggers")?.GetValue(workflow) as IEnumerable;
                    if (triggers is null)
                    {
                        continue;
                    }

                    foreach (var trigger in triggers)
                    {
                        var settings = trigger?.GetType().GetProperty("Settings")?.GetValue(trigger);
                        if (settings is null || settings.GetType().Name != "SignalTriggerSettings")
                        {
                            continue;
                        }

                        if (settings.GetType().GetProperty("SignalName")?.GetValue(settings) is string name
                            && !string.IsNullOrWhiteSpace(name)
                            && !signals.Contains(name))
                        {
                            signals.Add(name);
                        }
                    }
                }
            }

            return Ok(command, HubJson.Serialize(new { signals }));
        }
        catch (Exception ex)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed, "读取自动化配置失败：" + ex.Message);
        }
    }

    /// <summary>触发一个自动化信号：等价于 ClassIsland 内部「广播信号」，会点亮所有监听它的工作流。</summary>
    private static CommandReportRequest TriggerAutomation(RemoteCommandDto command)
    {
        var request = HubJson.DeserializeOrDefault(command.Payload, new AutomationTriggerPayload());
        var signal = (request.Signal ?? string.Empty).Trim();
        if (signal.Length == 0)
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, "载荷缺少 signal 字段。");
        }

        var handlerType = Type.GetType(
            "ClassIsland.Services.Automation.Triggers.SignalTriggerHandlerService, ClassIsland");
        var handler = handlerType is null ? null : IAppHost.Host?.Services.GetService(handlerType);
        if (handler is null)
        {
            return Fail(command, CommandErrorCodes.HostApiUnavailable, "当前 ClassIsland 未提供信号触发器服务，无法远程触发自动化。");
        }

        var method = handlerType!.GetMethod("EmitSignal", [typeof(string), typeof(bool)]);
        if (method is null)
        {
            return Fail(command, CommandErrorCodes.HostApiUnavailable, "信号触发器服务的接口与预期不符（EmitSignal 不存在）。");
        }

        method.Invoke(handler, [signal, false]);
        return Ok(command, $"已触发自动化信号「{signal}」。");
    }

    /// <summary>
    /// 列出宿主设置项。
    ///
    /// <para>
    /// 每一项给出：键（宿主 Settings 的属性名）、类型、当前值、能否远程修改。
    /// 复杂对象（主题实例、规则集、组件布局）标成不可写——它们的值跨进程传不过去，
    /// 强行改只会让宿主在下次保存时写出坏配置。
    /// </para>
    /// </summary>
    private static CommandReportRequest ReportSettings(RemoteCommandDto command)
    {
        var service = ResolveSettingsService();
        if (service is null)
        {
            return Fail(command, CommandErrorCodes.HostApiUnavailable,
                "当前 ClassIsland 未提供设置服务，无法读写设置项。");
        }

        var settings = service.GetType().GetProperty("Settings")?.GetValue(service);
        if (settings is null)
        {
            return Fail(command, CommandErrorCodes.HostApiUnavailable,
                "设置服务的接口与预期不符（没有 Settings 属性）。");
        }

        var items = new List<object>();
        foreach (var property in settings.GetType()
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(settings);
            }
            catch
            {
                // 懒加载属性可能抛（比如要读文件），跳过它比整条指令失败好。
                continue;
            }

            var kind = KindOf(property.PropertyType);
            items.Add(new
            {
                key = property.Name,
                type = property.PropertyType.Name,
                kind,
                value = TextOf(value, property.PropertyType),
                writable = kind != "object" && property.CanWrite,
            });
        }

        return Ok(command, HubJson.Serialize(new
        {
            count = items.Count,
            writable = items.Count(i => (bool)i.GetType().GetProperty("writable")!.GetValue(i)!),
            items,
        }));
    }

    /// <summary>
    /// 修改一个宿主设置项。
    ///
    /// <para>
    /// 只接受简单类型。复杂对象直接拒绝并说明原因——塞一个字符串进 <c>object</c> 类型属性，
    /// 宿主下次保存时会写出坏配置，而且看不出是谁弄坏的。
    /// </para>
    /// </summary>
    private static CommandReportRequest SetSetting(RemoteCommandDto command)
    {
        var request = HubJson.DeserializeOrDefault(command.Payload, new SettingsSetPayload());
        var key = (request.Key ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, "载荷缺少 key 字段。");
        }

        var service = ResolveSettingsService();
        var settings = service?.GetType().GetProperty("Settings")?.GetValue(service);
        if (settings is null)
        {
            return Fail(command, CommandErrorCodes.HostApiUnavailable,
                "当前 ClassIsland 未提供设置服务，无法修改设置项。");
        }

        var property = settings.GetType()
            .GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            return Fail(command, CommandErrorCodes.TargetNotFound, $"没有名为「{key}」的设置项。");
        }

        if (!property.CanWrite)
        {
            return Fail(command, CommandErrorCodes.Unsupported, $"设置项「{key}」是只读的。");
        }

        if (KindOf(property.PropertyType) == "object")
        {
            return Fail(command, CommandErrorCodes.Unsupported,
                $"设置项「{key}」是复杂对象（{property.PropertyType.Name}），"
                + "远程只能改开关 / 数字 / 文本 / 选项；这一项请在教室端本机修改。");
        }

        object? converted;
        try
        {
            converted = ParseSettingValue(request.Value ?? string.Empty, property.PropertyType);
        }
        catch (Exception ex)
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid,
                $"值「{request.Value}」无法转换为 {property.PropertyType.Name}：{ex.Message}");
        }

        var before = property.GetValue(settings);
        property.SetValue(settings, converted);

        // 保存到宿主配置文件；不保存的话重启就回到旧值，而且用户会以为"改了但没用"。
        var save = service!.GetType().GetMethod("SaveSettings", [typeof(string)]);
        save?.Invoke(service, ["IslandHub 远程修改"]);

        return Ok(command, $"已保存 {key}：{TextOf(before, property.PropertyType)} → "
            + $"{TextOf(converted, property.PropertyType)}");
    }

    /// <summary>把设置项的值分成四类：开关 / 数字 / 文本 / 选项（枚举），其余算复杂对象。</summary>
    private static string KindOf(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(bool))
        {
            return "bool";
        }

        if (underlying.IsEnum)
        {
            return "choice";
        }

        if (underlying == typeof(string))
        {
            return "text";
        }

        if (underlying == typeof(byte) || underlying == typeof(short) || underlying == typeof(int)
            || underlying == typeof(long) || underlying == typeof(float)
            || underlying == typeof(double) || underlying == typeof(decimal))
        {
            return "number";
        }

        return "object";
    }

    /// <summary>把值转成可传的文本；复杂对象返回 null（不猜它的结构）。</summary>
    private static string? TextOf(object? value, Type type)
    {
        if (value is null)
        {
            return null;
        }

        if (KindOf(type) == "object")
        {
            return null;
        }

        return value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString();
    }

    /// <summary>把文本转成目标类型。只处理四种简单类型，其余直接抛（由调用方转成错误回执）。</summary>
    private static object? ParseSettingValue(string text, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var trimmed = text.Trim();

        if (underlying == typeof(bool))
        {
            // 中文界面里管理员可能真的会填「是 / 否」，一并认。
            return trimmed.ToLowerInvariant() switch
            {
                "true" or "1" or "on" or "yes" or "是" or "开" => true,
                "false" or "0" or "off" or "no" or "否" or "关" => false,
                _ => throw new FormatException("布尔值请填 true / false"),
            };
        }

        if (underlying == typeof(string))
        {
            return text;
        }

        if (underlying.IsEnum)
        {
            // 允许填名字或数字（数字按枚举值解析），两者都对不上才报错。
            return Enum.Parse(underlying, trimmed, ignoreCase: true);
        }

        return Convert.ChangeType(trimmed, underlying, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 允许远程管理的配置目录（相对宿主配置目录）。
    ///
    /// <para>
    /// 这些模块没有跨进程 API——组件是 Avalonia 控件、规则集是宿主内部对象——
    /// 但它们的配置就是磁盘上的 JSON。走文件级管理：读 → 改 → 写回 → 重启生效。
    /// </para>
    ///
    /// <para>
    /// **不在白名单里的**：<c>PluginsIndex</c>（几十 MB 的市场缓存，改了没意义）、
    /// <c>Management/Credentials.json</c>（含凭据，不该经远程通道搬）、
    /// <c>.bak</c>（备份文件）、<c>Tutorial</c> / <c>ReadAnnouncements</c>（引导状态）。
    /// </para>
    /// </summary>
    /// <summary>宿主配置目录（<c>CommonDirectories.AppConfigPath</c>）。</summary>
    private static string? ConfigRoot()
    {
        var type = Type.GetType("ClassIsland.Shared.CommonDirectories, ClassIsland.Shared");
        return type?.GetProperty("AppConfigPath", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null) as string;
    }

    /// <summary>
    /// 把请求里的相对路径解析成真实路径，并校验它落在白名单内。
    ///
    /// <para>
    /// 判定逻辑在 <see cref="ConfigPathPolicy"/>（协议层）——那里能写单测，
    /// 而这类「放开写文件后的唯一信任边界」必须被测到，不能只靠代码审查。
    /// </para>
    /// </summary>
    private static bool TryResolveConfigPath(string? relative, out string fullPath, out string reason)
        => ConfigPathPolicy.TryResolve(ConfigRoot(), relative, out fullPath, out reason);

    /// <summary>列出可管理的配置文件（只列白名单目录下的 .json）。</summary>
    private static CommandReportRequest ReportConfigFiles(RemoteCommandDto command)
    {
        try
        {
            var root = ConfigRoot();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return Fail(command, CommandErrorCodes.HostApiUnavailable,
                    "拿不到宿主配置目录，无法列出可管理的配置。");
            }

            var files = new List<object>();
            foreach (var name in ConfigPathPolicy.WritableRoots)
            {
                var dir = Path.Combine(root, name);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(file);
                    if (info.Length > ConfigPathPolicy.MaxBytes)
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (relativePath.EndsWith("Credentials.json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    files.Add(new
                    {
                        path = relativePath,
                        group = name,
                        size = info.Length,
                        modifiedAt = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                    });
                }
            }

            // 单文件的那一项也要能看见（启用的主题）。
            var themesFile = Path.Combine(root, "EnabledThemes.json");
            if (File.Exists(themesFile))
            {
                var info = new FileInfo(themesFile);
                files.Add(new
                {
                    path = "EnabledThemes.json",
                    group = "Themes",
                    size = info.Length,
                    modifiedAt = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                });
            }

            return Ok(command, HubJson.Serialize(new { count = files.Count, files }));
        }
        catch (Exception ex)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed, "列出配置失败：" + ex.Message);
        }
    }

    /// <summary>读一份配置的原文。</summary>
    private static CommandReportRequest ReadConfigFile(RemoteCommandDto command)
    {
        var request = HubJson.DeserializeOrDefault(command.Payload, new ConfigFilePayload());
        if (!TryResolveConfigPath(request.Path, out var full, out var reason))
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, reason);
        }

        if (!File.Exists(full))
        {
            return Fail(command, CommandErrorCodes.TargetNotFound, $"配置不存在：{request.Path}");
        }

        var info = new FileInfo(full);
        if (info.Length > ConfigPathPolicy.MaxBytes)
        {
            return Fail(command, CommandErrorCodes.Unsupported,
                $"文件超过 {ConfigPathPolicy.MaxBytes / 1024 / 1024}MB，不像是一份正常配置。");
        }

        try
        {
            return Ok(command, File.ReadAllText(full));
        }
        catch (Exception ex)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed, "读取配置失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 写回一份配置。
    /// <para>
    /// 顺序刻意是「先校验 → 再备份 → 最后写」：任何一步失败都不该留下半个文件。
    /// 返回里明确写「重启后生效」——宿主只在启动时读这些配置，不重启会让人以为没成功。
    /// </para>
    /// </summary>
    private static CommandReportRequest WriteConfigFile(RemoteCommandDto command)
    {
        var request = HubJson.DeserializeOrDefault(command.Payload, new ConfigFilePayload());
        if (!TryResolveConfigPath(request.Path, out var full, out var reason))
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, reason);
        }

        var content = request.Content ?? string.Empty;
        if (content.Length == 0)
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid, "载荷缺少 content 字段。");
        }

        if (Encoding.UTF8.GetByteCount(content) > ConfigPathPolicy.MaxBytes)
        {
            return Fail(command, CommandErrorCodes.Unsupported,
                $"内容超过 {ConfigPathPolicy.MaxBytes / 1024 / 1024}MB，已拒绝。");
        }

        // 只对 JSON 校验：写进去的必须是能解析的内容，否则宿主启动时直接抛异常。
        if (full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var _ = JsonDocument.Parse(content);
            }
            catch (JsonException ex)
            {
                return Fail(command, CommandErrorCodes.PayloadInvalid,
                    "内容不是合法 JSON，已拒绝写入（避免教室端起不来）：" + ex.Message);
            }
        }

        try
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 备份旧文件：写坏了还能手工改回去，这比"保证写对"更实际。
            if (File.Exists(full))
            {
                File.Copy(full, full + ".bak", overwrite: true);
            }

            File.WriteAllText(full, content);

            return Ok(command, $"已写入 {request.Path}（{Encoding.UTF8.GetByteCount(content)} 字节）。"
                + "宿主会在**重启后**读取这份配置。");
        }
        catch (Exception ex)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed, "写入配置失败：" + ex.Message);
        }
    }

    /// <summary>解析设置服务实例（兼容两种程序集/类型名写法）。</summary>
    private static object? ResolveSettingsService()
    {
        foreach (var typeName in new[]
                 {
                     "ClassIsland.Core.Abstractions.Services.ISettingsService, ClassIsland.Core",
                     "ClassIsland.Services.SettingsService, ClassIsland",
                 })
        {
            var type = Type.GetType(typeName);
            var instance = type is null ? null : IAppHost.Host?.Services.GetService(type);
            if (instance is not null)
            {
                return instance;
            }
        }

        return null;
    }

    /// <summary>解析自动化服务实例（兼容两种程序集/类型名写法）。</summary>
    private static object? ResolveAutomationService()
    {
        foreach (var typeName in new[]
                 {
                     "ClassIsland.Core.Abstractions.Services.IAutomationService, ClassIsland.Core",
                     "ClassIsland.Services.AutomationService, ClassIsland",
                 })
        {
            var type = Type.GetType(typeName);
            if (type is null)
            {
                continue;
            }

            var service = IAppHost.Host?.Services.GetService(type);
            if (service is not null)
            {
                return service;
            }
        }

        return null;
    }

    // ────────────────────────────── 远程诊断 ──────────────────────────────

    /// <summary>抓取主界面画面并上传，供管理端在「远程管理 → 诊断」中查看。</summary>
    private async Task<CommandReportRequest> CaptureScreenshotAsync(RemoteCommandDto command)
    {
        // 教室端可在插件设置里关闭远程截图：关闭后直接拒绝并说明原因，不做「偷偷截图」。
        if (!settings.Load().AllowRemoteScreenshot)
        {
            return Fail(command, CommandErrorCodes.DisabledByPolicy, "教室端已关闭远程截图（可在 ClassIsland 的插件设置「远程协助与隐私」中开启）。");
        }

        var png = DiagnosticsCollector.CaptureScreen(out var note);
        if (png is null)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed, note);
        }

        if (OnDiagnosticUploaded is null)
        {
            return Fail(command, CommandErrorCodes.HostApiUnavailable, "诊断上传通道不可用（插件尚未完成初始化）。");
        }

        await OnDiagnosticUploaded(new DiagnosticUploadRequest
        {
            CommandId = command.Id,
            Kind = "screenshot",
            Note = note,
            ContentType = "image/png",
            ContentBase64 = Convert.ToBase64String(png),
            CapturedAt = DateTimeOffset.UtcNow,
        });

        // 教室端给出一条可见提示：截图不是无声无息发生的。
        // #22 之后 Show 多了一个「是否强调」参数：这条是**隐私提示**，必须让人看见，
        // 所以保持全屏强调（与它出现以来一直的行为一致）。
        try
        {
            HubNotificationProviderHolder.Current?.Show("IslandHub 远程协助", "管理端抓取了本机画面用于排查问题", false,
                TimeSpan.FromSeconds(6), emphasize: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "展示截图提示失败。");
        }

        return Ok(command, $"截图已上传（{note}）。可在管理端「远程管理 → 诊断」查看。");
    }

    /// <summary>采集诊断数据包，并顺手把本地日志推给 A 端（设备日志页即可查看）。</summary>
    private async Task<CommandReportRequest> CollectDiagnosticBundleAsync(RemoteCommandDto command)
    {
        var text = DiagnosticsCollector.CollectBundle(state);

        if (OnLogsRequested is not null)
        {
            try
            {
                await OnLogsRequested(DiagnosticsCollector.SnapshotLogs(state)
                    .Select(line => new LogEntryDto
                    {
                        Timestamp = line.Timestamp,
                        Level = line.Level,
                        Message = line.Message,
                    })
                    .ToList());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "上传本地日志失败。");
            }
        }

        if (text.Length > MaxOutputLength)
        {
            text = text[..MaxOutputLength] + "\n…（内容过长，已截断）";
        }

        return Ok(command, text);
    }

    // ────────────────────────────── 课表：换课 / 调休 ──────────────────────────────

    /// <summary>临时换课，或恢复原课表。</summary>
    private CommandReportRequest RunLessonSwap(RemoteCommandDto command)
    {
        try
        {
            var request = HubJson.DeserializeOrDefault(command.Payload, new LessonSwapRequestDto());
            var message = lessonBridge.Swap(request);

            // Bridge 用「…失败：原因」表达**业务失败**（例如教室端没有这个科目）：
            // 它不是异常，但也绝不能回报成功——否则管理端显示「已生效」而教室端毫无变化。
            return message.Contains("失败", StringComparison.Ordinal)
                ? Fail(command, CommandErrorCodes.ExecutionFailed, message)
                : Ok(command, message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "执行临时换课失败。");
            return Fail(command, CommandErrorCodes.ExecutionFailed, "换课失败：" + ex.Message);
        }
    }

    /// <summary>调休：给某个日期指定一张课表；或撤销该日期的调休。</summary>
    private CommandReportRequest RunLessonAdjust(RemoteCommandDto command)
    {
        try
        {
            var request = HubJson.DeserializeOrDefault(command.Payload, new LessonAdjustRequestDto());
            var message = lessonBridge.Adjust(request);
            return message.Contains("失败", StringComparison.Ordinal)
                ? Fail(command, CommandErrorCodes.ExecutionFailed, message)
                : Ok(command, message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "执行调休失败。");
            return Fail(command, CommandErrorCodes.ExecutionFailed, "调休失败：" + ex.Message);
        }
    }

    /// <summary>上报课表目录（今天各节 / 科目 / 课表）。</summary>
    private async Task<CommandReportRequest> ReportLessonCatalogAsync(RemoteCommandDto command)
    {
        try
        {
            var catalog = lessonBridge.ReadCatalog();
            if (OnLessonCatalogRead is not null)
            {
                // 走独立接口上报（不是命令 Output）：目录 JSON 可能有上万字节，而命令输出会被截断，
                // 截断后的 JSON 解析不出来，反而更难查。上报失败不影响本次指令的成败。
                await OnLessonCatalogRead(catalog);
            }

            return Ok(command, $"已上报课表目录：{catalog.Slots.Count} 节、"
                + $"{catalog.Subjects.Count} 个科目、{catalog.ClassPlans.Count} 张课表。");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取课表目录失败。");
            return Fail(command, CommandErrorCodes.ExecutionFailed, "读取课表目录失败：" + ex.Message);
        }
    }

    // ────────────────────────────── helpers ──────────────────────────────

    /// <summary>
    /// Ping 目标主机（#107）：回答「这台教室机到某处通不通、延迟多少」。
    /// <para>只解析**每行回复**里的耗时（中英文通吃），不依赖汇总行的措辞——见
    /// <see cref="NetProbeParser.ParsePing"/>。原始输出也一并带回去：丢包时管理员要看是哪几条丢了。</para>
    /// </summary>
    private static async Task<CommandReportRequest> RunPingAsync(RemoteCommandDto command)
    {
        var payload = HubJson.Deserialize<NetProbePayload>(command.Payload);
        if (payload is null || !NetProbeParser.IsSafeHost(payload.Host))
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid,
                "目标地址不合法：只接受 IP、域名或主机名（不含空格与特殊符号）。");
        }

        var host = payload.Host!.Trim();
        var count = Math.Clamp(payload.Count ?? DefaultPingCount, 1, MaxPingCount);
        var countText = count.ToString(CultureInfo.InvariantCulture);
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var result = isWindows
            ? await RunToolAsync("ping", ["-n", countText, "-w", "1000", host])
            : await RunToolAsync("ping", ["-c", countText, "-W", "1", host]);
        if (result is null)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed,
                "无法启动 ping：这台机器上没有可用的 ping 命令。");
        }

        var summary = NetProbeParser.ParsePing(result.Value.Output, count);
        return Ok(command, $"{summary.Describe()}\n\n{TrimProbeOutput(result.Value.Output)}");
    }

    /// <summary>
    /// 路由追踪（#107）：回答「不通是断在哪一跳」。
    /// <para>刻意不解析每一跳的 IP：不同系统的输出格式差异大，而原文本来就够管理员看；
    /// 这里只给出「走了多少跳」这个概览数字。</para>
    /// </summary>
    private static async Task<CommandReportRequest> RunTracerouteAsync(RemoteCommandDto command)
    {
        var payload = HubJson.Deserialize<NetProbePayload>(command.Payload);
        if (payload is null || !NetProbeParser.IsSafeHost(payload.Host))
        {
            return Fail(command, CommandErrorCodes.PayloadInvalid,
                "目标地址不合法：只接受 IP、域名或主机名（不含空格与特殊符号）。");
        }

        var host = payload.Host!.Trim();
        var hopsText = MaxTraceHops.ToString(CultureInfo.InvariantCulture);
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var result = isWindows
            ? await RunToolAsync("tracert", ["-d", "-h", hopsText, "-w", "1000", host])
            : await RunToolAsync("traceroute", ["-n", "-m", hopsText, "-w", "1", host]);
        if (result is null)
        {
            return Fail(command, CommandErrorCodes.ExecutionFailed,
                "无法启动路由追踪：这台机器上没有可用的 tracert / traceroute 命令。");
        }

        var hops = NetProbeParser.CountTracerouteHops(result.Value.Output);
        var head = hops == 0
            ? "没能记录到任何一跳（目标可能被防火墙静默丢包，或地址无法解析）。"
            : $"共记录到 {hops} 跳（上限 {MaxTraceHops}）——看得断在哪一跳就看下面的原文。";
        return Ok(command, $"{head}\n\n{TrimProbeOutput(result.Value.Output)}");
    }

    /// <summary>
    /// 磁盘空间（#107）：教室机最常见的隐形故障就是系统盘被日志 / 缓存塞满，
    /// 而「不够用」在界面上往往只表现为各种奇怪的应用失败。
    /// </summary>
    private static CommandReportRequest RunDiskSpace(RemoteCommandDto command)
    {
        var lines = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady
                    || drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                {
                    continue;
                }

                var name = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                lines.Add(NetProbeParser.DescribeDisk($"{name}（{DescribeDriveType(drive.DriveType)}）",
                    drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (IOException)
            {
                // 拔掉的 U 盘 / 断开的网络盘：跳过，不影响其它分区。
            }
            catch (UnauthorizedAccessException)
            {
                // 没权限读的分区同样跳过。
            }
        }

        return lines.Count == 0
            ? Fail(command, CommandErrorCodes.ExecutionFailed, "没有读到任何可用的磁盘分区。")
            : Ok(command, string.Join("\n", lines));
    }

    /// <summary>分区类型的可读名称（报告要给管理员看，不是给人看枚举名）。</summary>
    private static string DescribeDriveType(DriveType type) => type switch
    {
        DriveType.Fixed => "本地磁盘",
        DriveType.Removable => "可移动磁盘",
        DriveType.Network => "网络磁盘",
        _ => type.ToString(),
    };

    /// <summary>
    /// 跑一个系统工具并取回输出。返回 <c>null</c> 表示进程**没能启动**（命令不存在 / 被策略拦住）。
    /// <para>⚠️ 参数一律用 <c>ArgumentList</c> 逐个传，**绝不拼 shell 命令行**：目标地址是管理员
    /// 输入的，拼串就等于把命令注入的口子开在教室机上。</para>
    /// <para>刻意**不设** <c>StandardOutputEncoding</c>：让 .NET 用系统默认控制台编码
    /// （中文 Windows 上是 GBK），否则 ping / tracert 输出里的中文会变成乱码。</para>
    /// </summary>
    private static async Task<(int ExitCode, string Output)?> RunToolAsync(string fileName, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (stdout + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : "\n" + stderr)).Trim();
            return (process.ExitCode, output);
        }
        catch (Exception)
        {
            // 命令不存在、被组策略禁用等：都归为「起不来」，由调用方给出人话。
            return null;
        }
    }

    /// <summary>截断过长的探测输出（报告里只保留可读的一段）。</summary>
    private static string TrimProbeOutput(string output) =>
        output.Length <= MaxProbeOutputLength
            ? output
            : output[..MaxProbeOutputLength] + "\n…（输出过长，教室端已截断）";

    private static CommandReportRequest Ok(RemoteCommandDto command, string output) => new()
    {
        CommandId = command.Id,
        Success = true,
        Output = output,
        ExitCode = 0,
    };

    /// <summary>
    /// 构造一条失败回报。
    /// </summary>
    /// <param name="command">被执行的指令。</param>
    /// <param name="errorCode">
    /// 失败原因分类，取值见 <see cref="CommandErrorCodes"/>。**刻意设为必填**：管理端靠它给出中文「怎么办」，
    /// 只带上自由文本的话，界面上就只能把宿主的原文（常常是英文异常）丢给老师看。
    /// </param>
    /// <param name="error">给人看的原文，保留细节（例如宿主抛出的异常信息）。</param>
    private static CommandReportRequest Fail(RemoteCommandDto command, string errorCode, string error) => new()
    {
        CommandId = command.Id,
        Success = false,
        Output = string.Empty,
        ExitCode = -1,
        Error = error,
        ErrorCode = errorCode,
    };
}

/// <summary>持有通知提供方实例，供静态执行路径访问。</summary>
public static class HubNotificationProviderHolder
{
    /// <summary>当前通知提供方实例。</summary>
    public static HubNotificationProvider? Current { get; set; }
}
