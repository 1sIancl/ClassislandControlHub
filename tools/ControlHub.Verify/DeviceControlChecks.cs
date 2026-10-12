using ControlHub.Plugin.Models;
using ControlHub.Plugin.Services;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlHub.Verify;

/// <summary>
/// 教室端设备控制（音量 #23 / 亮度 #24）的验证。
/// <para>这两项没有 HTTP 接口可验——A 端只看得到「指令已下发」，真正动手的是教室机上的
/// CoreAudio 与显示器接口。所以这里分两层验：① 直接调底层实现，在有硬件的机器上**真读真写真还原**；
/// ② 把指令喂给<see cref="RemoteCommandExecutor"/> 跑一遍，覆盖载荷校验与回报文案——
/// 只验底层 API 的话，「指令接错了分支」「载荷字段名对不上」这类问题照样漏网。</para>
/// <para>没有音频设备 / 显示器的环境（CI、无头机）退化为「不崩溃、失败原因非空」，不拿环境当缺陷。</para>
/// </summary>
internal static class DeviceControlChecks
{
    public static async Task RunAsync(CheckSink check)
    {
        var snapshot = CheckAudioLayer(check);
        await CheckExecutorLayerAsync(check);
        RestoreAudio(check, snapshot);
        CheckBrightnessLayer(check);
    }

    // ────────────────────────── ① 底层实现（真硬件） ──────────────────────────

    private static (int Percent, bool Muted)? CheckAudioLayer(CheckSink check)
    {
        var readable = AudioVolume.TryGet(out var original, out var originalMuted, out var readError);
        check("音量：能读系统主音量（或给出非空的失败原因）",
            readable || !string.IsNullOrWhiteSpace(readError),
            readable ? $"当前 {original}%，静音={originalMuted}" : readError);

        if (!readable)
        {
            return null;   // 无音频设备的机器：到这里就够了
        }

        check("音量：读到的值在 0~100 之间", original is >= 0 and <= 100, $"{original}%");

        // 真写一次再回读（改动幅度 20%，别把人耳朵吓一跳）
        var target = original >= 50 ? original - 20 : original + 20;
        var setOk = AudioVolume.TrySetPercent(target, out var setError);
        var readBackOk = AudioVolume.TryGet(out var after, out _, out _);
        check($"音量：设到 {target}% 后回读一致（容差 1）",
            setOk && readBackOk && Math.Abs(after - target) <= 1,
            setError ?? $"回读 {after}%");

        // 越界夹取：不能把音量写成异常值
        AudioVolume.TrySetPercent(150, out _);
        check("音量：超范围输入被夹到 100%（不会写出异常值）",
            AudioVolume.TryGet(out var clamped, out _, out _) && clamped == 100, $"回读 {clamped}%");

        AudioVolume.TrySetMute(true, out _);
        var mutedOn = AudioVolume.TryGet(out _, out var on, out _) && on;
        AudioVolume.TrySetMute(false, out _);
        var mutedOff = AudioVolume.TryGet(out _, out var off, out _) && !off;
        check("音量：静音可以开、也可以关", mutedOn && mutedOff, $"开={mutedOn} 关={mutedOff}");

        return (original, originalMuted);
    }

    private static void RestoreAudio(CheckSink check, (int Percent, bool Muted)? snapshot)
    {
        if (snapshot is not { } original)
        {
            return;
        }

        AudioVolume.TrySetMute(original.Muted, out _);
        AudioVolume.TrySetPercent(original.Percent, out _);
        var restored = AudioVolume.TryGet(out var current, out var muted, out _)
                       && Math.Abs(current - original.Percent) <= 1 && muted == original.Muted;
        check($"音量：测试后已还原到 {original.Percent}%（静音={original.Muted}）", restored,
            $"当前 {current}%，静音={muted}");
    }

    // ────────────────────────── ② 执行器（真跑指令分支） ──────────────────────────

