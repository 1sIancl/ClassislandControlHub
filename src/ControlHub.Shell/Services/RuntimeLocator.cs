namespace ControlHub.Shell.Services;

/// <summary>
/// 定位 .NET 运行时安装目录。
/// <para>
/// 为什么需要它：A 端的 <c>ControlHub.Server.exe</c> 是 apphost，它只按系统默认位置找运行时，
/// 而把 .NET 装在用户目录（便携安装、单用户安装、或脚本安装到 <c>~/.dotnet</c>）时就会报
/// 「You must install or update .NET to run this application / No frameworks were found」。
/// 外壳找到运行时目录后通过 <c>DOTNET_ROOT</c> 告诉 apphost，就能正常启动。
/// </para>
/// </summary>
internal static class RuntimeLocator
{
    /// <summary>找到可用的 .NET 根目录（含 dotnet 可执行文件与 ASP.NET Core 共享框架）；找不到返回 <c>null</c>。</summary>
    public static string? FindDotnetRoot()
    {
        foreach (var candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var directory = candidate.Trim();
                var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(executable)
                    && Directory.Exists(Path.Combine(directory, "shared", "Microsoft.AspNetCore.App")))
                {
                    return directory;
                }
            }
            catch
            {
                // 路径非法就跳过。
            }
        }

        return null;
    }

    /// <summary>dotnet 可执行文件的完整路径；找不到返回 <c>null</c>。</summary>
    public static string? FindDotnetExecutable() =>
        FindDotnetRoot() is { } root
            ? Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")
            : null;

    private static IEnumerable<string?> Candidates()
    {
        yield return Environment.GetEnvironmentVariable("DOTNET_ROOT");
        yield return Environment.GetEnvironmentVariable("DOTNET_ROOT_X64");

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "dotnet");
        }

        // 便携/单用户安装的常见位置（dotnet-install 脚本默认装到这里）。
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        yield return FindOnPath();
    }

    private static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var directory = entry.Trim();
                var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(executable))
                {
                    return directory;
                }
            }
            catch
            {
                // 忽略非法 PATH 项。
            }
        }

        return null;
    }
}
