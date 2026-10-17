using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;
using IslandHub.Server.Options;
using Microsoft.Extensions.Options;

namespace IslandHub.Server.Services;

/// <summary>
/// 走班制教室按时段切档案（`#25`）。
///
/// <para><b>只在「该换」的那一刻动手</b>：每一轮先算出「按规则现在应该是哪个档案」，只有它和教室里
/// 当前生效的档案**不同**时才改 + 下发。走班制一天要切好几次，但绝大多数轮次什么都不会做——
/// 每次改档案都会抬升推送世代号、让教室端重拉配置，无脑按节下发会把全校教室端刷爆。</para>
///
/// <para><b>考试模式进行中不插手</b>：考试模式刻意把档案钉在考试档案上，走班规则这时候改档案
/// 会破坏它的前提（也违反考试模式页面上的那句提示）。</para>
///
/// <para><b>已知前提</b>：节次判定用的是「这台教室当前生效档案」里的作息时间表。走班制教室通常
/// 共用同一份作息，所以这与预期一致；如果各班档案的作息真的不同，判定会跟着当前档案的作息走
/// （这一点写进了 USAGE）。</para>
/// </summary>
public sealed class ProfileSlotService(
    HubStore store,
    SyncService sync,
    IOptionsMonitor<ServerOptions> options,
    ILogger<ProfileSlotService> logger) : BackgroundService
{
    /// <summary>执行者标识（写进推送批次的 createdBy，报表里一眼看出是走班规则推的）。</summary>
    public const string Actor = "system:profile-slots";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先对齐一次：服务重启（例如深夜升级）之后，早上的第一节要按规则切过来。
        var interval = TimeSpan.FromSeconds(Math.Max(10, options.CurrentValue.ProfileSlotIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.CurrentValue.ProfileSlotsEnabled)
                {
                    await ReconcileAsync(null, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 后台服务：一轮出错不能让下一轮不跑。
                logger.LogError(ex, "走班制时段规则对齐失败。");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 按当前时段对齐一次（<paramref name="deviceId"/> 为 <c>null</c> 表示所有带规则的教室）。
    /// <para>管理端也能手动触发（「立即对齐」）：作息临时调整后，管理员不必等下一轮。</para>
    /// </summary>
    public async Task<ProfileSlotsApplyReportDto> ReconcileAsync(string? deviceId,
        CancellationToken cancellationToken)
    {
        var report = new ProfileSlotsApplyReportDto { CheckedAt = DateTimeOffset.UtcNow };

        var allSlots = await store.GetAllProfileSlotsAsync(cancellationToken);
        if (deviceId is not null)
        {
            allSlots = allSlots.Where(s => string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal)).ToList();
        }

        if (allSlots.Count == 0)
        {
            return report;
        }

        if (await store.GetActiveExamModeAsync(cancellationToken) is not null)
        {
            report.Items.Add(new ProfileSlotApplyItemDto
            {
                DeviceName = "（全部带规则的教室）",
                Result = "skipped",
                Message = "考试模式进行中，走班规则暂不生效（考试结束后会自动恢复）。",
            });
            return report;
        }

        var devices = (await store.GetDevicesAsync(cancellationToken))
            .Where(d => !d.Revoked)
            .ToDictionary(d => d.Id, StringComparer.Ordinal);
        var now = TimeOnly.FromDateTime(DateTime.Now);   // 作息表是本地时间

        foreach (var group in allSlots.GroupBy(s => s.DeviceId))
        {
            if (!devices.TryGetValue(group.Key, out var device))
            {
                continue;   // 设备已删除：规则留着不影响，界面那边也会过滤掉
            }

            var item = new ProfileSlotApplyItemDto { DeviceName = device.Name };

            var profile = await sync.ResolveProfileAsync(device, cancellationToken);
            var layout = await ResolveLayoutAsync(profile, cancellationToken);
            var periodIndex = ProfileSlotResolver.ResolvePeriodIndex(layout, now);

            // 「算不出第几节」必须说出来，不能当成「不在上课时段」混过去：这台教室配了规则却永远不生效，
            // 管理员在界面上只会看到「什么都没发生」，根本联想不到是档案里缺时间表。
            if (ProfileSlotResolver.ClassPeriods(layout).Count == 0)
            {
                item.Result = "skipped";
                item.Message = "当前生效的档案里没有作息时间表（或没有「上课」时间点），无法判断第几节——"
                    + "请检查该档案的时间表。";
                report.Items.Add(item);
                report.DeviceCount++;
                continue;
            }

            if (periodIndex is null)
            {
                item.Result = "out_of_period";
                item.Message = "当前不在上课时段，保持现状。";
                report.Items.Add(item);
                report.DeviceCount++;
                continue;
            }

            var rule = group.FirstOrDefault(s => s.PeriodIndex == periodIndex.Value);
            if (rule is null)
            {
                item.Result = "unchanged";
                item.Message = $"第 {periodIndex.Value + 1} 节没有规则，保持现状。";
                report.Items.Add(item);
                report.DeviceCount++;
                continue;
            }

            if (string.Equals(device.ProfileId, rule.ProfileId, StringComparison.Ordinal))
            {
                item.Result = "unchanged";
                item.Message = $"第 {periodIndex.Value + 1} 节已经是目标档案，无需改动。";
                report.Items.Add(item);
                report.DeviceCount++;
                continue;
            }

            var target = await store.GetProfileAsync(rule.ProfileId, cancellationToken);
            if (target is null)
            {
                item.Result = "skipped";
                item.Message = $"第 {periodIndex.Value + 1} 节的规则指向的档案已不存在，请修正规则。";
                report.Items.Add(item);
                report.DeviceCount++;
                continue;
            }

            await store.SetDeviceProfileAsync(device.Id, target.Id, cancellationToken);
            await sync.PushAsync(new PushRequest
            {
                Scope = "device",
                TargetIds = [device.Id],
                Message = $"走班制：第 {periodIndex.Value + 1} 节切到「{target.Name}」",
            }, Actor, cancellationToken);

            item.Result = "applied";
            item.Message = $"第 {periodIndex.Value + 1} 节 → 已切到「{target.Name}」并下发。";
            report.Items.Add(item);
            report.DeviceCount++;
            report.Changed++;

            logger.LogInformation("走班制：{Device} 第 {Period} 节切到「{Profile}」。",
                device.Name, periodIndex.Value + 1, target.Name);
        }

        return report;
    }

    /// <summary>
    /// 取用于判定节次的时间表：优先「第一张启用的非临时课表所引用的时间表」，否则退回第一张时间表。
    /// </summary>
    private static Task<TimeLayoutDto?> ResolveLayoutAsync(ProfileRow? profile,
        CancellationToken cancellationToken)
    {
        var content = HubJson.Deserialize<ContentBundleDto>(profile?.Content ?? string.Empty);
        if (content is null || content.TimeLayouts.Count == 0)
        {
            return Task.FromResult<TimeLayoutDto?>(null);
        }

        var plan = content.ClassPlans.FirstOrDefault(p => !p.IsOverlay && p.IsEnabled);
        var referenced = plan is null
            ? null
            : content.TimeLayouts.FirstOrDefault(l => string.Equals(l.Id, plan.TimeLayoutId, StringComparison.Ordinal));

        return Task.FromResult<TimeLayoutDto?>(referenced ?? content.TimeLayouts[0]);
    }
}
