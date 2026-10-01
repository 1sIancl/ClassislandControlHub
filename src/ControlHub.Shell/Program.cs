using Avalonia;

namespace ControlHub.Shell;

/// <summary>
/// 外壳入口：GUI 模式启动 Avalonia；<c>--check</c> / <c>--smoke</c> 走无界面路径，便于自动化验证。
/// </summary>
internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        var options = ShellOptions.Parse(args);
        if (options is null)
        {
            ShellOptions.PrintUsage();
            return 1;
        }

        // 自检与冒烟不参与单实例限制：它们是无界面的诊断入口，可能被用来排障。
        if (options.Check)
        {
            return ShellDiagnostics.RunCheck(options);
        }

        if (options.SmokeSeconds > 0)
        {
            return ShellDiagnostics.RunSmokeAsync(options).GetAwaiter().GetResult();
        }

        // 单实例：第二个外壳既没必要，也会让人搞不清到底是哪个实例在管 A 端进程。
        _singleInstance = new Mutex(true, @"Local\ClassislandControlHub.Shell", out var isFirstInstance);
        if (!isFirstInstance)
        {
            ShellLog.Warn("已有一个外壳实例在运行（见系统托盘），本次启动直接退出。");
            Console.Error.WriteLine("ClassislandControlHub 外壳已在运行：请在系统托盘里打开它。");
            return 2;
        }

        // GUI 模式下没有控制台，未处理异常必须落到日志里，否则崩溃后什么都查不到。
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShellLog.Error("未处理异常",
            e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ShellLog.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };

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
