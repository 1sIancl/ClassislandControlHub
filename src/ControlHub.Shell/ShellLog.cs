using System.Text;

namespace ControlHub.Shell;

/// <summary>
/// 极简日志：同时写控制台与 <c>%LOCALAPPDATA%\ClassislandControlHub\shell.log</c>。
/// <para>外壳排障主要靠它——GUI 模式下没有控制台，日志文件是唯一线索。</para>
/// </summary>
internal static class ShellLog
{
    private static readonly Lock Gate = new();
    private static readonly string LogPath = BuildLogPath();

    /// <summary>日志文件路径。</summary>
    public static string Path => LogPath;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}：{ex}");

    private static void Write(string level, string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";

        try
        {
            Console.WriteLine(line);
        }
        catch
        {
            // GUI 模式下没有控制台，忽略。
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去不应影响主流程。
        }
    }

    private static string BuildLogPath()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassislandControlHub");

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // 落到临时目录。
            dir = System.IO.Path.GetTempPath();
        }

        return System.IO.Path.Combine(dir, "shell.log");
    }
}
