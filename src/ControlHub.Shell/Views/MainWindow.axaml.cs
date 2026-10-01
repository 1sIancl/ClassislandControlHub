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
