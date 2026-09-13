using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ControlHub.Desktop.Services;

/// <summary>连接配置（本地持久化）。</summary>
public sealed class ConnectionConfig
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:29800";
    public string Username { get; set; } = "admin";
    public string Token { get; set; } = string.Empty;
    public bool AllowInsecureTls { get; set; } = true;
}

/// <summary>
/// 应用全局状态：连接配置、登录令牌、主题偏好、当前页面。
/// <para>连接配置与令牌持久化到本机 %AppData%/ClassislandControlHub/desktop.json（本地文件，非业务数据）。</para>
/// </summary>
public sealed partial class AppState : ObservableObject
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassislandControlHub");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "desktop.json");

    [ObservableProperty]
    private ConnectionConfig connection = Load();

    [ObservableProperty]
    private string page = "connect";

    [ObservableProperty]
    private string theme = "system"; // system | light | dark

    /// <summary>当前登录令牌（未持久化到磁盘时为空）。</summary>
    public string? Token => string.IsNullOrWhiteSpace(Connection.Token) ? null : Connection.Token;

    /// <summary>是否已连接（持有令牌）。</summary>
    public bool Connected => !string.IsNullOrWhiteSpace(Connection.Token);

    /// <summary>保存连接配置到本地文件。</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Connection));
        }
        catch
        {
            // 忽略：本地配置写入失败不影响运行。
        }
    }

    private static ConnectionConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                return JsonSerializer.Deserialize<ConnectionConfig>(File.ReadAllText(ConfigPath)) ?? new ConnectionConfig();
            }
        }
        catch
        {
            // 忽略损坏的配置。
        }

        return new ConnectionConfig();
    }
}
