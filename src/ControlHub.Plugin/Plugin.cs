using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ControlHub.Plugin.Models;
using ControlHub.Plugin.Services;
using ControlHub.Plugin.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ControlHub.Plugin;

/// <summary>
/// 集控客户端插件入口。
/// <para>
/// 插件在 ClassIsland 内以后台服务方式运行，周期性与集控服务器（A 端）通信：
/// 注册设备、上报心跳、长轮询等待变更、拉取并应用课表/时间表/科目配置。
/// </para>
/// </summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    /// <inheritdoc />
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 设置存储：延迟解析插件配置目录，避免在 Initialize 早期访问尚未就绪的属性。
        services.AddSingleton(sp => new HubSettingsStore(() => PluginConfigFolder));

        // 运行状态（供设置页面绑定展示）。
        services.AddSingleton<HubState>();

        // 核心服务。
        services.AddSingleton<ServerDiscovery>();
        services.AddSingleton<HubClient>();
        services.AddSingleton<ClassIslandAdapter>(sp => new ClassIslandAdapter(
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<ILogger<ClassIslandAdapter>>()));
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<ClassIslandClockService>();
        services.AddSingleton<TimeSyncService>();

        // 集控提醒提供方（AddNotificationProvider 会注册到 RegistryService 并托管）。
        services.AddNotificationProvider<HubNotificationProvider>();

        // 远程指令执行器（shell / 插件 / 外观 / 提醒 / 重启 / 诊断）。
        services.AddSingleton<RemoteCommandExecutor>(sp =>
        {
            // 各上报回调的公共部分：读出服务器地址与设备令牌后发送；失败不影响主流程。
            async Task UploadAsync(Func<HubClient, string, string, Task> send)
            {
                var cfg = sp.GetRequiredService<HubSettingsStore>().Load();
                if (string.IsNullOrWhiteSpace(cfg.DeviceToken))
                {
                    return;
                }

                try
                {
                    await send(sp.GetRequiredService<HubClient>(), cfg.ServerUrl, cfg.DeviceToken);
                }
                catch
                {
                    // 上报失败不影响主流程。
                }
            }

            var executor = new RemoteCommandExecutor(
                sp.GetRequiredService<ILogger<RemoteCommandExecutor>>(),
                sp.GetRequiredService<HubState>(),
                sp.GetRequiredService<HubSettingsStore>());

            executor.OnPluginsReported = plugins =>
                UploadAsync((client, url, token) => client.ReportPluginsAsync(url, token, plugins));

            executor.OnDiagnosticUploaded = request =>
                UploadAsync((client, url, token) => client.UploadDiagnosticAsync(url, token, request));

            executor.OnLogsRequested = entries =>
                UploadAsync((client, url, token) => client.UploadLogsAsync(url, token, entries));

            return executor;
        });

        // 后台同步循环。
        services.AddHostedService(sp => sp.GetRequiredService<SyncEngine>());

        // 后台时间同步循环。
        services.AddHostedService(sp => sp.GetRequiredService<TimeSyncService>());

        // 设置页面。
        services.AddSettingsPage<ControlHubSettingsPage>();
    }
}
