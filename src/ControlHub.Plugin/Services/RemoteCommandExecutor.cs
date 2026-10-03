using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Services;
using ClassIsland.Shared;
using ControlHub.Plugin.Models;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using Microsoft.Extensions.Logging;

namespace ControlHub.Plugin.Services;

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
    HubSettingsStore settings)
{
    /// <summary>单条指令回报的输出上限，避免超大输出占满上行链路。</summary>
    private const int MaxOutputLength = 24 * 1024;

    /// <summary>ClassIsland 用插件目录下的这个文件标记「已禁用」。</summary>
    private const string DisabledMarker = ".disabled";

    /// <summary>插件上报回调（由插件入口注入，负责把插件列表回传 A 端）。</summary>
    public Func<List<PluginInfoDto>, Task>? OnPluginsReported { get; set; }

    /// <summary>诊断工件上传回调（截图等二进制内容经此回传 A 端）。</summary>
    public Func<DiagnosticUploadRequest, Task>? OnDiagnosticUploaded { get; set; }

    /// <summary>日志上报回调（采集诊断数据包时顺带把本地日志推给 A 端，供设备日志页查看）。</summary>
    public Func<List<LogEntryDto>, Task>? OnLogsRequested { get; set; }

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
                RemoteCommandKinds.PluginRefresh => await ReportPluginsAsync(command),
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
                _ => Fail(command, $"不支持的指令类型：{command.Kind}"),
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "执行远程指令失败：{Kind}", command.Kind);
            return Fail(command, ex.Message);
        }
    }

    // ────────────────────────────── shell ──────────────────────────────

    private static async Task<CommandReportRequest> RunShellAsync(RemoteCommandDto command)
    {
        var text = ResolveShellText(command, out var error);
        if (text is null)
        {
            return Fail(command, error!);
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
            return Fail(command, "无法启动进程。");
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

    private static CommandReportRequest RunNotify(RemoteCommandDto command)
    {
        var req = HubJson.Deserialize<NotifyRequestDto>(command.Payload) ?? new NotifyRequestDto();
        if (string.IsNullOrWhiteSpace(req.Title) && string.IsNullOrWhiteSpace(req.Message))
        {
            return Fail(command, "提醒内容为空。");
        }

        var provider = HubNotificationProviderHolder.Current;
        if (provider is null)
        {
            return Fail(command, "提醒提供方尚未就绪，请稍后重试。");
        }

        TimeSpan? duration = req.DurationSeconds > 0 ? TimeSpan.FromSeconds(req.DurationSeconds) : null;
        provider.Show(req.Title, req.Message, req.Speak, duration);

        return Ok(command, $"提醒已展示（语音播报：{(req.Speak ? "开" : "关")}）");
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
            return Fail(command, "缺少插件 ID。");
        }

        var dir = FindPluginDir(payload.PluginId);
        if (dir is null)
        {
            return Fail(command, $"未找到插件目录：{payload.PluginId}");
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
            File.WriteAllText(marker, "disabled by ClassislandControlHub");
        }

        return Ok(command,
            $"插件 {Path.GetFileName(dir)} 已{(payload.Enabled ? "启用" : "禁用")}，需重启 ClassIsland 生效。");
    }

    private static CommandReportRequest UninstallPlugin(RemoteCommandDto command)
    {
        var payload = HubJson.Deserialize<PluginTogglePayload>(command.Payload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.PluginId))
        {
            return Fail(command, "缺少插件 ID。");
        }

        var dir = FindPluginDir(payload.PluginId);
        if (dir is null)
        {
            return Fail(command, $"未找到插件目录：{payload.PluginId}");
        }

        if (string.Equals(Path.GetFileName(dir), "ControlHub.Plugin", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(command, "不允许卸载集控插件本身。");
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            return Fail(command, "卸载失败：" + ex.Message);
        }

        return Ok(command, $"插件 {Path.GetFileName(dir)} 已卸载，需重启 ClassIsland 生效。");
    }

    private sealed class PluginTogglePayload
    {
        public string? PluginId { get; set; }
        public bool Enabled { get; set; }
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
            return Fail(command, "外观配置为空。");
        }

        // 走 ClassIsland 自己的设置服务，而不是 IThemeService.SetTheme：
        // ① SetTheme 只改运行时主题、**不写设置**，宿主随后会用 Settings.Theme / ColorSource / PrimaryColor
        //    覆盖回去（表现为「提示已应用、界面没变化，重启就还原」）；
        // ② 改 Settings.* 会立即生效并自动落盘，与宿主自己的设置页行为一致。
        var settings = IAppHost.TryGetService<SettingsService>();
        if (settings is null)
        {
            return Fail(command, "无法获取 ClassIsland 设置服务（IAppHost 未就绪）。");
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
                    s.IsMainWindowBackgroundMaterialEnabled = false;
                    applied.Add("背景材质=关闭");
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
            s.IsCustomForegroundColorEnabled = true;
            s.CustomForegroundColor = foreground;
            applied.Add($"前景色={cfg.ForegroundColor!.Trim()}");
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
            return Fail(command, "没有应用任何外观：" + (skipped.Count > 0
                ? string.Join("；", skipped)
                : "配置里没有可应用的项。"));
        }

        // 属性变更已触发宿主自动保存；这里再显式保存一次作为双保险（幂等）。
        settings.SaveSettings("集控下发外观");

        // 留档，便于设置页展示「最近一次下发的外观」。
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClassIsland", "ControlHub");
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
        s.IsMainWindowBackgroundMaterialEnabled = true;

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
                return Fail(command, "无法确定可执行文件路径。");
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
            return Fail(command, "重启失败：" + ex.Message);
        }
    }

    // ────────────────────────────── 电源管理 ──────────────────────────────

    /// <summary>关机 / 重启：调用 Windows 自带的 shutdown 命令，立即执行且不弹确认。</summary>
    private static CommandReportRequest RunPowerCommand(RemoteCommandDto command, string mode, string okText)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Fail(command, "电源管理仅支持 Windows 终端。");
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
            return Fail(command, "电源指令执行失败：" + ex.Message);
        }
    }

    /// <summary>睡眠：调用 Windows 的 SetSuspendState（休眠 = false）。</summary>
    private static CommandReportRequest Sleep(RemoteCommandDto command)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Fail(command, "电源管理仅支持 Windows 终端。");
        }

        try
        {
            // bHibernate=false 即睡眠；bForce=false 允许应用程序否决（避免强断未保存的板书/文档）。
            var ok = SetSuspendState(false, false, false);
            return ok ? Ok(command, "正在进入睡眠。") : Fail(command, "睡眠请求被系统拒绝。");
        }
        catch (Exception ex)
        {
            return Fail(command, "睡眠失败：" + ex.Message);
        }
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
                return Fail(command, "当前 ClassIsland 未提供自动化服务，无法远程触发自动化。");
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
            return Fail(command, "读取自动化配置失败：" + ex.Message);
        }
    }

    /// <summary>触发一个自动化信号：等价于 ClassIsland 内部「广播信号」，会点亮所有监听它的工作流。</summary>
    private static CommandReportRequest TriggerAutomation(RemoteCommandDto command)
    {
        var request = HubJson.DeserializeOrDefault(command.Payload, new AutomationTriggerPayload());
        var signal = (request.Signal ?? string.Empty).Trim();
        if (signal.Length == 0)
        {
            return Fail(command, "载荷缺少 signal 字段。");
        }

        var handlerType = Type.GetType(
            "ClassIsland.Services.Automation.Triggers.SignalTriggerHandlerService, ClassIsland");
        var handler = handlerType is null ? null : IAppHost.Host?.Services.GetService(handlerType);
        if (handler is null)
        {
            return Fail(command, "当前 ClassIsland 未提供信号触发器服务，无法远程触发自动化。");
        }

        var method = handlerType!.GetMethod("EmitSignal", [typeof(string), typeof(bool)]);
        if (method is null)
        {
            return Fail(command, "信号触发器服务的接口与预期不符（EmitSignal 不存在）。");
        }

        method.Invoke(handler, [signal, false]);
        return Ok(command, $"已触发自动化信号「{signal}」。");
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
            return Fail(command, "教室端已关闭远程截图（可在 ClassIsland 的插件设置「远程协助与隐私」中开启）。");
        }

        var png = DiagnosticsCollector.CaptureScreen(out var note);
        if (png is null)
        {
            return Fail(command, note);
        }

        if (OnDiagnosticUploaded is null)
        {
            return Fail(command, "诊断上传通道不可用（插件尚未完成初始化）。");
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
        try
        {
            HubNotificationProviderHolder.Current?.Show("集控远程协助", "管理端抓取了本机画面用于排查问题", false,
                TimeSpan.FromSeconds(6));
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

    // ────────────────────────────── helpers ──────────────────────────────

    private static CommandReportRequest Ok(RemoteCommandDto command, string output) => new()
    {
        CommandId = command.Id,
        Success = true,
        Output = output,
        ExitCode = 0,
    };

    private static CommandReportRequest Fail(RemoteCommandDto command, string error) => new()
    {
        CommandId = command.Id,
        Success = false,
        Output = string.Empty,
        ExitCode = -1,
        Error = error,
    };
}

/// <summary>持有通知提供方实例，供静态执行路径访问。</summary>
public static class HubNotificationProviderHolder
{
    /// <summary>当前通知提供方实例。</summary>
    public static HubNotificationProvider? Current { get; set; }
}
