using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ClassIsland.Core.Abstractions.Services;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using Microsoft.Extensions.Logging;

namespace ControlHub.Plugin.Services;

/// <summary>
/// 远程指令执行器：执行 A 端下发的指令并返回结果。
/// <para>支持：shell（命令行）、notify（提醒）、plugin.refresh（上报插件列表）、
/// plugin.toggle/uninstall（插件启停/卸载）、appearance.apply（写入 ClassIsland 设置）、restart（重启）。</para>
/// </summary>
public sealed class RemoteCommandExecutor(
    ILogger<RemoteCommandExecutor> logger)
{
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
        string text;
        try
        {
            var payload = JsonSerializer.Deserialize<ShellPayload>(command.Payload);
            text = payload?.Command ?? command.Payload;
            if (!string.IsNullOrWhiteSpace(payload?.Args))
            {
                text += " " + payload.Args;
            }
        }
        catch (JsonException)
        {
            text = command.Payload;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Fail(command, "命令为空。");
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
        return new CommandReportRequest
        {
            CommandId = command.Id,
            Success = process.ExitCode == 0,
            Output = output,
            ExitCode = process.ExitCode,
            Error = process.ExitCode == 0 ? null : stderr,
        };
    }

    private sealed class ShellPayload
    {
        public string? Command { get; set; }
        public string? Args { get; set; }
    }

    // ────────────────────────────── notify ──────────────────────────────

    private static CommandReportRequest RunNotify(RemoteCommandDto command)
    {
        var req = JsonSerializer.Deserialize<NotifyRequestDto>(command.Payload) ?? new NotifyRequestDto();
        TimeSpan? duration = req.DurationSeconds > 0 ? TimeSpan.FromSeconds(req.DurationSeconds) : null;
        HubNotificationProviderHolder.Current?.Show(req.Title, req.Message, req.Speak, duration);
        return Ok(command, "提醒已展示。");
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

    /// <summary>收集已加载插件信息。</summary>
    public static List<PluginInfoDto> CollectPlugins()
    {
        var result = new List<PluginInfoDto>();
        try
        {
            foreach (var p in IPluginService.LoadedPlugins)
            {
                result.Add(new PluginInfoDto
                {
                    Id = p.Manifest.Id,
                    Name = p.Manifest.Name,
                    Version = p.Manifest.Version.ToString(),
                    IsEnabled = true,
                    Author = p.Manifest.Author,
                    Description = p.Manifest.Description,
                });
            }
        }
        catch
        {
            // 忽略：插件服务未就绪时返回空列表。
        }

        return result;
    }

    private static CommandReportRequest TogglePlugin(RemoteCommandDto command)
    {
        var payload = JsonSerializer.Deserialize<PluginTogglePayload>(command.Payload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.PluginId))
        {
            return Fail(command, "缺少插件 ID。");
        }

        var dir = FindPluginDir(payload.PluginId);
        if (dir is null)
        {
            return Fail(command, "未找到插件目录。");
        }

        var marker = Path.Combine(dir, ".disabled");
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

        return Ok(command, $"插件 {payload.PluginId} 已{(payload.Enabled ? "启用" : "禁用")}，需重启 ClassIsland 生效。");
    }

    private static CommandReportRequest UninstallPlugin(RemoteCommandDto command)
    {
        var payload = JsonSerializer.Deserialize<PluginTogglePayload>(command.Payload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.PluginId))
        {
            return Fail(command, "缺少插件 ID。");
        }

        var dir = FindPluginDir(payload.PluginId);
        if (dir is null)
        {
            return Fail(command, "未找到插件目录。");
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            return Fail(command, "卸载失败：" + ex.Message);
        }

        return Ok(command, $"插件 {payload.PluginId} 已卸载，需重启 ClassIsland 生效。");
    }

    private sealed class PluginTogglePayload
    {
        public string? PluginId { get; set; }
        public bool Enabled { get; set; }
    }

    private static string? FindPluginDir(string pluginId)
    {
        // ClassIsland 插件目录：应用目录/Plugins/<目录名>，目录名通常为插件 id 或程序集名。
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { "Plugins", "plugins" })
        {
            var root = Path.Combine(baseDir, candidate);
            if (!Directory.Exists(root))
            {
                continue;
            }

            var direct = Path.Combine(root, pluginId);
            if (Directory.Exists(direct))
            {
                return direct;
            }

            foreach (var dir in Directory.GetDirectories(root))
            {
                var manifest = Path.Combine(dir, "manifest.yml");
                if (File.Exists(manifest)
                    && File.ReadAllText(manifest).Contains($"id: {pluginId}", StringComparison.OrdinalIgnoreCase))
                {
                    return dir;
                }
            }
        }

        return null;
    }

    // ────────────────────────────── appearance ──────────────────────────────

    private static CommandReportRequest ApplyAppearance(RemoteCommandDto command)
    {
        var cfg = JsonSerializer.Deserialize<AppearanceConfigDto>(command.Payload);
        if (cfg is null)
        {
            return Fail(command, "外观配置为空。");
        }

        // 外观配置持久化到插件数据目录，供设置页展示；实际主题/组件写入由 ClassIsland 侧承接。
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClassIsland", "ControlHub");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "appearance.json"), JsonSerializer.Serialize(cfg));

        return Ok(command, $"已应用外观配置（主题={cfg.Theme ?? "不变"}，强调色={cfg.AccentColor ?? "不变"}），需重启 ClassIsland 生效。");
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
