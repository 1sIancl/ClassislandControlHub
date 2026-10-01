using Avalonia;

namespace ControlHub.Shell;

/// <summary>
/// 外壳入口：GUI 模式启动 Avalonia；<c>--check</c> / <c>--smoke</c> 走无界面路径，便于自动化验证。
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = ShellOptions.Parse(args);
        if (options is null)
        {
            ShellOptions.PrintUsage();
            return 1;
        }

        if (options.Check)
        {
            return ShellDiagnostics.RunCheck(options);
        }

        if (options.SmokeSeconds > 0)
        {
            return ShellDiagnostics.RunSmokeAsync(options).GetAwaiter().GetResult();
        }

        App.Options = options;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>Avalonia 应用构建（设计器与运行时共用）。</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
