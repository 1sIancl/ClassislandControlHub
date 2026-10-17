using System.Diagnostics;

namespace IslandHub.Server.Services;

/// <summary>
/// 进程级运行状态：记录<b>服务端进程自身</b>的启动时刻与已运行时长。
/// </summary>
/// <remarks>
/// 不要用 <see cref="Environment.TickCount64"/> 充当运行时长——那是**操作系统开机以来**的毫秒数：
/// 机器开了三天、服务端刚重启完，界面会照旧显示「运行时长 3 天」，
/// 结果就是升级或改配置后重启服务，无法从界面确认究竟有没有生效。
/// </remarks>
internal static class ServerRuntime
{
    private static DateTimeOffset _startedAt = ResolveStart();

    /// <summary>服务端进程启动时刻（UTC）。</summary>
    public static DateTimeOffset StartedAt => _startedAt;

    /// <summary>服务端进程已运行时长。</summary>
    public static TimeSpan Uptime => DateTimeOffset.UtcNow - _startedAt;

    /// <summary>在程序启动时调用一次，让启动时刻在启动瞬间就落定。</summary>
    public static void Init() => _startedAt = ResolveStart();

    // ── 配置热重载（#87）──
    // 为什么要记这个：改完 appsettings 后，「到底生效了没有」此前只能靠重启一次来确认；
    // 现在把它暴露在 /server/info 与界面上，改完刷新一下就知道。

    /// <summary>最近一次配置热重载的时刻（UTC）；从未重载过为 <c>null</c>。</summary>
    public static DateTimeOffset? ConfigReloadedAt { get; private set; }

    /// <summary>配置热重载次数（自本次启动起）。</summary>
    public static int ConfigReloadCount { get; private set; }

    /// <summary>最近一次重载里「真的变了」的项（用于日志与界面提示）。</summary>
    public static IReadOnlyList<string> ConfigReloadChanges { get; private set; } = [];

    /// <summary>记一次配置热重载。</summary>
    public static void RecordConfigReload(IReadOnlyList<string> changes)
    {
        ConfigReloadedAt = DateTimeOffset.UtcNow;
        ConfigReloadCount++;
        ConfigReloadChanges = changes;
    }

    private static DateTimeOffset ResolveStart()
    {
        try
        {
            // 进程启动时间由系统在内核里记录，与「何时被读取」无关。
            return new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            // 极少数受限环境读不到进程信息，退化为当前时刻；
            // Init 在启动早期调用，可把这个退化的误差压到最小。
            return DateTimeOffset.UtcNow;
        }
    }
}
