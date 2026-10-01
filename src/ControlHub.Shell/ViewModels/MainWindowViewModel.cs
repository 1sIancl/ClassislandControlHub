using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlHub.Shell.Services;

namespace ControlHub.Shell.ViewModels;

/// <summary>
/// 主窗口视图模型：把 <see cref="ServerLifecycleService"/> 的状态映射成界面文案与命令。
/// <para>外壳不承载任何业务逻辑，界面全部来自 A 端 Web UI。</para>
/// </summary>
internal sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ShellOptions _options;
    private readonly ServerLifecycleService _lifecycle;

    private string _statusText = "正在准备…";
    private string _versionText = "版本 —";
    private string _addressText = string.Empty;
    private string _accessText = string.Empty;
    private string _errorText = string.Empty;
    private bool _isBusy = true;
    private bool _isRunning;
    private string _pageTitle = "ClassislandControlHub 集控";

    public MainWindowViewModel(ShellOptions options, ServerLifecycleService lifecycle)
    {
        _options = options;
        _lifecycle = lifecycle;
        _lifecycle.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshState);

        OpenInBrowserCommand = new RelayCommand(OpenInBrowser, () => _lifecycle.State is ServerState.Running
            or ServerState.ManagedExternally or ServerState.Remote);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => !_isBusy);
        RestartServerCommand = new AsyncRelayCommand(RestartAsync, () => !_isBusy);
        StopServerCommand = new AsyncRelayCommand(StopAsync, () => !_isBusy);
        ToggleLogCommand = new RelayCommand(() => IsLogVisible = !IsLogVisible);
        ClearLogCommand = new RelayCommand(ClearLog);

        foreach (var line in ShellLog.RecentLines)
        {
            AppendLog(line);
        }

        ShellLog.LineWritten += line => Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendLog(line));
    }

    /// <summary>请求把 WebView 导航到指定地址。</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>请求把窗口带回前台。</summary>
    public event Action? ShowRequested;

    /// <summary>有新日志写入（界面据此把日志面板滚到底部）。</summary>
    public event Action? LogAppended;

    public RelayCommand OpenInBrowserCommand { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    public AsyncRelayCommand RestartServerCommand { get; }

    public AsyncRelayCommand StopServerCommand { get; }

    /// <summary>显示 / 隐藏日志面板。</summary>
    public RelayCommand ToggleLogCommand { get; }

    /// <summary>清空日志面板（只清界面，不动日志文件）。</summary>
    public RelayCommand ClearLogCommand { get; }

    /// <summary>窗口标题。</summary>
    public string PageTitle
    {
        get => _pageTitle;
        private set => SetProperty(ref _pageTitle, value);
    }

    /// <summary>工具条上的状态文案。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>状态栏：A 端版本。</summary>
    public string VersionText
    {
        get => _versionText;
        private set => SetProperty(ref _versionText, value);
    }

    /// <summary>状态栏：A 端地址。</summary>
    public string AddressText
    {
        get => _addressText;
        private set => SetProperty(ref _addressText, value);
    }

    /// <summary>状态栏：免登录是否可用。</summary>
    public string AccessText
    {
        get => _accessText;
        private set => SetProperty(ref _accessText, value);
    }

    /// <summary>状态栏：失败原因（正常时为空）。</summary>
    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>状态栏：日志文件位置。</summary>
    public string LogPathText => $"日志 {ShellLog.Path}";

    /// <summary>A 端是否处于可用状态（决定状态胶囊的配色）。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    /// <summary>是否正在忙（禁用按钮并显示进度条）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommands();
            }
        }
    }

    /// <summary>窗口首次显示后调用。</summary>
    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var ok = await _lifecycle.StartAsync();
            RefreshState();

            if (ok)
            {
                NavigateRequested?.Invoke(_lifecycle.EntryUrl);
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error("初始化失败", ex);
            StatusText = "启动失败";
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>重新加载页面。</summary>
    public Task ReloadAsync()
    {
        ShellLog.Info("刷新页面。");
        NavigateRequested?.Invoke(_lifecycle.EntryUrl);
        return Task.CompletedTask;
    }

    /// <summary>重启 A 端并重新进入（服务模式与远程模式下只刷新页面）。</summary>
    public async Task RestartAsync()
    {
        ShellLog.Info("重启 A 端。");
        IsBusy = true;
        try
        {
            if (await _lifecycle.RestartAsync())
            {
                NavigateRequested?.Invoke(_lifecycle.EntryUrl);
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error("重启失败", ex);
        }
        finally
        {
            IsBusy = false;
            RefreshState();
        }
    }

    /// <summary>停止 A 端（仅进程模式有效）。</summary>
    public async Task StopAsync()
    {
        IsBusy = true;
        try
        {
            await _lifecycle.StopAsync();
        }
        finally
        {
            IsBusy = false;
            RefreshState();
        }
    }

    /// <summary>把当前地址交给系统浏览器打开。</summary>
    public void OpenInBrowser()
    {
        try
        {
            // 带令牌的引导地址只用于 WebView 首次导航，这里给用户一个干净的地址，避免令牌出现在浏览器历史里。
            Process.Start(new ProcessStartInfo(_lifecycle.BaseUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShellLog.Error("打开浏览器失败", ex);
        }
    }

    /// <summary>把窗口带回前台（托盘点击）。</summary>
    public void RequestShow() => ShowRequested?.Invoke();

    // ────────────────────────────── 日志面板 ──────────────────────────────

    private readonly ObservableCollection<ShellLogLine> _logLines = [];
    private bool _isLogVisible;

    /// <summary>日志面板内容（按级别着色）。</summary>
    public ObservableCollection<ShellLogLine> LogLines => _logLines;

    /// <summary>日志面板是否展开。</summary>
    public bool IsLogVisible
    {
        get => _isLogVisible;
        set => SetProperty(ref _isLogVisible, value);
    }

    private void AppendLog(string line)
    {
        _logLines.Add(new ShellLogLine(line));
        while (_logLines.Count > 400)
        {
            _logLines.RemoveAt(0);
        }

        LogAppended?.Invoke();
    }

    private void ClearLog() => _logLines.Clear();

    /// <summary>
    /// 处理来自 Web 页面的消息（WebMessage 通道）。
    /// <para>页面里发 <c>{"action":"restart" | "stop" | "open-browser"}</c> 即可控制外壳。</para>
    /// </summary>
    public void HandleShellMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        string action;
        try
        {
            using var document = JsonDocument.Parse(body);
            action = document.RootElement.TryGetProperty("action", out var value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            ShellLog.Warn($"忽略无法解析的页面消息：{body}");
            return;
        }

        ShellLog.Info($"收到页面消息：{action}");
        switch (action)
        {
            case "restart":
                _ = RestartAsync();
                break;
            case "stop":
                _ = StopAsync();
                break;
            case "open-browser":
                OpenInBrowser();
                break;
            default:
                ShellLog.Warn($"未知的页面命令：{action}");
                break;
        }
    }

    private void RefreshState()
    {
        StatusText = _lifecycle.State switch
        {
            ServerState.Stopped => "A 端未运行",
            ServerState.Starting => "正在启动 A 端…",
            ServerState.Running => $"A 端运行中（由外壳托管，端口 {_options.Port}）",
            ServerState.ManagedExternally => "已复用已有的 A 端实例（外壳不管它的启停）",
            ServerState.Remote => $"已连接远程服务器 {_lifecycle.BaseUrl}",
            ServerState.Stopping => "正在停止 A 端…",
            ServerState.Failed => "启动失败",
            _ => "未知状态",
        };

        VersionText = string.IsNullOrEmpty(_lifecycle.Version) ? "版本 —" : $"版本 {_lifecycle.Version}";
        AddressText = _lifecycle.State is ServerState.Running or ServerState.ManagedExternally or ServerState.Remote
            ? _lifecycle.BaseUrl
            : string.Empty;
        AccessText = _lifecycle.State switch
        {
            ServerState.Running or ServerState.ManagedExternally => string.IsNullOrEmpty(_lifecycle.LocalShellToken)
                ? "免登录不可用（缺少外壳令牌，将显示登录页）"
                : "免登录已启用",
            ServerState.Remote => "免登录不适用（远程连接需登录）",
            _ => string.Empty,
        };
        ErrorText = _lifecycle.Error ?? string.Empty;
        IsRunning = _lifecycle.State is ServerState.Running or ServerState.ManagedExternally or ServerState.Remote;

        PageTitle = _lifecycle.State switch
        {
            ServerState.Remote => $"ClassislandControlHub 集控 · {_lifecycle.BaseUrl}",
            ServerState.Failed => "ClassislandControlHub 集控 · 启动失败",
            _ => "ClassislandControlHub 集控",
        };

        IsBusy = _lifecycle.State is ServerState.Starting or ServerState.Stopping;
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OpenInBrowserCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
        RestartServerCommand.NotifyCanExecuteChanged();
        StopServerCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>日志面板里的一行：保留级别标记，供界面着色。</summary>
public sealed class ShellLogLine(string text)
{
    /// <summary>整行文本（含时间戳与级别）。</summary>
    public string Text { get; } = text;

    /// <summary>是否为告警行。</summary>
    public bool IsWarn { get; } = text.Contains("[WARN]", StringComparison.Ordinal);

    /// <summary>是否为错误行。</summary>
    public bool IsError { get; } = text.Contains("[ERROR]", StringComparison.Ordinal);
}
