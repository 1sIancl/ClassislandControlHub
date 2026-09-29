using System.Diagnostics;

namespace ControlHub.Server.Services;

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
