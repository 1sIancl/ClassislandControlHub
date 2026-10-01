using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
                if (!Options.MinimizeToTrayOnClose || _tray is null)
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
