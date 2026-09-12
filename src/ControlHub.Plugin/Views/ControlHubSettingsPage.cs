using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using ControlHub.Plugin.Models;
using ControlHub.Plugin.Services;

namespace ControlHub.Plugin.Views;

/// <summary>
/// 集控客户端设置页面。
/// <para>
/// 使用代码构建界面（而非 XAML），以降低对 Avalonia XAML 编译链的依赖，
/// 同时确保在不同 ClassIsland 主题下都能清晰呈现。
/// 页面提供：服务器地址配置、自动发现、注册码填写、同步分区开关、手动同步与运行日志。
/// </para>
/// </summary>
[SettingsPageInfo("controlhub.client.settings", "集控客户端", SettingsPageCategory.External)]
public partial class ControlHubSettingsPage : SettingsPageBase
{
    private readonly HubSettingsStore _settings;
    private readonly HubState _state;
    private readonly SyncEngine _engine;
    private readonly ServerDiscovery _discovery;
    private readonly TimeSyncService _timeSync;

    private TextBox _serverUrlBox = null!;
    private TextBox _enrollCodeBox = null!;
    private TextBox _deviceNameBox = null!;
    private CheckBox _applyTimeLayouts = null!;
    private CheckBox _applyClassPlans = null!;
    private CheckBox _applySubjects = null!;
    private CheckBox _applySettings = null!;
    private CheckBox _autoSync = null!;
    private CheckBox _allowInsecureTls = null!;
    private CheckBox _enableTimeSync = null!;
    private TextBlock _timeSyncText = null!;

    private TextBlock _statusText = null!;
    private TextBlock _statusDetail = null!;
    private TextBlock _profileText = null!;
    private TextBlock _revisionText = null!;
    private TextBlock _lastSyncText = null!;
    private Border _announcementBanner = null!;
    private TextBlock _announcementText = null!;
    private StackPanel _logPanel = null!;

