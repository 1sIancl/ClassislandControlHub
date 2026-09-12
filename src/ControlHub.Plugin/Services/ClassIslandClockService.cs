using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Services;
using ClassIsland.Shared;
using Microsoft.Extensions.Logging;

namespace ControlHub.Plugin.Services;

/// <summary>
/// 封装对 ClassIsland「精确时间」的配置。
/// <para>
/// ClassIsland 的时钟有两种来源：默认使用系统时间；启用「使用精确时间」后，会从
/// <see cref="ClassIsland.Models.Settings.ExactTimeServer"/> 指定的 NTP 服务器同步精确时间
/// （而非系统时间）。本服务把 ClassIsland 的时间服务器指向集控服务器（A 端）的 NTP 服务，
/// 从而让大屏时钟以 A 端为时间源——修改的是 ClassIsland 的时钟，而非 Windows 系统时间。
/// </para>
/// </summary>
public sealed class ClassIslandClockService(ILogger<ClassIslandClockService> logger)
{
    /// <summary>
    /// 把 ClassIsland 的精确时间服务器指向 <paramref name="ntpHost"/> 并启用，随后触发一次立即同步。
    /// </summary>
    /// <param name="ntpHost">A 端 NTP 服务器主机名或 IP（标准 UDP 123 端口）。</param>
    /// <returns>同步状态描述。</returns>
    public string ConfigureNtpSync(string ntpHost)
    {
        var settings = IAppHost.TryGetService<SettingsService>()
                       ?? throw new InvalidOperationException("无法获取 ClassIsland 设置服务（IAppHost 未就绪）。");

        settings.Settings.ExactTimeServer = ntpHost;
        settings.Settings.IsExactTimeEnabled = true;
        settings.SaveSettings("集控时间同步");

        var exactTime = IAppHost.TryGetService<IExactTimeService>();
        exactTime?.Sync();

        var status = exactTime?.SyncStatusMessage ?? string.Empty;
        logger.LogInformation("ClassIsland 精确时间服务器已指向 {Host}，同步状态：{Status}", ntpHost, status);
        return string.IsNullOrWhiteSpace(status)
            ? $"已将 ClassIsland 精确时间服务器指向 {ntpHost}。"
            : $"已将 ClassIsland 精确时间服务器指向 {ntpHost}：{status}";
    }

    /// <summary>当前 ClassIsland 精确时间服务器配置（用于状态展示）。</summary>
    public string GetExactTimeServer()
    {
        var settings = IAppHost.TryGetService<SettingsService>();
        return settings?.Settings.ExactTimeServer ?? string.Empty;
    }
}
