using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ControlHub.Desktop.Services;
using ControlHub.Desktop.ViewModels;
using ControlHub.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHub.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        var provider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = provider.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // 应用状态（连接、主题、当前页面）
        services.AddSingleton<AppState>();
        // REST API 客户端（复用 ControlHub.Protocol）
        services.AddSingleton<ApiClient>();

        // 各页面 ViewModel
        services.AddSingleton<ConnectViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<DevicesViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<DeployViewModel>();
        services.AddSingleton<RemoteViewModel>();
        services.AddSingleton<BackupViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ProfileEditorViewModel>();

        // 主窗口
        services.AddSingleton<MainWindowViewModel>();
    }
}
