using System.Diagnostics;
using System.Net.NetworkInformation;
using System.ServiceProcess;

namespace ControlHub.Shell.Services;

/// <summary>A 端当前由谁托管。</summary>
internal enum ServerState
{
    /// <summary>未运行。</summary>
    Stopped,

    /// <summary>启动中。</summary>
    Starting,

    /// <summary>由外壳启动并托管。</summary>
    Running,

    /// <summary>已有实例（Windows 服务或别的进程）在跑，外壳只复用不托管。</summary>
    ManagedExternally,

    /// <summary>连接的是远程服务器。</summary>
    Remote,

    /// <summary>正在停止。</summary>
    Stopping,

    /// <summary>启动失败。</summary>
    Failed,
}

/// <summary>
/// 外壳的大脑：决定「该怎么启动」，并在退出时按需停掉它启动的进程。
/// <para>
/// 决策顺序：远程模式 → Windows 服务 → 端口已被占用（复用）→ 本地进程。
/// 只有最后一种情况外壳才负责停止服务；服务由 SCM 管，别人的进程更不该动。
/// </para>
/// </summary>
internal sealed class ServerLifecycleService(ShellOptions options)
{
    private const string ServiceName = "ClassislandControlHub";

    private readonly ShellOptions _options = options;
    private Process? _process;
    private ServerLocation? _location;

    /// <summary>当前状态。</summary>
    public ServerState State { get; private set; } = ServerState.Stopped;

    /// <summary>A 端基础地址。</summary>
    public string BaseUrl { get; private set; } = $"http://127.0.0.1:{options.Port}";

    /// <summary>A 端版本号（健康检查得到）。</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>失败原因。</summary>
    public string? Error { get; private set; }

    /// <summary>本地外壳令牌（读自数据目录；为空表示拿不到，将退化为需要登录）。</summary>
    public string? LocalShellToken { get; private set; }

    /// <summary>状态变化通知。</summary>
    public event Action? Changed;

    /// <summary>WebView 首次导航用的地址：带令牌时会自动免登录。</summary>
    public string EntryUrl => string.IsNullOrEmpty(LocalShellToken)
        ? $"{BaseUrl}/"
        : $"{BaseUrl}/api/v1/local-shell?shellToken={Uri.EscapeDataString(LocalShellToken)}";

    /// <summary>启动或复用 A 端。</summary>
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        Error = null;
        SetState(ServerState.Starting);

        // ① 远程模式：不碰本地进程。
        if (!string.IsNullOrWhiteSpace(_options.ServerUrl))
        {
            BaseUrl = _options.ServerUrl!;
            var remote = await HealthProbe.WaitAsync(BaseUrl, TimeSpan.FromSeconds(15), cancellationToken);
            if (!remote.Healthy)
            {
                return Fail($"无法连接远程服务器：{remote.Error}");
            }

            Version = remote.Version;
            LocalShellToken = null; // 远程连接没有本地令牌，走正常登录
            SetState(ServerState.Remote);
            ShellLog.Info($"已连接远程服务器 {BaseUrl}（版本 {Version}）。");
            return true;
        }

        // ② Windows 服务：交给 SCM，外壳只负责启动与复用。
        if (OperatingSystem.IsWindows() && TryFindService(out var service))
        {
            return await UseWindowsServiceAsync(service, cancellationToken);
        }

        // ③ 端口已被占用：判定为已有实例，直接复用，避免重复启动。
        if (IsPortInUse(_options.Port))
        {
            _location = ServerLocator.Locate(_options.ServerPath, _options.DataDirectory);
            var reused = await HealthProbe.WaitAsync(BaseUrl, TimeSpan.FromSeconds(15), cancellationToken);
            if (reused.Healthy)
            {
                Version = reused.Version;
                LocalShellToken = TryReadToken(_location);
                SetState(ServerState.ManagedExternally);
                ShellLog.Info($"端口 {_options.Port} 已有实例，直接复用（版本 {Version}）。");
                return true;
            }

            return Fail($"端口 {_options.Port} 被占用，但它不是集控服务（健康检查失败：{reused.Error}）。");
        }

