using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlHub.Desktop.Services;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Desktop.ViewModels;

/// <summary>设置 ViewModel：主题 + 服务器信息 + 时间偏移。</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly ApiClient _api;

    [ObservableProperty]
    private string theme = "system";

    [ObservableProperty]
    private string serverName = "—";

    [ObservableProperty]
    private string version = "—";

    [ObservableProperty]
    private string timeOffset = "0";

    [ObservableProperty]
    private string status = string.Empty;

    public SettingsViewModel(AppState state, ApiClient api)
    {
        _state = state;
        _api = api;
        theme = state.Theme;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!_state.Connected) return;
        try
        {
            var info = await _api.GetAsync<ServerInfoDto>(
                _state.Connection.ServerUrl, null!, "/server/info");
            if (info.Data is not null)
            {
                ServerName = info.Data.ServerName;
                Version = info.Data.Version;
            }
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void ApplyTheme(string value)
    {
        Theme = value;
        _state.Theme = value;
        var app = Avalonia.Application.Current;
        if (app is not null)
        {
            app.RequestedThemeVariant = value switch
            {
                "light" => Avalonia.Styling.ThemeVariant.Light,
                "dark" => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
        }
    }
}
