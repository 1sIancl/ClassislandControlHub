namespace ControlHub.Shell;

/// <summary>
/// 外壳启动参数。
/// <para>
/// 外壳有三件事：管进程、装 WebView、打通本地信任；它<b>不做业务</b>——
/// 课表、设备、远程管理这些界面全部复用 A 端已有的 Web 界面。
/// </para>
/// </summary>
internal sealed record ShellOptions
{
    /// <summary>连远程服务器时使用（<c>--server</c>）：此时不启动本地进程。</summary>
    public string? ServerUrl { get; init; }

    /// <summary>A 端程序目录或可执行文件路径（<c>--server-path</c>）。默认在外壳目录及其 server 子目录里找。</summary>
    public string? ServerPath { get; init; }

    /// <summary>传给 A 端的数据目录（<c>--data-dir</c>），用于与 Windows 服务模式共用同一份数据。</summary>
    public string? DataDirectory { get; init; }

    /// <summary>A 端 HTTP 端口，默认 29800。</summary>
    public int Port { get; init; } = 29800;

    /// <summary>退出外壳时是否停止 A 端进程（进程模式下默认停止；服务模式不受此影响）。</summary>
    public bool StopServerOnExit { get; init; } = true;

    /// <summary>关闭窗口时最小化到托盘而不是退出。</summary>
    public bool MinimizeToTrayOnClose { get; init; } = true;

    /// <summary>只做环境自检并退出（<c>--check</c>）。</summary>
    public bool Check { get; init; }

    /// <summary>无界面跑一遍「启动 → 健康检查 → 免登录 → 停止」并退出（<c>--smoke 秒</c>），用于自动化验证。</summary>
    public int SmokeSeconds { get; init; }

    /// <summary>解析参数；失败返回 <c>null</c>。</summary>
    public static ShellOptions? Parse(string[] args)
    {
        var result = new ShellOptions();

        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (args[i])
            {
                case "--server":
                    var url = Next();
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        return null;
                    }

                    result = result with { ServerUrl = url.TrimEnd('/') };
                    break;
                case "--server-path":
                    result = result with { ServerPath = Next() };
                    break;
                case "--data-dir":
                    result = result with { DataDirectory = Next() };
                    break;
                case "--port":
                    if (!int.TryParse(Next(), out var port) || port is <= 0 or > 65535)
                    {
                        return null;
                    }

                    result = result with { Port = port };
                    break;
                case "--no-stop-on-exit":
                    result = result with { StopServerOnExit = false };
                    break;
                case "--exit-on-close":
                    result = result with { MinimizeToTrayOnClose = false };
                    break;
                case "--check":
                    result = result with { Check = true };
                    break;
                case "--smoke":
                    if (!int.TryParse(Next(), out var seconds) || seconds <= 0)
                    {
                        return null;
                    }

                    result = result with { SmokeSeconds = seconds };
                    break;
                case "-h":
                case "--help":
                    return null;
                default:
                    Console.Error.WriteLine($"未知参数：{args[i]}");
                    return null;
            }
        }

        return result;
    }

    /// <summary>打印用法。</summary>
    public static void PrintUsage()
    {
        Console.WriteLine("""
            ClassislandControlHub 本地外壳：启动 A 端、内嵌 Web 管理界面、免登录进入。

            用法：
              ControlHub.Shell.exe [选项]

            选项：
              --server <地址>       连接远程服务器（不启动本地进程），例如 http://192.168.1.5:29800
              --server-path <路径>  指定 A 端目录或可执行文件；默认在外壳目录与 server/ 子目录里查找
              --data-dir <目录>     传给 A 端的数据目录（与服务模式共用数据时使用）
              --port <端口>         A 端 HTTP 端口，默认 29800
              --no-stop-on-exit     退出外壳时不停掉 A 端进程
              --exit-on-close       关闭窗口时直接退出（默认最小化到托盘）
              --check               只做环境自检（A 端位置、端口占用、WebView 运行时）后退出
              --smoke <秒>          无界面跑一遍启动/健康检查/免登录/停止，用于自动化验证
              -h, --help            显示本帮助
            """);
    }
}
