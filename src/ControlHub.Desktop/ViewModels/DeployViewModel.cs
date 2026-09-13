using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlHub.Desktop.Services;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Desktop.ViewModels;

/// <summary>配置下发 ViewModel：全局推送 + 设备列表。</summary>
public sealed partial class DeployViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly ApiClient _api;

    public ObservableCollection<DeviceSummaryDto> Devices { get; } = [];

    [ObservableProperty]
    private string status = "未加载";

    public DeployViewModel(AppState state, ApiClient api)
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
            var devices = await _api.GetAsync<List<DeviceSummaryDto>>(
                _state.Connection.ServerUrl, _state.Connection.Token, "/admin/devices");
            Devices.Clear();
            foreach (var d in devices.Data ?? [])
            {
                Devices.Add(d);
            }

            Status = $"共 {Devices.Count} 台设备，{Devices.Count(d => !d.UpToDate)} 台待更新";
        }
        catch (Exception ex)
        {
            Status = "加载失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task PushAllAsync()
    {
        try
        {
            await _api.PostAsync(_state.Connection.ServerUrl, _state.Connection.Token,
                "/admin/push", new { });
            Status = "已触发全局推送。";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = "推送失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task PushDeviceAsync(DeviceSummaryDto device)
    {
        if (device is null) return;
        try
        {
            await _api.PostAsync(_state.Connection.ServerUrl, _state.Connection.Token,
                $"/admin/devices/{device.Id}/push", new { });
            Status = $"已推送到 {device.Name}。";
        }
        catch (Exception ex)
        {
            Status = "推送失败：" + ex.Message;
        }
    }
}