        // ④ 本地进程模式：由外壳启动，也由外壳负责停止。
        _location = ServerLocator.Locate(_options.ServerPath, _options.DataDirectory);
        if (_location is null)
        {
            return Fail("未找到 A 端程序（ControlHub.Server.exe / .dll）。请用 --server-path 指定目录。");
        }

        if (!StartProcess(_location, out var startError))
        {
            return Fail(startError ?? "启动 A 端进程失败。");
        }

        var health = await HealthProbe.WaitAsync(BaseUrl, TimeSpan.FromSeconds(40), cancellationToken,
            () => _process?.HasExited ?? true);
        if (!health.Healthy)
        {
            if (_process?.HasExited == true)
            {
                return Fail($"A 端进程启动后立即退出（退出码 {_process.ExitCode}）。"
                            + $"最常见原因是缺少 .NET 10 运行时——可运行 --check 查看；日志：{ShellLog.Path}");
            }

            return Fail($"A 端启动后未在 40 秒内就绪（{health.Error}）。详见日志：{ShellLog.Path}");
        }

        Version = health.Version;
        LocalShellToken = TryReadToken(_location);
        SetState(ServerState.Running);
        ShellLog.Info($"A 端已就绪（版本 {Version}，端口 {_options.Port}，数据目录 {_location.DataDirectory}）。");

        if (string.IsNullOrEmpty(LocalShellToken))
        {
            ShellLog.Warn("未读到本地外壳令牌，将以正常登录页进入（不影响使用）。");
        }

