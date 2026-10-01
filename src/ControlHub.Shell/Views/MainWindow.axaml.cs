using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ControlHub.Shell.Services;
using ControlHub.Shell.ViewModels;

namespace ControlHub.Shell.Views;

/// <summary>
/// 主窗口：工具条 + 状态栏 + 内嵌的 A 端 Web 管理界面。
/// </summary>
public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private bool _webMessageBound;

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
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.NavigateRequested += Navigate;
            _viewModel.ShowRequested += BringToFront;
            _viewModel.LogAppended += ScrollLogToEnd;

            if (!_webMessageBound && this.FindControl<NativeWebView>("Web") is { } web)
            {
                _webMessageBound = true;

                // 页面 ↔ 外壳通道：页面里 window.chrome.webview.postMessage 的内容会从这里进来。
                web.WebMessageReceived += OnWebMessage;

                // 记录加载结果：GUI 模式下没有控制台，这行日志是「界面到底有没有起来」的唯一线索。
                web.NavigationCompleted += (_, _) => ShellLog.Info($"页面加载完成：{web.Source}");
            }
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