    public ControlHubSettingsPage(
        HubSettingsStore settings,
        HubState state,
        SyncEngine engine,
        ServerDiscovery discovery,
        TimeSyncService timeSync)
    {
        _settings = settings;
        _state = state;
        _engine = engine;
        _discovery = discovery;
        _timeSync = timeSync;

        BuildUi();
        LoadFromSettings();
        RefreshStatus();

        _state.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RefreshStatus();
            RefreshLogs();
        });

        Dispatcher.UIThread.Post(RefreshLogs);
    }

    // ────────────────────────────── UI 构建 ──────────────────────────────

    private void BuildUi()
    {
        var root = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 14, Margin = new Thickness(4) },
        };

        var stack = (StackPanel)root.Content;
        stack.Children.Add(BuildStatusCard());
        stack.Children.Add(BuildServerCard());
        stack.Children.Add(BuildSyncCard());
        stack.Children.Add(BuildTimeSyncCard());
        stack.Children.Add(BuildLogCard());

        Content = root;
    }

    private Control BuildStatusCard()
    {
        _statusText = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, Text = "未配置" };
        _statusDetail = new TextBlock { Foreground = Dim(), TextWrapping = TextWrapping.Wrap, Text = "请在下方填写服务器地址与注册码。" };
        _profileText = new TextBlock { Foreground = Dim() };
        _revisionText = new TextBlock { Foreground = Dim() };
        _lastSyncText = new TextBlock { Foreground = Dim() };

        _announcementText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _announcementBanner = new Border
        {
            Background = AccentSoft(),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9),
            Child = _announcementText,
            IsVisible = false,
        };

        return new Border
        {
            Background = Panel(),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Child = new StackPanel { Spacing = 8, Children = { _statusText, _statusDetail, _announcementBanner, _profileText, _revisionText, _lastSyncText } },
        };
    }

    private Control BuildServerCard()
    {
        _serverUrlBox = new TextBox { PlaceholderText = "http://192.168.1.5:29800" };
        _enrollCodeBox = new TextBox { PlaceholderText = "注册码，例如 8 位大写字母数字" };
        _deviceNameBox = new TextBox { PlaceholderText = "留空则使用本机机器名，例如 高一(3)班" };

        var discoverButton = new Button { Content = "自动发现服务器" };
        discoverButton.Click += async (_, _) =>
        {
            discoverButton.IsEnabled = false;
            discoverButton.Content = "正在搜索…";
            try
            {
                var found = await _discovery.DiscoverAsync();
                if (found.Count == 0)
                {
                    _state.Log("warn", "局域网内未发现集控服务器，请确认服务器已开启自动发现，或手动填写地址。");
                }
                else if (found.Count == 1)
                {
                    _serverUrlBox.Text = found[0].BaseUrl;
                    _state.Log("info", $"已发现服务器：{found[0].ServerName}（{found[0].BaseUrl}）");
                }
                else
                {
                    _serverUrlBox.Text = found[0].BaseUrl;
                    _state.Log("info", $"发现 {found.Count} 台服务器，已选第一台（{found[0].ServerName}）。请按需调整。");
                }
            }
            finally
            {
                discoverButton.IsEnabled = true;
                discoverButton.Content = "自动发现服务器";
            }
        };

        return Section(
            "服务器连接",
            "配置集控服务器（A 端）的地址与设备注册码。",
            Label("服务器地址", _serverUrlBox),
            discoverButton,
            Label("注册码", _enrollCodeBox),
            Label("设备名称", _deviceNameBox),
            Row(
                Button("保存设置", SaveSettings),
                Button("立即同步", async () => await SyncNowAsync()),
                Button("重新注册", ResetEnrollment)
            )
        );
    }

    private Control BuildSyncCard()
    {
        _autoSync = new CheckBox { Content = "启用自动同步（接收服务器即时下发）", IsChecked = true };
        _applyTimeLayouts = new CheckBox { Content = "应用「时间表」", IsChecked = true };
        _applyClassPlans = new CheckBox { Content = "应用「课表」", IsChecked = true };
        _applySubjects = new CheckBox { Content = "应用「科目」", IsChecked = true };
        _applySettings = new CheckBox { Content = "应用「自定义设置」", IsChecked = true };
        _allowInsecureTls = new CheckBox { Content = "允许不受信任的 HTTPS 证书（自签名场景）", IsChecked = false };

        return Section(
            "同步内容",
            "选择需要从服务器接收并写入 ClassIsland 的配置分区。关闭自动同步后，仅通过「立即同步」手动拉取。",
            _autoSync,
            Row(_applyTimeLayouts, _applyClassPlans),
            Row(_applySubjects, _applySettings),
            _allowInsecureTls
        );
    }

    private Control BuildTimeSyncCard()
    {
        _enableTimeSync = new CheckBox { Content = "启用时钟同步（让 ClassIsland 以集控服务器为时间源）", IsChecked = true };
        _timeSyncText = new TextBlock { Foreground = Dim(), TextWrapping = TextWrapping.Wrap, Text = "尚未同步。" };

        return Section(
            "时间同步",
            "把 ClassIsland 的「精确时间服务器」指向集控服务器并启用精确时间，让大屏时钟以服务器为时间源——修改的是 ClassIsland 的时钟，不修改 Windows 系统时间，无需管理员权限。",
            _enableTimeSync,
            _timeSyncText,
            Row(
                Button("立即同步", async () => await SyncTimeNowAsync())
            )
        );
    }

    private Control BuildLogCard()
    {
        _logPanel = new StackPanel { Spacing = 3 };

        return Section(
            "运行日志",
            "记录插件与服务器的交互过程，便于排查连接与同步问题。",
            new ScrollViewer
            {
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _logPanel
            }
        );
    }

    // ────────────────────────────── 行为 ──────────────────────────────

    private void LoadFromSettings()
    {
        var s = _settings.Load();
        _serverUrlBox.Text = s.ServerUrl;
        _enrollCodeBox.Text = s.EnrollCode;
        _deviceNameBox.Text = s.DeviceName;
        _autoSync.IsChecked = s.AutoSync;
        _applyTimeLayouts.IsChecked = s.ApplyTimeLayouts;
        _applyClassPlans.IsChecked = s.ApplyClassPlans;
        _applySubjects.IsChecked = s.ApplySubjects;
        _applySettings.IsChecked = s.ApplySettings;
        _allowInsecureTls.IsChecked = s.AllowInsecureTls;
        _enableTimeSync.IsChecked = s.EnableTimeSync;
    }

    private void SaveSettings()
    {
        _settings.Update(s =>
        {
            s.ServerUrl = _serverUrlBox.Text?.Trim() ?? string.Empty;
            s.EnrollCode = _enrollCodeBox.Text?.Trim().ToUpperInvariant() ?? string.Empty;
            s.DeviceName = _deviceNameBox.Text?.Trim() ?? string.Empty;
            s.AutoSync = _autoSync.IsChecked ?? true;
            s.ApplyTimeLayouts = _applyTimeLayouts.IsChecked ?? true;
            s.ApplyClassPlans = _applyClassPlans.IsChecked ?? true;
            s.ApplySubjects = _applySubjects.IsChecked ?? true;
            s.ApplySettings = _applySettings.IsChecked ?? true;
            s.AllowInsecureTls = _allowInsecureTls.IsChecked ?? false;
            s.EnableTimeSync = _enableTimeSync.IsChecked ?? true;

            // 服务器地址或注册码变化后，旧令牌不再适用，需重新注册。
            if (!string.Equals(s.ServerUrl, _state.ServerUrl, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(s.ServerUrl))
            {
                s.DeviceToken = string.Empty;
            }
        });

        _state.Log("info", "设置已保存。");
        RefreshStatus();
    }

    private async Task SyncNowAsync()
    {
        SaveSettings();
        try
        {
            var result = await _engine.SyncNowAsync();
            _state.Log(result.Success ? "info" : "error", result.Message);
        }
        catch (Exception ex)
        {
            _state.LastError = ex.Message;
            _state.Log("error", ex.Message);
        }

        RefreshStatus();
    }

    private void ResetEnrollment()
    {
        _settings.Update(s => s.DeviceToken = string.Empty);
        _state.Log("info", "已清除设备凭证，将在下一次同步时重新注册。");
        RefreshStatus();
    }

    private async Task SyncTimeNowAsync()
    {
        SaveSettings();
        try
        {
            var message = await _timeSync.SyncOnceAsync();
            _state.Log("info", message);
        }
        catch (Exception ex)
        {
            _state.LastTimeSyncMessage = "时间同步失败：" + ex.Message;
            _state.Log("error", ex.Message);
        }

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var s = _settings.Load();
        var statusBrush = _state.Status switch
        {
            HubConnectionStatus.Connected => Accent(),
            HubConnectionStatus.Connecting => Accent(),
            HubConnectionStatus.Disconnected => Danger(),
            HubConnectionStatus.Revoked => Danger(),
            _ => Dim(),
        };

        _statusText.Text = _state.StatusText;
        _statusText.Foreground = statusBrush;

        var detail = _state.Status switch
        {
            HubConnectionStatus.Connected => $"已连接：{_state.ServerName}（{_state.ServerUrl}）",
            HubConnectionStatus.Revoked => "设备已被服务器停用，请联系管理员恢复后再同步。",
            HubConnectionStatus.Disconnected when !string.IsNullOrWhiteSpace(_state.LastError) => _state.LastError,
            HubConnectionStatus.Disconnected => "与服务器断开连接，正在自动重试。",
            HubConnectionStatus.Connecting => "正在连接服务器…",
            _ => "请在下方填写服务器地址与注册码，或点击「自动发现服务器」。",
        };
        _statusDetail.Text = detail;

        _profileText.Text = string.IsNullOrWhiteSpace(_state.ProfileName)
            ? "尚未同步任何配置档案。"
            : $"当前档案：{_state.ProfileName}";
        _revisionText.Text = _state.Revision > 0 ? $"配置版本：#{_state.Revision}" : string.Empty;
        _lastSyncText.Text = _state.LastSyncAt is null
            ? "尚未同步"
            : $"最近同步：{_state.LastSyncAt:yyyy-MM-dd HH:mm:ss}";

        _announcementBanner.IsVisible = !string.IsNullOrWhiteSpace(_state.Announcement);
        _announcementText.Text = _state.Announcement ?? string.Empty;

        _timeSyncText.Text = string.IsNullOrWhiteSpace(_state.LastTimeSyncMessage)
            ? "尚未同步。"
            : _state.LastTimeSyncMessage;
    }

    private void RefreshLogs()
    {
        if (_logPanel is null)
        {
            return;
        }

        _logPanel.Children.Clear();
        foreach (var line in _state.Logs.Reverse().Take(80))
        {
            var brush = line.Level switch
            {
                "error" => Danger(),
                "warn" => Warn(),
                "debug" => Dim(),
                _ => Accent(),
            };

            _logPanel.Children.Add(new TextBlock
            {
                Text = line.ToString(),
                FontSize = 12,
                Foreground = brush,
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    // ────────────────────────────── 构建辅助 ──────────────────────────────

    private static Control Section(string title, string subtitle, params Control[] children)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeight.SemiBold });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            panel.Children.Add(new TextBlock { Text = subtitle, Foreground = Dim(), TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        }

        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return new Border
        {
            Background = Panel(),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Child = panel,
        };
    }

    private static Control Label(string text, Control control) => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = text, FontSize = 12.5, Foreground = Dim() },
            control,
        },
    };

    private static Control Row(params Control[] children)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private static Button Button(string text, Action onClick)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 2, 8, 0) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Button Button(string text, Func<Task> onClick)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 2, 8, 0) };
        button.Click += async (_, _) => await onClick();
        return button;
    }

    private static IBrush Panel() => new SolidColorBrush(Color.FromRgb(28, 32, 40));
    private static IBrush Dim() => new SolidColorBrush(Color.FromRgb(150, 158, 172));
    private static IBrush Accent() => new SolidColorBrush(Color.FromRgb(91, 157, 255));
    private static IBrush AccentSoft() => new SolidColorBrush(Color.FromArgb(40, 91, 157, 255));
    private static IBrush Danger() => new SolidColorBrush(Color.FromRgb(242, 101, 122));
    private static IBrush Warn() => new SolidColorBrush(Color.FromRgb(242, 181, 68));
}
