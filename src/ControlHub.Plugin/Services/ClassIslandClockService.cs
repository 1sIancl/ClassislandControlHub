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
        // 写一次原则（2026-10-03 B 端崩溃事故复盘后的补救）：
        // 改宿主时钟配置要写 ClassIsland 的 Settings.json，而宿主自己在打开设置页、保存设置时也写同一个文件；
        // 两个写入方撞上时，宿主会把「文件被其它进程占用」当成严重错误直接退出（表现为「打开设置页就崩」）。
        // 因此这里**只在确实需要改的时候写一次**：宿主已经在用集控作时间源就直接返回，不再重复写。
        var settings = IAppHost.TryGetService<SettingsService>();
        if (settings is not null
            && settings.Settings.IsExactTimeEnabled
            && string.Equals(settings.Settings.ExactTimeServer?.Trim(), ntpHost.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            // 已经是目标状态：连同步都不必触发（宿主自己会按周期同步）。
            return $"ClassIsland 已经在使用集控服务器（{ntpHost}）作为精确时间源。";
        }

        try
        {
            return ConfigureNtpSyncCore(ntpHost);
        }
        catch (Exception ex)
        {
            // 写配置失败（常见：宿主正在保存设置）绝不能抛出去——后台循环会把异常抛给宿主。
            logger.LogWarning(ex, "应用时间同步设置失败，稍后会自动重试；其它功能不受影响。");
            return "应用时间同步失败（宿主可能正在保存配置），稍后会自动重试；其它功能不受影响。";
        }
    }

    /// <summary>
    /// 实际应用时间同步：先探测集控端 NTP 是否可用（不可用则把宿主改回系统时间），可用才写入配置。
    /// </summary>
    private string ConfigureNtpSyncCore(string ntpHost)
    {
        // 先探测再改（重要）：
        // 集控端的 NTP 端口（UDP 123）经常不可用——Windows 上该端口通常被「Windows 时间」服务占着，
        // Linux 上非特权用户也绑不上。若此时仍把宿主的时间源指过去，ClassIsland 会不停超时重试
        // （GuerrillaNtp 报 SocketException 10060），而每次重试前后都要写宿主的 Settings.json——
        // 写得越勤，越容易撞上「文件被其它进程占用」这类致命错误直接崩掉宿主。
        if (!CanReachNtp(ntpHost))
        {
            logger.LogWarning(
                "集控服务器 {Host} 的 NTP 端口（UDP 123）无响应。", ntpHost);

            // 自我修复：如果宿主的时间源**此刻正指向集控**（上一个版本改的、或手工改的），而集控又没有 NTP，
            // 宿主就会陷入「不停超时重试 → 反复写 Settings.json」的循环；严重时因为 Settings.json 被占用
            // 抛「严重错误」，把整个宿主带崩。这里把它改回「使用系统时间」，让循环当场停下。
            if (TryRevertHostTimeSource(ntpHost))
            {
                return $"集控服务器没有可用的 NTP 服务（{ntpHost}:123 无响应），"
                       + "已把 ClassIsland 改回「使用系统时间」以避免持续超时与反复写配置。"
                       + "如需以集控为时间源：请先确认服务端能绑定 UDP 123（Windows 上该端口常被系统时间服务占用）。";
            }

            return $"集控服务器没有可用的 NTP 服务（{ntpHost}:123 无响应），已保持 ClassIsland 原有时间源不变。"
                   + "如需以集控为时间源：请确认服务端能绑定 UDP 123（Windows 上该端口常被系统时间服务占用，"
                   + "可改用其它端口并在此处显式指定）。";
        }

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

    /// <summary>
    /// 若宿主的时间源正指向集控（且我们已确认连不上），就把它改回「使用系统时间」。
    /// <para>只处理「指向集控」这种情况：指向别的时间源是别人的配置，我们只提示、不擅自改。</para>
    /// <para>任何异常都被吞掉并记日志——这里是在**救火**，绝不能再把异常抛给宿主。</para>
    /// </summary>
    private bool TryRevertHostTimeSource(string ntpHost)
    {
        try
        {
            var settings = IAppHost.TryGetService<SettingsService>();
            if (settings is null)
            {
                return false;
            }

            var current = settings.Settings.ExactTimeServer?.Trim() ?? string.Empty;
            if (!string.Equals(current, ntpHost.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            settings.Settings.IsExactTimeEnabled = false;
            settings.SaveSettings("集控时间同步不可用，已改回系统时间");
            logger.LogWarning("已把 ClassIsland 改回「使用系统时间」：集控 {Host} 的 NTP 不可用。", ntpHost);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "回退 ClassIsland 时间源失败（不影响其它功能）。");
            return false;
        }
    }

    /// <summary>
    /// 探测指定主机是否真的在提供 NTP 服务（发一个最小的 NTP 客户端请求，等 1.5 秒）。
    /// <para>用同步 API + 超时即可：它只在同步流程里调用一次，且此处**不需要**异步带来的复杂度；
    /// 关键是绝不能因为「探测本身」把插件卡住。</para>
    /// </summary>
#pragma warning disable CA1849 // 同步套接字调用：这里刻意使用（带超时，且只在同步流程调用一次）
    private static bool CanReachNtp(string host)
    {
        try
        {
            using var udp = new System.Net.Sockets.UdpClient();
            udp.Client.ReceiveTimeout = 1500;
            udp.Client.SendTimeout = 1500;

            // NTP 客户端请求：48 字节，首字节 0x1B（LI=0, Version=3, Mode=3）。
            var request = new byte[48];
            request[0] = 0x1B;

            udp.Send(request, request.Length, host, 123);

            var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
            var response = udp.Receive(ref remote);
            return response.Length >= 48;
        }
        catch
        {
            // 任何异常（超时、DNS 失败、端口被拒）都视为「没有可用的 NTP」——这正是我们要保守处理的情况。
            return false;
        }
    }
#pragma warning restore CA1849
}
