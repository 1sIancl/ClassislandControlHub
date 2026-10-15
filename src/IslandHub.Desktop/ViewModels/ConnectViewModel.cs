using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IslandHub.Desktop.Services;

namespace IslandHub.Desktop.ViewModels;

/// <summary>连接 / 登录页 ViewModel。</summary>
public sealed partial class ConnectViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly ApiClient _api;

    [ObservableProperty]
    private string serverUrl;

    [ObservableProperty]
    private string username;

    // 密码刻意**不从磁盘回填**（`AppState` 只存服务器地址与账号），所以这里必须是空串初值——
    // 否则退出构造函数时字段为 null，编译器按可空引用类型报 CS8618。
    [ObservableProperty]
    private string password = string.Empty;

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

