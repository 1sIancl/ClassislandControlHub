using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
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
    ILogger<RemoteCommandExecutor> logger)
{
    /// <summary>单条指令回报的输出上限，避免超大输出占满上行链路。</summary>
    private const int MaxOutputLength = 24 * 1024;

    /// <summary>ClassIsland 用插件目录下的这个文件标记「已禁用」。</summary>
    private const string DisabledMarker = ".disabled";

    /// <summary>插件上报回调（由同步引擎注入，负责把插件列表回传 A 端）。</summary>
    public Func<List<PluginInfoDto>, Task>? OnPluginsReported { get; set; }

    /// <summary>执行一条指令。</summary>
    public async Task<CommandReportRequest> ExecuteAsync(RemoteCommandDto command)
    {
        logger.LogInformation("执行远程指令 {Kind}（{Id}）。", command.Kind, command.Id);
        try
        {
            return command.Kind switch
            {
                RemoteCommandKinds.Shell => await RunShellAsync(command),
                RemoteCommandKinds.Notify => RunNotify(command),
                RemoteCommandKinds.PluginRefresh => await ReportPluginsAsync(command),
                RemoteCommandKinds.PluginToggle => TogglePlugin(command),
                RemoteCommandKinds.PluginUninstall => UninstallPlugin(command),
                RemoteCommandKinds.AppearanceApply => ApplyAppearance(command),
                RemoteCommandKinds.Restart => Restart(command),
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

        // 0 = 浅色，1 = 深色（见 IThemeService.CurrentRealThemeMode）。
        int? mode = cfg.Theme?.Trim().ToLowerInvariant() switch
        {
            "light" => 0,
            "dark" => 1,
            _ => null,
        };

        Color? primary = null;
        if (!string.IsNullOrWhiteSpace(cfg.AccentColor) && Color.TryParse(cfg.AccentColor.Trim(), out var parsed))
        {
            primary = parsed;
        }

        if (mode is null && primary is null)
        {
            return Fail(command, "外观配置里没有可应用的项（主题或强调色），强调色需为 #RRGGBB 形式。");
        }

        var themeService = IAppHost.TryGetService<IThemeService>();
        if (themeService is null)
        {
            return Fail(command, "无法获取 ClassIsland 主题服务（IAppHost 未就绪）。");
        }

        themeService.SetTheme(mode ?? themeService.CurrentRealThemeMode, primary);

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

        var parts = new List<string>();
        if (mode is not null)
        {
            parts.Add($"主题={cfg.Theme}");
        }

        if (primary is not null)
        {
            parts.Add($"强调色={cfg.AccentColor}");
        }

        return Ok(command, "已应用外观：" + string.Join("，", parts) + "。");
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
