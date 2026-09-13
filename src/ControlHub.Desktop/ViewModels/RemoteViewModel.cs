using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlHub.Desktop.Services;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Desktop.ViewModels;

/// <summary>远程管理 ViewModel：命令行 / 插件 / 外观 / 提醒 / 备份。</summary>
public sealed partial class RemoteViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly ApiClient _api;

    public ObservableCollection<DeviceSummaryDto> Devices { get; } = [];

    public ObservableCollection<string> OutputLines { get; } = [];

    [ObservableProperty]
    private DeviceSummaryDto? selectedDevice;

    [ObservableProperty]
    private string command = string.Empty;

    [ObservableProperty]
    private bool broadcast;

    [ObservableProperty]
    private string notifyTitle = string.Empty;

    [ObservableProperty]
    private string notifyMessage = string.Empty;

    [ObservableProperty]
    private bool notifySpeak;

    [ObservableProperty]
    private string accentColor = "#1E90FF";

    [ObservableProperty]
    private string status = string.Empty;

    public RemoteViewModel(AppState state, ApiClient api)
    {
        _state = state;
        _api = api;
    }

    [RelayCommand]
    public async Task LoadDevicesAsync()
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

            SelectedDevice ??= Devices.FirstOrDefault(d => d.Online) ?? Devices.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RunCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(Command)) return;
        OutputLines.Clear();
        try
        {
            if (Broadcast || SelectedDevice is null)
            {
                var r = await _api.PostAsync<object>(_state.Connection.ServerUrl, _state.Connection.Token,
                    "/admin/devices/command",
                    new { kind = "shell", payload = System.Text.Json.JsonSerializer.Serialize(new { command = Command }) });
                OutputLines.Add($"> {Command}");
                OutputLines.Add("已统一下发到在线设备。");
            }
            else
            {
                var r = await _api.PostAsync<DeviceCommandDto>(_state.Connection.ServerUrl, _state.Connection.Token,
                    $"/admin/devices/{SelectedDevice.Id}/command",
                    new { kind = "shell", payload = System.Text.Json.JsonSerializer.Serialize(new { command = Command }) });
                OutputLines.Add($"> {Command}");
                OutputLines.Add($"已下发到 {SelectedDevice.Name}，指令 ID {r.Data?.Id}");
            }
        }
        catch (Exception ex)
        {
            OutputLines.Add("失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private async Task SendNotifyAsync()
    {
        try
        {
            if (Broadcast || SelectedDevice is null)
            {
                await _api.PostAsync(_state.Connection.ServerUrl, _state.Connection.Token,
                    "/admin/devices/command",
                    new { kind = "notify", payload = System.Text.Json.JsonSerializer.Serialize(new { title = NotifyTitle, message = NotifyMessage, speak = NotifySpeak }) });
            }
            else
            {
                await _api.PostAsync(_state.Connection.ServerUrl, _state.Connection.Token,
                    $"/admin/devices/{SelectedDevice.Id}/notify",
                    new { title = NotifyTitle, message = NotifyMessage, speak = NotifySpeak });
            }

            Status = "提醒已发送。";
        }
        catch (Exception ex)
        {
            Status = "发送失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ApplyAppearanceAsync()
    {
        try
        {
            var appearance = new { theme = (string?)null, accentColor = AccentColor, fontFamily = (string?)null };
            if (Broadcast || SelectedDevice is null)
            {
                var r = await _api.PostAsync<object>(_state.Connection.ServerUrl, _state.Connection.Token,
                    "/admin/devices/appearance", new { appearance });
                Status = $"已统一下发外观。";
            }
            else
            {
                await _api.PostAsync(_state.Connection.ServerUrl, _state.Connection.Token,
                    $"/admin/devices/{SelectedDevice.Id}/command",
                    new { kind = "appearance.apply", payload = System.Text.Json.JsonSerializer.Serialize(appearance) });
                Status = $"外观已下发到 {SelectedDevice.Name}。";
            }
        }
        catch (Exception ex)
        {
            Status = "下发失败：" + ex.Message;
        }
    }
}

