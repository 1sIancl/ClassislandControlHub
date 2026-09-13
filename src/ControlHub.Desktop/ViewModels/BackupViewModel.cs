using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlHub.Desktop.Services;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Desktop.ViewModels;

/// <summary>备份 ViewModel。</summary>
public sealed partial class BackupViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly ApiClient _api;

    public ObservableCollection<BackupEntryDto> Backups { get; } = [];

    [ObservableProperty]
    private string status = "未加载";

    public BackupViewModel(AppState state, ApiClient api)
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
            var list = await _api.GetAsync<List<BackupEntryDto>>(
                _state.Connection.ServerUrl, _state.Connection.Token, "/admin/backups");
            Backups.Clear();
            foreach (var b in list.Data ?? [])
            {
                Backups.Add(b);
            }

            Status = $"共 {Backups.Count} 个备份";
        }
        catch (Exception ex)
        {
            Status = "加载失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        try
        {
            await _api.PostAsync<object>(_state.Connection.ServerUrl, _state.Connection.Token,
                "/admin/backups", new { note = "管理端手动备份" });
            Status = "备份已创建。";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = "创建失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(BackupEntryDto backup)
    {
        if (backup is null) return;
        try
        {
            await _api.DeleteAsync(_state.Connection.ServerUrl, _state.Connection.Token,
                $"/admin/backups/{backup.Id}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = "删除失败：" + ex.Message;
        }
    }
}
