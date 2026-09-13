using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlHub.Desktop.Services;

namespace ControlHub.Desktop.ViewModels;

/// <summary>连接 / 登录页 ViewModel。</summary>
public sealed partial class ConnectViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly ApiClient _api;

    [ObservableProperty]
    private string serverUrl;

    [ObservableProperty]
    private string username;

    [ObservableProperty]
    private string password;

    [ObservableProperty]
    private string statusText = "输入服务器地址与账号后登录。";

    public ConnectViewModel(AppState state, ApiClient api)
    {
        _state = state;
        _api = api;
        serverUrl = state.Connection.ServerUrl;
        username = state.Connection.Username;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        try
        {
            StatusText = "正在连接…";
            var url = ServerUrl.Trim();
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                url = "http://" + url;
            }

            var token = await _api.LoginAsync(url, Username, Password);
            if (string.IsNullOrWhiteSpace(token))
            {
                StatusText = "登录失败：服务器未返回令牌。";
                return;
            }

            _state.Connection.ServerUrl = url;
            _state.Connection.Username = Username;
            _state.Connection.Token = token;
            _state.Save();
            StatusText = "登录成功。";
        }
        catch (Exception ex)
        {
            StatusText = "登录失败：" + ex.Message;
        }
    }
}

