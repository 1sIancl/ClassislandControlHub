using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlHub.Desktop.Services;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Desktop.ViewModels;

/// <summary>设备管理 ViewModel：设备列表 + 注册码 + 分组。</summary>
public sealed partial class DevicesViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly ApiClient _api;

    public ObservableCollection<DeviceSummaryDto> Devices { get; } = [];

    [ObservableProperty]
    private string status = "未加载";

    [ObservableProperty]
    private string enrollCode = string.Empty;

    [ObservableProperty]
    private string enrollCodeHint = "生成后显示注册码";

    public DevicesViewModel(AppState state, ApiClient api)
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

            Status = $"共 {Devices.Count} 台设备";
        }
        catch (Exception ex)
        {
            Status = "加载失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task GenerateCodeAsync()
    {
        if (!_state.Connected) return;
        try
        {
            var result = await _api.PostAsync<EnrollCodeResult>(
                _state.Connection.ServerUrl, _state.Connection.Token, "/admin/enroll-codes", new { });
            EnrollCode = result.Data?.Code ?? string.Empty;
            EnrollCodeHint = string.IsNullOrEmpty(EnrollCode) ? "生成失败" : "复制给 B 端用于注册";
        }
        catch (Exception ex)
        {
            EnrollCodeHint = "生成失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteDeviceAsync(DeviceSummaryDto device)
    {
        if (device is null) return;
        try
        {
            await _api.DeleteAsync(_state.Connection.ServerUrl, _state.Connection.Token,
                $"/admin/devices/{device.Id}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = "删除失败：" + ex.Message;
        }
    }

    private sealed class EnrollCodeResult
    {
        public string Code { get; set; } = string.Empty;
    }
}

