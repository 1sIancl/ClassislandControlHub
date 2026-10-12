using System.IO.Compression;
using System.Security.Cryptography;
using ControlHub.Plugin.Models;
using ControlHub.Plugin.Services;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlHub.Verify;

/// <summary>
/// 插件市场代理缓存（#121）里**教室端那一半**的验证。
/// <para>包是 A 端缓存的、指令是 A 端下发的，但「这个包能不能装」的判定在教室端：
/// 校验 SHA256、读包内 manifest、比对宿主版本与平台——这一层没有 HTTP 接口可验，
/// 只能在验证台里直接喂字节跑分支。</para>
/// <para>A 端那一半（索引解析、镜像顺序、缓存与下发）由 <c>_shot/verify-plugin-market.js</c>
/// 用本地夹具端到端覆盖。</para>
/// </summary>
internal static class PluginMarketChecks
{
    /// <summary>测试用插件 ID：刻意带 Verify 前缀，万一清理失败也不会和真插件撞名。</summary>
    private const string FixtureId = "ControlHub.Verify.PluginPackageFixture";

    public static async Task RunAsync(CheckSink check)
    {
        Console.WriteLine();
        Console.WriteLine("插件包校验与安装（plugin.install）");

        CheckValidationLayer(check);
        await CheckExecutorLayerAsync(check);
    }

    // ────────────────────────── ① 校验分支（纯逻辑，不碰磁盘） ──────────────────────────

    private static void CheckValidationLayer(CheckSink check)
    {
        var good = BuildPackage(FixtureId, "1.2.3.0", "2.1.1.1");
        var sha = Convert.ToHexString(SHA256.HashData(good));

        check("校验：正常的 .cipx 通过（返回 null 表示可安装）",
            PluginPackageInstaller.Validate(good, FixtureId, "1.2.3.0", string.Empty) is null);

        check("校验：SHA256 一致时通过",
            PluginPackageInstaller.Validate(good, FixtureId, "1.2.3.0", sha) is null);

        var badSha = PluginPackageInstaller.Validate(good, FixtureId, "1.2.3.0", new string('A', 64));
        check("校验：SHA256 不一致 → COMMAND_INTEGRITY_FAILED（提示重新缓存）",
            badSha?.ErrorCode == CommandErrorCodes.IntegrityFailed
            && (badSha.Message.Contains("重新缓存")),
            $"{badSha?.ErrorCode} / {badSha?.Message}");

        check("校验：空字节 → 载荷不完整",
            PluginPackageInstaller.Validate([], FixtureId, "1.2.3.0", string.Empty)?.ErrorCode
            == CommandErrorCodes.PayloadInvalid);

        check("校验：不是 zip 的文件 → 载荷不完整",
            PluginPackageInstaller.Validate("这不是一个 zip"u8.ToArray(), FixtureId, "1.2.3.0", string.Empty)
                ?.ErrorCode == CommandErrorCodes.PayloadInvalid);

        check("校验：zip 里没有 manifest.yml → 载荷不完整",
            PluginPackageInstaller.Validate(BuildZipWithoutManifest(), FixtureId, "1.2.3.0", string.Empty)
                ?.ErrorCode == CommandErrorCodes.PayloadInvalid);

        var otherId = BuildPackage("Some.Other.Plugin", "1.2.3.0", "2.1.1.1");
        check("校验：包内 ID 与下发的不符 → 拒绝（避免把别人的包推上去）",
            PluginPackageInstaller.Validate(otherId, FixtureId, "1.2.3.0", string.Empty)?.ErrorCode
            == CommandErrorCodes.PayloadInvalid);

        check("校验：包内版本与下发的不符 → 拒绝",
            PluginPackageInstaller.Validate(good, FixtureId, "9.9.9.9", string.Empty)?.ErrorCode
            == CommandErrorCodes.PayloadInvalid);

        var noId = BuildPackage(string.Empty, "1.2.3.0", "2.1.1.1");
        check("校验：manifest 缺 id → 拒绝",
            PluginPackageInstaller.Validate(noId, FixtureId, string.Empty, string.Empty)?.ErrorCode
            == CommandErrorCodes.PayloadInvalid);

        var tooOld = BuildPackage(FixtureId, "1.2.3.0", "1.0.0.0");
        check("校验：apiVersion 低于宿主支持的 2.0.0.0 → COMMAND_UNSUPPORTED",
            PluginPackageInstaller.Validate(tooOld, FixtureId, "1.2.3.0", string.Empty)?.ErrorCode
            == CommandErrorCodes.Unsupported);

        var tooNew = BuildPackage(FixtureId, "1.2.3.0", "99.0.0.0");
        var tooNewOutcome = PluginPackageInstaller.Validate(tooNew, FixtureId, "1.2.3.0", string.Empty);
        check("校验：apiVersion 高于本机 ClassIsland → COMMAND_UNSUPPORTED（提示先升级宿主）",
            tooNewOutcome?.ErrorCode == CommandErrorCodes.Unsupported
            && tooNewOutcome.Message.Contains("升级"),
            $"{tooNewOutcome?.ErrorCode} / {tooNewOutcome?.Message}");

        var linuxOnly = BuildPackage(FixtureId, "1.2.3.0", "2.1.1.1", "[Linux]");
        check("校验：插件只声明 Linux → COMMAND_PLATFORM_UNSUPPORTED",
            PluginPackageInstaller.Validate(linuxOnly, FixtureId, "1.2.3.0", string.Empty)?.ErrorCode
            == CommandErrorCodes.PlatformUnsupported);

        var noPlatforms = BuildPackage(FixtureId, "1.2.3.0", "2.1.1.1");
        check("校验：未声明 supportedOSPlatforms = 全平台，不因平台被拦",
            PluginPackageInstaller.Validate(noPlatforms, FixtureId, "1.2.3.0", string.Empty) is null);

        check("校验：读包内 manifest 能取到 id / 版本 / API 版本",
            PluginPackageInstaller.ReadPackageInfo(good) is { Id: FixtureId, Version: "1.2.3.0", ApiVersion: "2.1.1.1" });
    }

