using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlHub.Desktop.Services;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Desktop.ViewModels;

/// <summary>仪表盘 ViewModel：设备统计 + 服务器信息。</summary>
public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly ApiClient _api;

    [ObservableProperty]
    private string serverName = "—";

    [ObservableProperty]
    private string version = "—";

    [ObservableProperty]
    private int deviceCount;

    [ObservableProperty]
    private int onlineCount;

    [ObservableProperty]
    private int profileCount;

    [ObservableProperty]
    private string status = "未加载";

    public ObservableCollection<DeviceSummaryDto> Devices { get; } = [];

    public DashboardViewModel(AppState state, ApiClient api)
    {
        _state = state;
        _api = api;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!_state.Connected) return;
        try
        {
            var info = await _api.GetAsync<ServerInfoDto>(_state.Connection.ServerUrl, null!, "/server/info");
            if (info.Data is not null)
            {
                ServerName = info.Data.ServerName;
                Version = info.Data.Version;
            }

            var devices = await _api.GetAsync<List<DeviceSummaryDto>>(
                _state.Connection.ServerUrl, _state.Connection.Token, "/admin/devices");
            var list = devices.Data ?? [];
            DeviceCount = list.Count;
            OnlineCount = list.Count(d => d.Online);
            Devices.Clear();
            foreach (var d in list)
            {
                Devices.Add(d);
            }

            var profiles = await _api.GetAsync<List<ProfileDto>>(
                _state.Connection.ServerUrl, _state.Connection.Token, "/admin/profiles");
            ProfileCount = profiles.Data?.Count ?? 0;

            Status = $"加载完成 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Status = "加载失败：" + ex.Message;
        }
    }
}