        return true;
    }

    /// <summary>停止 A 端（仅当它由外壳启动时）。</summary>
    public async Task StopAsync()
    {
        if (State is ServerState.Remote or ServerState.ManagedExternally)
        {
            ShellLog.Info("A 端不是由外壳启动的，退出时不停它。");
            return;
        }

        if (_process is null || _process.HasExited)
        {
            _process?.Dispose();
            _process = null;
            SetState(ServerState.Stopped);
            return;
        }

        SetState(ServerState.Stopping);

        // ① 先请求优雅退出（仅回环 + 外壳令牌有效）。
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v1/local-shell/shutdown");
            if (!string.IsNullOrEmpty(LocalShellToken))
            {
                request.Headers.Add("X-Hub-Local-Shell", LocalShellToken);
            }

            await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            ShellLog.Warn($"优雅停机请求失败（将退回强制结束）：{ex.Message}");
        }

        // ② 等它自己退出，超时再强杀整棵进程树。
        try
        {
            _process.WaitForExit(10000);
        }
        catch
        {
            // 进程可能已经退出。
        }

        if (!_process.HasExited)
        {
            ShellLog.Warn("A 端未在 10 秒内退出，强制结束进程树。");
            try
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                ShellLog.Error("强制结束 A 端进程失败", ex);
            }
        }

        _process.Dispose();
        _process = null;
        SetState(ServerState.Stopped);
        ShellLog.Info("A 端已停止。");

        // 端口释放需要一点时间，避免立刻重启时误判「已被占用」。
        for (var i = 0; i < 20 && IsPortInUse(_options.Port); i++)
        {
            await Task.Delay(200);
        }
    }

    /// <summary>重启（仅供外壳托管的进程模式使用）。</summary>
    public async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
    {
        if (State is ServerState.Remote)
        {
            return await StartAsync(cancellationToken);
        }

        if (State is ServerState.ManagedExternally)
        {
            ShellLog.Warn("A 端由服务或其它进程托管，外壳不重启它。");
            return true;
        }

        await StopAsync();
        return await StartAsync(cancellationToken);
    }

    private bool StartProcess(ServerLocation location, out string? error)
    {
        error = null;
        try
        {
            var startInfo = new ProcessStartInfo(location.Executable)
            {
                WorkingDirectory = location.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (!string.IsNullOrEmpty(location.ExpandedArguments))
            {
                startInfo.Arguments = location.ExpandedArguments;
            }

            // 外壳托管时只监听回环：局域网访问不到，本地信任也不会误伤局域网用户。
            startInfo.Environment["ControlHub__BindAddress"] = "127.0.0.1";
            startInfo.Environment["ControlHub__HttpPort"] = _options.Port.ToString();
            if (!string.IsNullOrWhiteSpace(_options.DataDirectory))
            {
                startInfo.Environment["ControlHub__DataDirectory"] = _options.DataDirectory!;
            }

            // apphost 只按系统默认位置找运行时；把 .NET 装在用户目录时必须显式指路（否则报「No frameworks were found」）。
            var dotnetRoot = RuntimeLocator.FindDotnetRoot();
            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                startInfo.Environment["DOTNET_ROOT"] = dotnetRoot;
                if (OperatingSystem.IsWindows())
                {
                    startInfo.Environment["DOTNET_ROOT_X64"] = dotnetRoot;
                }

                // dll 模式（Linux，或发布时未生成 apphost）下用找到的 dotnet，而不是依赖 PATH。
                if (string.Equals(Path.GetFileNameWithoutExtension(location.Executable), "dotnet",
                        StringComparison.OrdinalIgnoreCase))
                {
                    startInfo.FileName = Path.Combine(dotnetRoot,
                        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                }
            }
            else
            {
                ShellLog.Warn("未找到 .NET 运行时目录；若 A 端起不来，请先安装 .NET 10 运行时。");
            }

            var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    ShellLog.Info($"[A 端] {e.Data}");
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    ShellLog.Warn($"[A 端] {e.Data}");
                }
            };
            process.Exited += (_, _) => ShellLog.Warn($"A 端进程已退出（代码 {process.ExitCode}）。");

            if (!process.Start())
            {
                error = "进程启动失败。";
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            ShellLog.Info($"已启动 A 端：{location.Executable} {location.ExpandedArguments}（工作目录 {location.WorkingDirectory}）");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.Message}（若提示找不到 dotnet，请先安装 .NET 10 运行时）";
            ShellLog.Error("启动 A 端进程失败", ex);
            return false;
        }
    }

    /// <summary>读取本地外壳令牌（A 端启动时生成在数据目录里）。</summary>
    private static string? TryReadToken(ServerLocation? location)
    {
        if (location is null)
        {
            return null;
        }

        var path = Path.Combine(location.DataDirectory, "local-shell.token");
        for (var i = 0; i < 10; i++)
        {
            try
            {
                if (File.Exists(path))
                {
                    var token = File.ReadAllText(path).Trim();
                    if (token.Length >= 32)
                    {
                        return token;
                    }
                }
            }
            catch
            {
                // 文件可能正被写入，稍后重试。
            }

            Thread.Sleep(200);
        }

        return null;
    }

    /// <summary>启动或复用已安装的 Windows 服务（服务由 SCM 管理，外壳不负责停止它）。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task<bool> UseWindowsServiceAsync(ServiceController service, CancellationToken cancellationToken)
    {
        try
        {
            if (service.Status != ServiceControllerStatus.Running)
            {
                ShellLog.Info("检测到 Windows 服务，正在启动…");
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }

            var probe = await HealthProbe.WaitAsync(BaseUrl, TimeSpan.FromSeconds(20), cancellationToken);
            Version = probe.Version;
            LocalShellToken = TryReadToken(null);
            SetState(ServerState.ManagedExternally);
            ShellLog.Info($"已复用 Windows 服务（版本 {Version}）。");
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"启动 Windows 服务失败：{ex.Message}");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TryFindService(out ServiceController service)
    {
        service = null!;
        try
        {
            var controller = new ServiceController(ServiceName);
            _ = controller.Status; // 服务不存在时这里会抛 InvalidOperationException
            service = controller;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }

    private bool Fail(string message)
    {
        Error = message;
        SetState(ServerState.Failed);
        ShellLog.Error(message);
        return false;
    }

    private void SetState(ServerState state)
    {
        State = state;
        Changed?.Invoke();
    }
}
