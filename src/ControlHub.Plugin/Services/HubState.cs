using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ControlHub.Plugin.Services;

/// <summary>连接状态。</summary>
public enum HubConnectionStatus
{
    /// <summary>未配置服务器。</summary>
    NotConfigured,

    /// <summary>正在连接/注册。</summary>
    Connecting,

    /// <summary>已连接。</summary>
    Connected,

    /// <summary>连接失败或已断开。</summary>
    Disconnected,

    /// <summary>设备被服务器停用。</summary>
    Revoked,
}

/// <summary>
/// 插件运行状态。供设置页面实时展示连接情况、同步进度与最近日志。
/// </summary>
public sealed class HubState : INotifyPropertyChanged
{
    private HubConnectionStatus _status = HubConnectionStatus.NotConfigured;
    private string _serverName = string.Empty;
    private string _serverUrl = string.Empty;
    private string _profileName = string.Empty;
    private long _revision;
    private DateTimeOffset? _lastSyncAt;
    private string _lastError = string.Empty;
    private string? _announcement;
    private string _currentClassPlanName = string.Empty;
    private bool _respectLock;
    private DateTimeOffset? _lastTimeSyncAt;
    private string _lastTimeSyncMessage = string.Empty;

    /// <summary>最近日志（保留最近 200 条）。</summary>
    public ObservableCollection<LogLine> Logs { get; } = [];

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>连接状态。</summary>
    public HubConnectionStatus Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    /// <summary>服务器名称（注册成功后由服务器返回）。</summary>
    public string ServerName
    {
        get => _serverName;
        set => Set(ref _serverName, value);
    }

    /// <summary>当前服务器地址。</summary>
    public string ServerUrl
    {
        get => _serverUrl;
        set => Set(ref _serverUrl, value);
    }

    /// <summary>当前生效的配置档案名称。</summary>
    public string ProfileName
    {
        get => _profileName;
        set => Set(ref _profileName, value);
    }

    /// <summary>已应用的配置版本号。</summary>
    public long Revision
    {
        get => _revision;
        set => Set(ref _revision, value);
    }

    /// <summary>最近一次成功同步时间。</summary>
    public DateTimeOffset? LastSyncAt
    {
        get => _lastSyncAt;
        set => Set(ref _lastSyncAt, value);
    }

    /// <summary>最近一次错误信息。</summary>
    public string LastError
    {
        get => _lastError;
        set => Set(ref _lastError, value);
    }

    /// <summary>服务器下发的公告。</summary>
    public string? Announcement
    {
        get => _announcement;
        set => Set(ref _announcement, value);
    }

    /// <summary>当前正在显示的课表名称。</summary>
    public string CurrentClassPlanName
    {
        get => _currentClassPlanName;
        set => Set(ref _currentClassPlanName, value);
    }

    /// <summary>服务器是否要求锁定本地编辑。</summary>
    public bool RespectLock
    {
        get => _respectLock;
        set => Set(ref _respectLock, value);
    }

    /// <summary>最近一次时钟同步的时间。</summary>
    public DateTimeOffset? LastTimeSyncAt
    {
        get => _lastTimeSyncAt;
        set => Set(ref _lastTimeSyncAt, value);
    }

    /// <summary>最近一次时钟同步的结果描述。</summary>
    public string LastTimeSyncMessage
    {
        get => _lastTimeSyncMessage;
        set => Set(ref _lastTimeSyncMessage, value);
    }

    /// <summary>追加一条日志。</summary>
    public void Log(string level, string message)
    {
        var line = new LogLine
        {
            Timestamp = DateTimeOffset.Now,
            Level = level,
            Message = message,
        };

        Logs.Add(line);
        while (Logs.Count > 200)
        {
            Logs.RemoveAt(0);
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Logs)));
    }

    /// <summary>状态栏可读文本。</summary>
    public string StatusText => Status switch
    {
        HubConnectionStatus.NotConfigured => "未配置服务器",
        HubConnectionStatus.Connecting => "连接中…",
        HubConnectionStatus.Connected => "已连接",
        HubConnectionStatus.Disconnected => "已断开",
        HubConnectionStatus.Revoked => "设备已被停用",
        _ => string.Empty,
    };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(Status))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }

        return true;
    }
}

/// <summary>一条日志。</summary>
public sealed class LogLine
{
    /// <summary>时间。</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>级别：info / warn / error / debug。</summary>
    public string Level { get; init; } = "info";

    /// <summary>内容。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>面向界面的格式化文本。</summary>
    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss}] [{Level.ToUpperInvariant()}] {Message}";
}
