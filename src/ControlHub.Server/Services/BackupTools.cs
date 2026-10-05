using System.Security.Cryptography;
using System.Text;

namespace ControlHub.Server.Services;

/// <summary>
/// 与备份相关的命令行工具（#60）。
///
/// 解密为什么做成命令行、而不是接口：加密备份的真正使用场景是**换机器 / 灾难恢复**——
/// 那时新服务器上可能还没有能跑起来的服务端（库损坏、环境没装好），接口根本调不到；
/// 而可执行文件本身总是能运行。
///
/// 口令优先取命令行参数，其次交互式输入（不回显）；输入被重定向时从标准输入读一行，
/// 便于脚本里 <c>echo 口令 | ControlHub.Server --decrypt-backup in.enc out.zip</c>。
/// </summary>
public static class BackupTools
{
    /// <summary>
    /// 若参数是备份工具的调用则执行并返回 <c>true</c>（调用方据此在启动 Web 之前退出）。
    /// </summary>
    public static bool TryRunCommandLine(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !string.Equals(args[0], "--decrypt-backup", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        exitCode = Decrypt(args);
        return true;
    }

    private static int Decrypt(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("用法：ControlHub.Server --decrypt-backup <加密备份文件> <输出zip> [口令]");
            Console.Error.WriteLine("      不给口令时会交互式提示输入（不回显）；也可从标准输入读入。");
            return 2;
        }

        var input = args[1];
        var output = args[2];
        var passphrase = args.Length >= 4 ? args[3] : ReadPassphrase();

        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"找不到文件：{input}");
            return 1;
        }

        if (string.IsNullOrEmpty(passphrase))
        {
            Console.Error.WriteLine("口令不能为空。");
            return 1;
        }

        try
        {
            var payload = File.ReadAllBytes(input);
            var zip = BackupCrypto.Decrypt(payload, passphrase);
            File.WriteAllBytes(output, zip);

            Console.WriteLine($"已解密：{input}（{payload.Length} 字节）→ {output}（{zip.Length} 字节）");
            Console.WriteLine("解出来的就是一个普通 zip：解开后可见 controlhub.db 等文件。");
            Console.WriteLine("把它放回数据目录后重启服务，或直接用其中的数据库替换现有库即可。");
            return 0;
        }
        catch (CryptographicException)
        {
            // GCM 认证失败：口令错误或文件被改动过。两种情况对用户来说处理方式一样——先确认口令。
            Console.Error.WriteLine("解密失败：口令不正确，或文件已损坏 / 被改动过。");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"解密失败：{ex.Message}");
            return 1;
        }
    }

    /// <summary>读口令：交互式（不回显）或从标准输入。</summary>
    private static string ReadPassphrase()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        Console.Write("请输入备份口令：");
        var builder = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }

        return builder.ToString();
    }
}
