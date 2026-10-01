namespace ControlHub.Shell.Services;

/// <summary>A 端程序的位置信息。</summary>
/// <param name="Executable">要启动的可执行文件（优先 apphost <c>.exe</c>，否则退回 <c>dotnet &lt;dll&gt;</c>）。</param>
/// <param name="WorkingDirectory">工作目录（A 端以自身目录为内容根，这里与之一致）。</param>
/// <param name="DataDirectory">数据目录（用于读取本地外壳令牌）。</param>
/// <param name="ExpandedArguments">可执行文件参数（dotnet 模式需要带上 dll 路径）。</param>
internal sealed record ServerLocation(string Executable, string WorkingDirectory, string DataDirectory,
    string ExpandedArguments);

/// <summary>在外壳目录附近查找 A 端程序。</summary>
internal static class ServerLocator
{
    /// <summary>
    /// 查找 A 端：优先 <c>--server-path</c>，其次外壳目录下的 <c>server/</c>、外壳目录本身、以及上一级目录。
    /// <para>返回 <c>null</c> 表示没找到——此时外壳会提示用户手动指定。</para>
    /// </summary>
    public static ServerLocation? Locate(string? serverPath, string? dataDirectoryOverride)
    {
        var baseDir = AppContext.BaseDirectory;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(serverPath))
        {
            candidates.Add(serverPath);
        }

        candidates.Add(Path.Combine(baseDir, "server"));
        candidates.Add(baseDir);
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..")));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..")));

        foreach (var candidate in candidates)
        {
            var location = TryResolve(candidate, dataDirectoryOverride);
            if (location is not null)
            {
                return location;
            }
        }

        return null;
    }

    private static ServerLocation? TryResolve(string candidate, string? dataDirectoryOverride)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate) && !File.Exists(candidate))
        {
            return null;
        }

        var directory = File.Exists(candidate) ? Path.GetDirectoryName(candidate)! : candidate;
        if (!Directory.Exists(directory))
        {
            return null;
        }

        // 1) apphost（ControlHub.Server.exe）：装了 .NET 运行时即可直接跑。
        var appHost = Path.Combine(directory, "ControlHub.Server.exe");
        if (File.Exists(appHost))
        {
            return Build(appHost, directory, string.Empty, dataDirectoryOverride);
        }

        // 2) 退回 dotnet <dll>（Linux 上没有 .exe，或发布时未生成 apphost）。
        var dll = Path.Combine(directory, "ControlHub.Server.dll");
        if (File.Exists(dll))
        {
            return Build("dotnet", directory, $"\"{dll}\"", dataDirectoryOverride);
        }

        return null;
    }

    private static ServerLocation Build(string executable, string directory, string arguments, string? dataDirectoryOverride)
    {
        var dataDirectory = string.IsNullOrWhiteSpace(dataDirectoryOverride)
            ? Path.Combine(directory, "data")
            : dataDirectoryOverride;

        return new ServerLocation(executable, directory, dataDirectory, arguments);
    }
}
