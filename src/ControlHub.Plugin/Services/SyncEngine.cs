using System.Reflection;
using System.Runtime.InteropServices;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Plugin.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ControlHub.Plugin.Services;

/// <summary>
/// 同步引擎：插件的核心后台循环。
/// <para>
/// 负责「注册设备 → 心跳 → 长轮询等待变更 → 拉取配置 → 应用 → 上报结果」的完整闭环，
/// 以及断线重连、令牌失效后重新注册、定向推送即时响应等容错逻辑。
/// </para>
/// </summary>
public sealed class SyncEngine(
    HubSettingsStore settings,
    HubState state,
    HubClient client,
    ClassIslandAdapter adapter,
    RemoteCommandExecutor commandExecutor,
    ILogger<SyncEngine> logger) : BackgroundService
{
    /// <summary>断线重连的退避秒数。</summary>
    private int _backoffSeconds = 3;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        logger.LogInformation("集控同步引擎已启动。");

        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = settings.Load();

            if (string.IsNullOrWhiteSpace(cfg.ServerUrl))
            {
                state.Status = HubConnectionStatus.NotConfigured;
                await SafeDelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            state.ServerUrl = cfg.ServerUrl;
            client.SetAllowInsecureTls(cfg.AllowInsecureTls);

            try
            {
                // 1) 确保已注册（令牌为空或失效时重新注册）。
                if (string.IsNullOrWhiteSpace(cfg.DeviceToken))
                {
                    await EnsureEnrolledAsync(cfg, stoppingToken);
                    cfg = settings.Load();
                }

                // 2) 心跳。
                var heartbeat = await HeartbeatAsync(cfg, stoppingToken);
                if (heartbeat.Message is not null)
                {
                    state.Log("info", "服务器消息：" + heartbeat.Message);
                }

                // 3) 自动同步：需要时拉取并应用。
                if (cfg.AutoSync && heartbeat.ShouldSync)
                {
                    await SyncAndApplyAsync(cfg, force: false, stoppingToken);
                    cfg = settings.Load();
                }

                state.Status = HubConnectionStatus.Connected;
                state.LastError = string.Empty;
                _backoffSeconds = 3;

                // 4) 执行服务端下发的远程指令（命令行 / 插件 / 外观 / 提醒等）。
                await ProcessCommandsAsync(cfg, stoppingToken);

                // 5) 长轮询等待下一次变更（服务器推送）。
                if (cfg.AutoSync)
                {
                    await LongPollAsync(cfg, stoppingToken);
                }
                else
                {
                    await SafeDelayAsync(TimeSpan.FromSeconds(Math.Max(5, cfg.SyncIntervalSeconds)), stoppingToken);
                }
            }
            catch (HubApiException ex) when (ex.Code == HubErrorCodes.DeviceRevoked)
            {
                state.Status = HubConnectionStatus.Revoked;
                state.LastError = ex.Message;
                state.Log("error", ex.Message);
                await SafeDelayAsync(TimeSpan.FromSeconds(60), stoppingToken);
            }
            catch (HubApiException ex) when (ex.Code == HubErrorCodes.DeviceUnknown
                                              || ex.Code == HubErrorCodes.AuthInvalid)
            {
                // 令牌失效，清空后下一轮重新注册。
                settings.Update(s => s.DeviceToken = string.Empty);
                state.Status = HubConnectionStatus.Disconnected;
                state.LastError = ex.Message;
                state.Log("warn", ex.Message + "（将重新注册）");
                await SafeDelayAsync(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (HubApiException ex)
            {
                state.Status = HubConnectionStatus.Disconnected;
                state.LastError = ex.Message;
                state.Log("error", ex.Message);
                await BackoffAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                state.Status = HubConnectionStatus.Disconnected;
                state.LastError = ex.Message;
                state.Log("error", ex.Message);
                logger.LogWarning(ex, "同步循环出现异常。");
                await BackoffAsync(stoppingToken);
            }
        }

        logger.LogInformation("集控同步引擎已停止。");
    }

    /// <summary>手动触发一次「注册 + 同步 + 应用」的完整流程。由设置页面的按钮调用。</summary>
    public async Task<ApplyResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        var cfg = settings.Load();
        if (string.IsNullOrWhiteSpace(cfg.ServerUrl))
        {
            throw new HubApiException("NOT_CONFIGURED", "请先填写服务器地址。");
        }

        client.SetAllowInsecureTls(cfg.AllowInsecureTls);
        state.Status = HubConnectionStatus.Connecting;

        if (string.IsNullOrWhiteSpace(cfg.DeviceToken))
        {
            await EnsureEnrolledAsync(cfg, cancellationToken);
        }

        var heartbeat = await HeartbeatAsync(cfg, cancellationToken);
        var result = await SyncAndApplyAsync(cfg, force: true, cancellationToken);

        state.Status = HubConnectionStatus.Connected;
        state.LastError = string.Empty;
        return result;
    }

    // ────────────────────────────── 内部步骤 ──────────────────────────────

    private async Task EnsureEnrolledAsync(PluginSettings cfg, CancellationToken ct)
    {
        state.Status = HubConnectionStatus.Connecting;
        state.Log("info", "正在向服务器注册设备…");

        var response = await client.EnrollAsync(cfg.ServerUrl, new EnrollRequest
        {
            EnrollCode = cfg.EnrollCode,
            DeviceId = cfg.DeviceId,
            DeviceName = string.IsNullOrWhiteSpace(cfg.DeviceName) ? Environment.MachineName : cfg.DeviceName,
            MachineName = Environment.MachineName,
            OsVersion = RuntimeInformation.OSDescription,
            ClassIslandVersion = GetClassIslandVersion(),
            PluginVersion = GetPluginVersion(),
            Capabilities = BuildCapabilities(cfg),
        }, ct);

        settings.Update(s =>
        {
            s.DeviceToken = response.DeviceToken;
            s.DeviceId = response.DeviceId;
            s.DeviceName = response.DeviceName;
        });

        state.ServerName = response.ServerName;
        state.ServerUrl = cfg.ServerUrl;
        state.Revision = response.Revision;
        state.Log("info", $"设备注册成功：{response.DeviceName}（分组：{response.GroupName ?? "未分组"}）");
    }

    private async Task<HeartbeatResponse> HeartbeatAsync(PluginSettings cfg, CancellationToken ct)
    {
        var response = await client.HeartbeatAsync(cfg.ServerUrl, cfg.DeviceToken, new HeartbeatRequest
        {
            State = state.Status == HubConnectionStatus.Connecting ? DeviceStates.Syncing : DeviceStates.Idle,
            AppliedRevision = cfg.AppliedRevision,
            AppliedPushEpoch = cfg.AppliedPushEpoch,
            UptimeSeconds = (long)(DateTime.UtcNow - ProcessStartTime).TotalSeconds,
            CurrentClassPlanName = state.CurrentClassPlanName,
            LastError = string.IsNullOrWhiteSpace(state.LastError) ? null : state.LastError,
            Metrics = new Dictionary<string, string>
            {
                ["memoryMb"] = (Environment.WorkingSet / 1024 / 1024).ToString(),
            },
        }, ct);

        return response;
    }

    private async Task<ApplyResult> SyncAndApplyAsync(PluginSettings cfg, bool force, CancellationToken ct)
    {
        var sync = await client.SyncAsync(cfg.ServerUrl, cfg.DeviceToken,
            cfg.AppliedRevision, cfg.AppliedPushEpoch, BuildCapabilities(cfg), force, ct);

        if (sync is null)
        {
            return new ApplyResult { Success = true, Message = "配置已是最新，无需同步。", AppliedSections = [] };
        }

        // 内容未变化（例如定向推送重复触发）时，仅推进版本号即可。
        var contentChanged = force || !string.Equals(sync.Checksum, cfg.LastAppliedChecksum, StringComparison.Ordinal);
        ApplyResult applyResult;

        if (contentChanged)
        {
            state.Status = HubConnectionStatus.Connecting;
            state.Log("info", $"收到新配置（版本 #{sync.Revision}），正在应用…");

            applyResult = adapter.Apply(sync.Content, new ApplyOptions
            {
                TimeLayouts = cfg.ApplyTimeLayouts,
                ClassPlans = cfg.ApplyClassPlans,
                Subjects = cfg.ApplySubjects,
                Settings = cfg.ApplySettings,
                LockLocalEditing = cfg.RespectLockLocalEditing && sync.Content.Settings.LockLocalEditing,
            });
        }
        else
        {
            applyResult = new ApplyResult
            {
                Success = true,
                Message = "配置内容未变化，已确认版本。",
                AppliedSections = BuildCapabilities(cfg),
                CurrentClassPlanName = state.CurrentClassPlanName,
            };
        }

        var result = applyResult.Success ? ApplyResults.Success : ApplyResults.Failed;

        await client.ReportAsync(cfg.ServerUrl, cfg.DeviceToken, new ApplyReportRequest
        {
            Revision = sync.Revision,
            PushEpoch = sync.PushEpoch,
            Result = result,
            AppliedSections = applyResult.AppliedSections,
            Message = applyResult.Message,
        }, ct);

        if (applyResult.Success)
        {
            settings.Update(s =>
            {
                s.AppliedRevision = sync.Revision;
                s.AppliedPushEpoch = sync.PushEpoch;
                s.LastAppliedChecksum = sync.Checksum;
            });

            state.Revision = sync.Revision;
            state.ProfileName = sync.ProfileName;
            state.LastSyncAt = DateTimeOffset.Now;
            state.Announcement = sync.Content.Settings.Announcement;
            state.RespectLock = sync.Content.Settings.LockLocalEditing;
            if (!string.IsNullOrWhiteSpace(applyResult.CurrentClassPlanName))
            {
                state.CurrentClassPlanName = applyResult.CurrentClassPlanName;
            }

            state.Log("info", $"同步完成：版本 #{sync.Revision}，档案「{sync.ProfileName}」。");
        }
        else
        {
            state.LastError = applyResult.Message;
            state.Log("error", applyResult.Message);
        }

        return applyResult;
    }

    /// <summary>拉取并执行服务端下发的远程指令，并回报结果。</summary>
    private async Task ProcessCommandsAsync(PluginSettings cfg, CancellationToken ct)
    {
        List<RemoteCommandDto> commands;
        try
        {
            commands = await client.GetCommandsAsync(cfg.ServerUrl, cfg.DeviceToken, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "拉取远程指令失败。");
            return;
        }

        foreach (var cmd in commands)
        {
            var report = await commandExecutor.ExecuteAsync(cmd);
            try
            {
                await client.ReportCommandAsync(cfg.ServerUrl, cfg.DeviceToken, report, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "回报指令结果失败：{Id}", cmd.Id);
            }
        }
    }

    private async Task LongPollAsync(PluginSettings cfg, CancellationToken ct)
    {
        var wait = await client.WaitAsync(cfg.ServerUrl, cfg.DeviceToken,
            cfg.AppliedRevision, cfg.AppliedPushEpoch, HubProtocol.DefaultLongPollSeconds, ct);

        if (wait.Changed)
        {
            state.Log("debug", "服务器推送了配置变更，开始拉取。");
            await SyncAndApplyAsync(cfg, force: false, ct);
        }
    }

    private List<string> BuildCapabilities(PluginSettings cfg)
    {
        var sections = new List<string>(4);
        if (cfg.ApplyTimeLayouts) sections.Add(HubProtocol.Sections.TimeLayouts);
        if (cfg.ApplyClassPlans) sections.Add(HubProtocol.Sections.ClassPlans);
        if (cfg.ApplySubjects) sections.Add(HubProtocol.Sections.Subjects);
        if (cfg.ApplySettings) sections.Add(HubProtocol.Sections.Settings);
        return sections;
    }

    private async Task BackoffAsync(CancellationToken ct)
    {
        await SafeDelayAsync(TimeSpan.FromSeconds(_backoffSeconds), ct);
        _backoffSeconds = Math.Min(_backoffSeconds * 2, 60);
    }

    private async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // 忽略：外层循环会因取消令牌退出。
        }
    }

    private static readonly DateTime ProcessStartTime = DateTime.UtcNow;

    private static string GetClassIslandVersion() =>
        typeof(ClassIsland.Core.Abstractions.PluginBase).Assembly.GetName().Version?.ToString() ?? "2.1.0.0";

    private static string GetPluginVersion() =>
        typeof(SyncEngine).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
}