    // ────────────────────────── ② 执行器（真跑 plugin.install 分支） ──────────────────────────

    private static async Task CheckExecutorLayerAsync(CheckSink check)
    {
        var payload = new PluginInstallPayload
        {
            PluginId = FixtureId,
            Version = "1.2.3.0",
            CipxUrl = "/client/plugin-packages/x/y/download",
            Sha256 = string.Empty,
            FileName = FixtureId + ".cipx",
        };

        Task<CommandReportRequest> Run(RemoteCommandExecutor executor, object? body) =>
            executor.ExecuteAsync(new RemoteCommandDto
            {
                Id = "verify-plugin-install",
                Kind = RemoteCommandKinds.PluginInstall,
                Payload = body is null ? string.Empty : HubJson.Serialize(body),
            });

        // ── 载荷不全 ──
        await using (var bare = NewExecutor(download: null))
        {
            var missing = await Run(bare.Executor, new PluginInstallPayload { PluginId = FixtureId });
            check("执行器：plugin.install 缺少下载地址时按「载荷不完整」拒绝",
                !missing.Success && missing.ErrorCode == CommandErrorCodes.PayloadInvalid,
                $"{missing.Success} / {missing.ErrorCode}");
        }

        // ── 没接下载通道（老版本插件）──
        await using (var offline = NewExecutor(download: null))
        {
            var noChannel = await Run(offline.Executor, payload);
            check("执行器：没有下载通道时回 HOST_API_UNAVAILABLE（提示升级插件）",
                !noChannel.Success && noChannel.ErrorCode == CommandErrorCodes.HostApiUnavailable
                && (noChannel.Error ?? string.Empty).Contains("升级"),
                $"{noChannel.ErrorCode} / {noChannel.Error}");
        }

        // ── 下载失败 ──
        await using (var failing = NewExecutor(_ => throw new InvalidOperationException("取包时连接被重置")))
        {
            var failed = await Run(failing.Executor, payload);
            check("执行器：下载失败 → 执行失败，且带上真实原因",
                !failed.Success && failed.ErrorCode == CommandErrorCodes.ExecutionFailed
                && (failed.Error ?? string.Empty).Contains("连接被重置"),
                $"{failed.ErrorCode} / {failed.Error}");
        }

        // ── SHA256 不符（服务器缓存坏了 / 传输坏了）──
        var package = BuildPackage(FixtureId, "1.2.3.0", "2.1.1.1");
        await using (var mismatch = NewExecutor(_ => package))
        {
            var wrong = await Run(mismatch.Executor, new PluginInstallPayload
            {
                PluginId = FixtureId,
                Version = "1.2.3.0",
                CipxUrl = "/client/plugin-packages/x/y/download",
                Sha256 = new string('B', 64),
            });
            check("执行器：SHA256 不符 → COMMAND_INTEGRITY_FAILED（而不是笼统的执行失败）",
                !wrong.Success && wrong.ErrorCode == CommandErrorCodes.IntegrityFailed,
                $"{wrong.ErrorCode}");
        }

        // ── 正常路径：真落盘 ──
        var expectedPath = string.Empty;
        try
        {
            expectedPath = Path.Combine(PluginPackageInstaller.ResolvePendingDirectory(), FixtureId + ".cipx");
        }
        catch (Exception ex)
        {
            check("落盘：宿主的待安装目录可定位（或给出原因）", false, ex.Message);
        }

        await using (var good = NewExecutor(_ => package))
        {
            var ok = await Run(good.Executor, payload);
            var landed = expectedPath.Length > 0 && File.Exists(expectedPath);
            check("执行器：plugin.install 走通并告知「重启后生效」",
                ok.Success && ok.Output.Contains("重启") && landed,
                $"{ok.Success} / 落盘={landed} / {ok.Output}{(ok.Success ? string.Empty : " / " + ok.Error)}");

            if (landed)
            {
                var written = await File.ReadAllBytesAsync(expectedPath);
                check("落盘：写入的字节与下载到的一致（没被二次加工）",
                    written.AsSpan().SequenceEqual(package));
            }
        }

        // 清理：这个假插件包绝不能留在宿主的待安装目录里（下次启动会被装进去）。
        if (expectedPath.Length > 0 && File.Exists(expectedPath))
        {
            File.Delete(expectedPath);
        }

        check("落盘：验证用的假包已清理（不会在宿主下次启动时被装上）",
            expectedPath.Length == 0 || !File.Exists(expectedPath));
    }

