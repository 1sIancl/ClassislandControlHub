using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using ControlHub.Shell.Services;
using ControlHub.Shell.ViewModels;

namespace ControlHub.Shell.Views;

/// <summary>
/// 主窗口：工具条 + 状态栏 + 日志面板 + 内嵌的 A 端 Web 管理界面。
/// <para>业务界面完全来自 A 端 Web UI，这里只负责外壳自身的外观与交互。</para>
/// </summary>
public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private bool _webMessageBound;
    private DispatcherTimer? _themeTimer;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.NavigateRequested -= Navigate;
            _viewModel.ShowRequested -= BringToFront;
            _viewModel.LogAppended -= ScrollLogToEnd;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.NavigateRequested += Navigate;
            _viewModel.ShowRequested += BringToFront;
            _viewModel.LogAppended += ScrollLogToEnd;
            BindWebView();
        }
    }

    private void BindWebView()
    {
        if (_webMessageBound || this.FindControl<NativeWebView>("Web") is not { } web)
        {
            return;
        }

        _webMessageBound = true;

        // 页面 ↔ 外壳通道：页面里 window.chrome.webview.postMessage 的内容会从这里进来。
        web.WebMessageReceived += OnWebMessage;

        // 记录加载结果：GUI 模式下没有控制台，这行日志是「界面到底有没有起来」的唯一线索。
        web.NavigationCompleted += (_, _) =>
        {
            ShellLog.Info($"页面加载完成：{web.Source}");
            _ = SyncThemeAsync();
        };

        // 页面里的主题随时可能被改，定时跟一下，避免「外壳深色 + 页面浅色」的割裂感。
        _themeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _themeTimer.Tick += (_, _) => _ = SyncThemeAsync();
        _themeTimer.Start();
    }

    /// <summary>让外壳配色跟随网页里的主题设置。</summary>
    private async Task SyncThemeAsync()
    {
        if (Application.Current is not { } app || this.FindControl<NativeWebView>("Web") is not { } web)
        {
            return;
        }

        try
        {
            var theme = (await web.InvokeScript("document.documentElement.getAttribute('data-theme')"))?.Trim();
            var variant = theme switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };

            if (app.RequestedThemeVariant != variant)
            {
                app.RequestedThemeVariant = variant;
                ShellLog.Info($"外壳主题跟随页面：{theme ?? "跟随系统"}");
            }
        }
        catch (Exception ex)
        {
            // 页面还没就绪或 WebView 不支持脚本时忽略即可。
            ShellLog.Warn($"同步页面主题失败：{ex.Message}");
        }
    }

    /// <summary>把 WebView 的消息转发给视图模型（页面里也能重启 A 端）。</summary>
    private void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            _viewModel?.HandleShellMessage(e.Body ?? string.Empty);
        }
        catch (Exception ex)
        {
            ShellLog.Error("处理页面消息失败", ex);
        }
    }

    private void ScrollLogToEnd()
    {
        try
        {
            this.FindControl<ScrollViewer>("LogScroll")?.ScrollToEnd();
        }
        catch
        {
            // 面板未展开时忽略。
        }
    }

    private void Navigate(string url)
    {
        try
        {
            var web = this.FindControl<NativeWebView>("Web");
            if (web is null)
            {
                ShellLog.Warn("未找到 WebView 控件，无法导航。");
                return;
            }

            // 引导地址里带外壳令牌，日志里不要原样写出来。
            ShellLog.Info($"导航到 {(url.Contains("shellToken=") ? "（含外壳令牌，已隐去）" : url)}");
            web.Navigate(new Uri(url));
        }
        catch (Exception ex)
        {
            ShellLog.Error($"WebView 导航失败：{url}", ex);
        }
    }

    private void BringToFront()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
