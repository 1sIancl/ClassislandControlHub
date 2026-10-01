using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using ControlHub.Shell.Services;
using ControlHub.Shell.ViewModels;
using ControlHub.Shell.Views;

namespace ControlHub.Shell;

/// <summary>
/// 外壳应用：创建主窗口、托盘图标，并在退出时按需停掉由它启动的 A 端。
/// </summary>
public partial class App : Application
{
    private ServerLifecycleService? _lifecycle;
    private TrayIcon? _tray;
    private bool _shuttingDown;

    /// <summary>启动参数（由 <see cref="Program"/> 注入）。</summary>
    internal static ShellOptions Options { get; set; } = new();

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ShellLog.Info("======== 外壳启动 ========");

            _lifecycle = new ServerLifecycleService(Options);
            var viewModel = new MainWindowViewModel(Options, _lifecycle);
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;

            // 关闭窗口默认最小化到托盘（A 端继续跑）；托盘不可用时退化为直接退出。
            window.Closing += (_, e) =>
            {
                // 应用正在退出（托盘菜单「退出」、--screenshot 等）时不再拦截。
                if (_shuttingDown || !Options.MinimizeToTrayOnClose || _tray is null)
                {
                    return;
                }

                e.Cancel = true;
                window.Hide();
                ShellLog.Info("窗口已最小化到托盘（A 端继续运行，右键托盘图标可退出）。");
            };

            _tray = CreateTray(desktop, window);
            desktop.ShutdownRequested += (_, _) => StopServerIfNeeded();
            desktop.Exit += (_, _) => StopServerIfNeeded();

            _ = viewModel.InitializeAsync();

            if (!string.IsNullOrWhiteSpace(Options.ScreenshotPath))
            {
                _ = CaptureScreenshotAndExitAsync(window);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private TrayIcon? CreateTray(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
            var tray = new TrayIcon
            {
                ToolTipText = "ClassislandControlHub 集控",
                IsVisible = true,
            };

            if (File.Exists(iconPath))
            {
                tray.Icon = new WindowIcon(iconPath);
            }

            tray.Menu = new NativeMenu
            {
                Items =
                {
                    new NativeMenuItem("打开管理界面")
                    {
                        Command = new RelayCommand(() => BringToFront(window)),
                    },
                    new NativeMenuItem("在浏览器打开")
                    {
                        Command = new RelayCommand(() => (window.DataContext as MainWindowViewModel)?.OpenInBrowser()),
                    },
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem("退出")
                    {
                        Command = new RelayCommand(() => desktop.Shutdown()),
                    },
                },
            };

            tray.Clicked += (_, _) => BringToFront(window);
            return tray;
        }
        catch (Exception ex)
        {
            ShellLog.Warn($"托盘图标不可用，关闭窗口将直接退出：{ex.Message}");
            return null;
        }
    }

    private static void BringToFront(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// 把窗口渲染成 PNG 后退出（<c>--screenshot 路径</c>）。
    /// <para>
    /// 只渲染本窗口的视觉树——不抓桌面，因此不会碰到用户屏幕上的其它内容；
    /// 但也因此不包含 WebView 里的网页（那是独立的原生子窗口），只用于核对外壳自身的外观。
    /// </para>
    /// </summary>
    private static async Task CaptureScreenshotAndExitAsync(Window window)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Options.ScreenshotDelaySeconds)));

        try
        {
            var path = Path.GetFullPath(Options.ScreenshotPath!);
            await SaveScreenshotAsync(window, path);

            // 再拍一张展开日志面板的，方便核对日志面板外观（正常使用下它是默认收起的）。
            if (window.DataContext is MainWindowViewModel viewModel)
            {
                viewModel.IsLogVisible = true;
                await Task.Delay(700);
                await SaveScreenshotAsync(window, Path.Combine(
                    Path.GetDirectoryName(path) ?? ".",
                    $"{Path.GetFileNameWithoutExtension(path)}.logs{Path.GetExtension(path)}"));
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error("截图失败", ex);
        }

        static async Task SaveScreenshotAsync(Window target, string path)
        {
            var size = new PixelSize(
                Math.Max(1, (int)Math.Round(target.Bounds.Width)),
                Math.Max(1, (int)Math.Round(target.Bounds.Height)));

            var png = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
                bitmap.Render(target);
                using var stream = new MemoryStream();
#pragma warning disable CS0618
                bitmap.Save(stream);
#pragma warning restore CS0618
                return stream.ToArray();
            });

            await File.WriteAllBytesAsync(path, png);
            ShellLog.Info($"已保存窗口截图：{path}（{size.Width}×{size.Height}）");
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void StopServerIfNeeded()
    {
        // ShutdownRequested 与 Exit 都会走到这里，加个闸门避免重复停机（白等一轮超时）。
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;

        if (_lifecycle is null || !Options.StopServerOnExit || _lifecycle.State != ServerState.Running)
        {
            return;
        }

        try
        {
            ShellLog.Info("退出外壳：停止由它启动的 A 端…");

            // 必须丢到线程池再等：StopAsync 内部的 await 续体会回到 UI 线程，
            // 在 UI 线程上 GetResult() 会直接死锁（表现为「停完 A 端但外壳不退出」）。
            Task.Run(() => _lifecycle.StopAsync()).GetAwaiter().GetResult();

            ShellLog.Info("A 端已停止，外壳退出。");
        }
        catch (Exception ex)
        {
            ShellLog.Error("停止 A 端失败", ex);
        }
    }
}