    /// <summary>造一个执行器：可选注入「下载」回调（<c>null</c> = 模拟老版本插件没接通道）。</summary>
    private static ExecutorFixture NewExecutor(Func<string, byte[]>? download)
    {
        // 自己的子目录：别去删 DeviceControlChecks 也在用的那个共享目录。
        var dataDir = Path.Combine(Path.GetTempPath(), "controlhub-verify", "plugin-market");
        var executor = new RemoteCommandExecutor(
            NullLogger<RemoteCommandExecutor>.Instance,
            new HubState(),
            new HubSettingsStore(() => dataDir),
            null!);

        if (download is not null)
        {
            executor.OnDownloadPackage = (_, _) => Task.FromResult(download(string.Empty));
        }

        return new ExecutorFixture(executor, dataDir);
    }

    private sealed class ExecutorFixture(RemoteCommandExecutor executor, string dataDir) : IAsyncDisposable
    {
        public RemoteCommandExecutor Executor { get; } = executor;

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(dataDir))
            {
                try
                {
                    Directory.Delete(dataDir, recursive: true);
                }
                catch
                {
                    // 清理失败不影响断言结果。
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    // ────────────────────────── 夹具：手工造一个 .cipx ──────────────────────────

    private static byte[] BuildPackage(string id, string version, string apiVersion, string? platforms = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("manifest.yml");
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine("# 验证台夹具：不是真插件");
            writer.WriteLine("entranceAssembly: Fixture.dll");
            if (id.Length > 0)
            {
                writer.WriteLine($"id: {id}");
            }

            writer.WriteLine($"name: {id}");
            writer.WriteLine($"version: {version}");
            writer.WriteLine($"apiVersion: {apiVersion}");
            if (platforms is not null)
            {
                writer.WriteLine($"supportedOSPlatforms: {platforms}");
            }
        }

        return stream.ToArray();
    }

    private static byte[] BuildZipWithoutManifest()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("readme.md");
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine("没有 manifest.yml 的包");
        }

        return stream.ToArray();
    }
}
