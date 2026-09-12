using System.Text.Json;
using ControlHub.Protocol;

namespace ControlHub.Plugin.Models;

/// <summary>
/// 插件设置的读写封装。
/// <para>
/// 设置以 JSON 形式持久化在插件配置目录的 <c>settings.json</c> 中。
/// 这里没有使用 ClassIsland 的配置辅助类，而是直接使用共享序列化约定，
/// 保证两端数据格式一致、无额外依赖。
/// </para>
/// </summary>
public sealed class HubSettingsStore
{
    private readonly Func<string> _configFolder;
    private readonly object _gate = new();
    private PluginSettings? _settings;

    /// <summary>创建设置存储。</summary>
    /// <param name="configFolderResolver">返回插件配置目录的委托（延迟求值）。</param>
    public HubSettingsStore(Func<string> configFolderResolver)
    {
        _configFolder = configFolderResolver;
    }

    /// <summary>配置文件绝对路径。</summary>
    public string SettingsPath => Path.Combine(_configFolder(), "settings.json");

    /// <summary>读取设置（首次访问时从磁盘加载，之后使用内存缓存）。</summary>
    public PluginSettings Load()
    {
        lock (_gate)
        {
            if (_settings is not null)
            {
                return _settings;
            }

            var path = SettingsPath;
            if (File.Exists(path))
            {
                _settings = HubJson.DeserializeOrDefault(File.ReadAllText(path), new PluginSettings());
            }
            else
            {
                _settings = new PluginSettings();
            }

            EnsureDeviceId(_settings);
            return _settings;
        }
    }

    /// <summary>把当前设置写回磁盘（原子替换，避免写入中断导致文件损坏）。</summary>
    public void Save()
    {
        lock (_gate)
        {
            if (_settings is null)
            {
                return;
            }

            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temp = path + ".tmp";
            File.WriteAllText(temp, HubJson.Serialize(_settings, indented: true));
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>
    /// 在 <paramref name="mutator"/> 内修改设置并持久化，返回修改后的设置。
    /// 保证修改与落盘处于同一把锁内，避免多线程竞态。
    /// </summary>
    public PluginSettings Update(Action<PluginSettings> mutator)
    {
        lock (_gate)
        {
            var settings = Load();
            mutator(settings);
            EnsureDeviceId(settings);
            Save();
            return settings;
        }
    }

    /// <summary>确保设备标识存在（首次运行生成一次并持久化）。</summary>
    private static void EnsureDeviceId(PluginSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DeviceId))
        {
            settings.DeviceId = $"ci-{Guid.NewGuid():D}";
        }
    }
}
