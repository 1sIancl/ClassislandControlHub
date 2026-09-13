using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlHub.Desktop.Services;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Desktop.ViewModels;

/// <summary>配置档案 ViewModel：档案列表 + 新建/删除。</summary>
public sealed partial class ProfilesViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly ApiClient _api;

    public ObservableCollection<ProfileDto> Profiles { get; } = [];

    [ObservableProperty]
    private string status = "未加载";

    /// <summary>请求打开档案编辑器（由主窗口注入回调）。</summary>
    public Func<string, Task>? EditRequested { get; set; }

    public ProfilesViewModel(AppState state, ApiClient api)
    {
        _state = state;
        _api = api;
    }

    [RelayCommand]
    private async Task EditAsync(ProfileDto profile)
    {
        if (profile is null || EditRequested is null) return;
        await EditRequested(profile.Id);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!_state.Connected) return;
        try
        {
            var profiles = await _api.GetAsync<List<ProfileDto>>(
                _state.Connection.ServerUrl, _state.Connection.Token, "/admin/profiles");
            Profiles.Clear();
            foreach (var p in profiles.Data ?? [])
            {
                Profiles.Add(p);
            }

            Status = $"共 {Profiles.Count} 个档案";
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
                "/admin/profiles", new { name = "新配置档案", description = string.Empty, content = (object?)null });
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = "新建失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ProfileDto profile)
    {
        if (profile is null) return;
        try
        {
            await _api.DeleteAsync(_state.Connection.ServerUrl, _state.Connection.Token,
                $"/admin/profiles/{profile.Id}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = "删除失败：" + ex.Message;
        }
    }
}