    private static async Task CheckExecutorLayerAsync(CheckSink check)
    {
        // LessonBridge 传 null：这两个指令不碰课表（课表桥接另有 LessonBridgeChecks 覆盖）。
        // 传 null 而不是再造一套宿主替身，是为了让「这里不该用到课表」这件事在代码里看得见。
        var executor = new RemoteCommandExecutor(
            NullLogger<RemoteCommandExecutor>.Instance,
            new HubState(),
            new HubSettingsStore(() => Path.Combine(Path.GetTempPath(), "controlhub-verify")),
            null!);

        Task<CommandReportRequest> Run(string kind, object? payload) => executor.ExecuteAsync(new RemoteCommandDto
        {
            Id = $"verify-{kind}",
            Kind = kind,
            Payload = payload is null ? string.Empty : HubJson.Serialize(payload),
        });

        // ── 音量：正常路径 ──
        var set = await Run(RemoteCommandKinds.SetVolume, new VolumePayload { Percent = 35 });
        check("执行器：volume.set 走通并回读当前值",
            set.Success && set.Output.Contains("35") && set.Output.Contains("回读"),
            $"{set.Success} / {set.Output}{(set.Success ? string.Empty : " / " + set.Error)}");

        // ── 音量：载荷什么都没有 → 明确拒绝（而不是当成「什么都不做」的成功）──
        var empty = await Run(RemoteCommandKinds.SetVolume, new VolumePayload());
        check("执行器：volume.set 载荷全空时按「载荷不完整」拒绝",
            !empty.Success && empty.ErrorCode == CommandErrorCodes.PayloadInvalid,
            $"{empty.Success} / {empty.ErrorCode}");

        // ── 音量：越界 ──
        var tooLoud = await Run(RemoteCommandKinds.SetVolume, new VolumePayload { Percent = 150 });
        check("执行器：volume.set 越界（150）被拒并说明范围",
            !tooLoud.Success && tooLoud.ErrorCode == CommandErrorCodes.PayloadInvalid
            && (tooLoud.Error ?? string.Empty).Contains("0~100"),
            $"{tooLoud.ErrorCode} / {tooLoud.Error}");

        // ── 音量：只切静音（不带 percent）也要能执行 ──
        var muteOnly = await Run(RemoteCommandKinds.SetVolume, new VolumePayload { Mute = true });
        check("执行器：volume.set 只带 mute 也能执行（不必每次都传音量）",
            muteOnly.Success && muteOnly.Output.Contains("已静音"),
            $"{muteOnly.Success} / {muteOnly.Output}");

        // ── 亮度：缺 percent → 明确拒绝（不能默默设成 0，屏幕会黑掉）──
        var missing = await Run(RemoteCommandKinds.SetBrightness, new BrightnessPayload());
        check("执行器：brightness.set 缺 percent 时被拒绝（不会默默把屏幕调黑）",
            !missing.Success && missing.ErrorCode == CommandErrorCodes.PayloadInvalid,
            $"{missing.Success} / {missing.ErrorCode}");

        // ── 亮度：越界 ──
        var tooBright = await Run(RemoteCommandKinds.SetBrightness, new BrightnessPayload { Percent = -5 });
        check("执行器：brightness.set 越界（-5）被拒并说明范围",
            !tooBright.Success && tooBright.ErrorCode == CommandErrorCodes.PayloadInvalid,
            $"{tooBright.ErrorCode} / {tooBright.Error}");

        // ── 亮度：正常路径要与硬件能力一致——要么成功，要么**明确说不支持** ──
        var apply = await Run(RemoteCommandKinds.SetBrightness, new BrightnessPayload { Percent = 60 });
        var probe = DisplayBrightness.Query();
        var consistent = apply.Success
            ? probe.Adjustable > 0
            : apply.ErrorCode == CommandErrorCodes.PlatformUnsupported && probe.Adjustable == 0;
        check("执行器：brightness.set 的结果与硬件能力一致（能调就成功，不能调就明确报不支持）",
            consistent,
            $"success={apply.Success} code={apply.ErrorCode} 可调={probe.Adjustable} / {apply.Output}{apply.Error}");

        // 还原亮度（这轮真改过的话）
        if (apply.Success && probe.CurrentPercent >= 0)
        {
            DisplayBrightness.SetPercent(probe.CurrentPercent);
        }
    }

    // ────────────────────────── 亮度：能力探测 ──────────────────────────

    private static void CheckBrightnessLayer(CheckSink check)
    {
        var probe = DisplayBrightness.Query();
        check("亮度：能探测显示器（返回台数，不抛异常）", probe.Total >= 0,
            $"检测到 {probe.Total} 台，可调 {probe.Adjustable} 台，"
            + $"当前 {(probe.CurrentPercent < 0 ? "—" : probe.CurrentPercent + "%")}");
        check("亮度：可调台数不会超过检测台数",
            probe.Adjustable >= 0 && probe.Adjustable <= probe.Total, $"{probe.Adjustable}/{probe.Total}");

        var apply = DisplayBrightness.SetPercent(60);
        check("亮度：设置结果自洽（生效 ≤ 可调 ≤ 总数）",
            apply.Applied >= 0 && apply.Applied <= apply.Adjustable && apply.Adjustable <= apply.Total,
            $"总计 {apply.Total} / 可调 {apply.Adjustable} / 生效 {apply.Applied}");

        if (probe.Adjustable == 0)
        {
            // 外接显示器多为此种情况。要验的不是「能不能调」，而是**如实报告不支持**
            // ——这正是 #24 最容易做错的地方（假装成功会让现场以为系统坏了）。
            check("亮度：一台都不支持时结果为「生效 0 台」（供上层报不支持，而不是假装成功）",
                apply.Applied == 0, $"可调 {apply.Adjustable} / 生效 {apply.Applied}");
            return;
        }

        if (probe.CurrentPercent >= 0)
        {
            DisplayBrightness.SetPercent(probe.CurrentPercent);
            var back = DisplayBrightness.Query();
            check($"亮度：测试后已还原到 {probe.CurrentPercent}%（容差 2）",
                back.Adjustable == 0 || Math.Abs(back.CurrentPercent - probe.CurrentPercent) <= 2,
                $"回读 {back.CurrentPercent}%");
        }
    }
}
