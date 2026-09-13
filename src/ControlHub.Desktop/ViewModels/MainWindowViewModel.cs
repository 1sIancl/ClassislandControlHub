using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlHub.Desktop.Services;

namespace ControlHub.Desktop.ViewModels;

/// <summary>导航条目。</summary>
public sealed record NavItem(string Key, string Label, string Icon);

/// <summary>主窗口 ViewModel：导航 + 主题 + 各页面。</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppState _state;

    public ObservableCollection<NavItem> NavItems { get; } =
    [
        new("dashboard", "仪表盘", "\uE80F"),
        new("devices", "设备管理", "\uE7F8"),
        new("profiles", "配置档案", "\uE8F1"),
        new("deploy", "配置下发", "\uE8A8"),
        new("remote", "远程管理", "\uE756"),
        new("backup", "备份", "\uE7B8"),
        new("settings", "设置", "\uE713"),
    ];

    [ObservableProperty]
    private string page = "dashboard";

    public ViewModelBase CurrentViewModel => Page switch
    {
        "dashboard" => Dashboard,
        "devices" => Devices,
        "profiles" => Profiles,
        "deploy" => Deploy,
        "remote" => Remote,
        "backup" => Backup,
        "settings" => Settings,
        "profileEditor" => ProfileEditor,
        _ => Dashboard,
    };

    public ConnectViewModel Connect { get; }
    public DashboardViewModel Dashboard { get; }
    public DevicesViewModel Devices { get; }
    public ProfilesViewModel Profiles { get; }
    public DeployViewModel Deploy { get; }
    public RemoteViewModel Remote { get; }
    public BackupViewModel Backup { get; }
    public SettingsViewModel Settings { get; }
    public ProfileEditorViewModel ProfileEditor { get; }

    public bool Connected => _state.Connected;

    public MainWindowViewModel(
        AppState state,
        ConnectViewModel connect,
        DashboardViewModel dashboard,
        DevicesViewModel devices,
        ProfilesViewModel profiles,
        DeployViewModel deploy,
        RemoteViewModel remote,
        BackupViewModel backup,
        SettingsViewModel settings,
        ProfileEditorViewModel profileEditor)
    {
        _state = state;
        Connect = connect;
        Dashboard = dashboard;
        Devices = devices;
        Profiles = profiles;
        Deploy = deploy;
        Remote = remote;
        Backup = backup;
        Settings = settings;
        ProfileEditor = profileEditor;
        profiles.EditRequested = OpenProfileEditorAsync;
    }

    /// <summary>打开档案编辑器。</summary>
    public async Task OpenProfileEditorAsync(string profileId)
    {
        Page = "profileEditor";
        await ProfileEditor.LoadAsync(profileId);
        OnPropertyChanged(nameof(CurrentViewModel));
    }

    [RelayCommand]
    private void Navigate(string key)
    {
        Page = key;
        OnPropertyChanged(nameof(CurrentViewModel));
    }

    [RelayCommand]
    private void Disconnect()
    {
        _state.Connection.Token = string.Empty;
        _state.Save();
        OnPropertyChanged(nameof(Connected));
    }
}

