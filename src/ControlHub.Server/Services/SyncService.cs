using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Services;

/// <summary>
/// 同步服务：负责把「设备 → 应生效的配置档案」解析出来，并生成下发内容包。
/// </summary>
public sealed class SyncService(
    HubStore store,
    RevisionNotifier notifier,
    IOptions<ServerOptions> options)
{
    private readonly ServerOptions _options = options.Value;

    /// <summary>
    /// 解析设备当前应生效的配置档案。
    /// 优先级：设备单独指定 &gt; 设备所属分组的默认档案 &gt; 上级分组（楼栋）的默认档案 &gt; 全局默认档案。
    /// </summary>
    public async Task<ProfileRow?> ResolveProfileAsync(DeviceRow device,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(device.ProfileId))
        {
            var direct = await store.GetProfileAsync(device.ProfileId, cancellationToken);
            if (direct is not null)
            {
                return direct;
            }
        }

        // 从设备所在分组逐级向上找：楼层没设置就用所属楼栋的默认档案。
        var groupId = device.GroupId;
        for (var depth = 0; depth < 4 && !string.IsNullOrEmpty(groupId); depth++)
        {
            var group = await store.GetGroupAsync(groupId, cancellationToken);
            if (group is null)
            {
                break;
            }

            if (!string.IsNullOrEmpty(group.DefaultProfileId))
            {
                var fromGroup = await store.GetProfileAsync(group.DefaultProfileId, cancellationToken);
                if (fromGroup is not null)
                {
                    return fromGroup;
                }
            }

            groupId = group.ParentId;
        }

        return await store.GetDefaultProfileAsync(cancellationToken);
    }

    /// <summary>
    /// 构建设备的同步应答。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="clientRevision">客户端当前持有的版本号。与服务器一致且非强制时返回 <c>null</c> 表示无需下发。</param>
    /// <param name="clientPushEpoch">
    /// 客户端已送达的推送世代号。定向推送 / 分批下发只推进世代号而不改内容，
    /// 因此「是否需要下发」必须同时看它与 <see cref="DeviceRow.PushEpoch"/>。
    /// </param>
    /// <param name="sections">客户端声明支持的同步分区，为空表示全部。</param>
    /// <param name="force">是否强制下发。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SyncResponse?> BuildSyncResponseAsync(DeviceRow device, long? clientRevision,
        long? clientPushEpoch, IReadOnlyCollection<string>? sections, bool force,
        CancellationToken cancellationToken = default)
    {
        var revision = await store.GetRevisionAsync(cancellationToken);

        // 「已是最新」必须同时满足两个条件：版本号一致 **且** 推送世代号也送达过。
        // 只比版本号是错的：定向推送与分批下发**只推进世代号、不改内容**（revision 不变），
        // 那样客户端会一直被判定为「无需同步」，推送永远不生效（表现为：管理端显示待同步，
        // 设备却始终不重新拉取）。旧版插件不发 pushEpoch，此时沿用旧行为以保持兼容。
        var epochDelivered = !clientPushEpoch.HasValue || clientPushEpoch.Value >= device.PushEpoch;
        if (!force && clientRevision.HasValue && clientRevision.Value == revision && epochDelivered)
        {
            return null;
        }

        var profile = await ResolveProfileAsync(device, cancellationToken);
        if (profile is null)
        {
            // 没有可用档案时下发一个空内容包，让客户端明确知道「当前无配置」，
            // 而不是一直保持旧数据。
            return new SyncResponse
            {
                Revision = revision,
                // 空档案也要回填世代号：否则客户端报不上去，会一直卡在「待同步」。
                PushEpoch = device.PushEpoch,
                ProfileId = string.Empty,
                ProfileName = "(未分配配置档案)",
                Content = new ContentBundleDto(),
                Checksum = HubChecksum.ComputeOf(new ContentBundleDto()),
                Force = force,
            };
        }

        var content = HubJson.DeserializeOrDefault(profile.Content, new ContentBundleDto());
        content.Settings ??= new ProfileSettingsDto();
        content.Settings.Values ??= [];
        content.Settings.Announcement ??= null;

        // 声明了分区能力时按需裁剪，减少带宽占用。
        if (sections is { Count: > 0 })
        {
            var requested = sections.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!requested.Contains(HubProtocol.Sections.TimeLayouts))
            {
                content.TimeLayouts = [];
            }

            if (!requested.Contains(HubProtocol.Sections.ClassPlans))
            {
                content.ClassPlans = [];
            }

            if (!requested.Contains(HubProtocol.Sections.Subjects))
            {
                content.Subjects = [];
            }

            if (!requested.Contains(HubProtocol.Sections.Settings))
            {
                content.Settings = new ProfileSettingsDto();
            }
        }

        // 临时换课：把「今天生效」的覆盖合成到内容包上。覆盖不写回档案，
        // 因此过了截止日期、设备下一次同步就会自动拿回原课表。
        await ApplyTimetableOverridesAsync(content, profile.Id, cancellationToken);

        return new SyncResponse
        {
            Revision = revision,
            PushEpoch = device.PushEpoch,
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            IssuedAt = DateTimeOffset.UtcNow,
            Content = content,
            Checksum = HubChecksum.ComputeOf(content),
            Force = force,
        };
    }

    /// <summary>
    /// 应用「临时换课」：命中当天的覆盖会把对应课表的某个节点换成临时科目。
    /// <para>日期按服务器本地日期判断（学校作息以本地时间为准），星期几与协议的
    /// <c>0=周日 .. 6=周六</c> 一致。</para>
    /// </summary>
    private async Task ApplyTimetableOverridesAsync(ContentBundleDto content, string profileId,
        CancellationToken cancellationToken)
    {
        if (content.ClassPlans.Count == 0)
        {
            return;
        }

        var today = DateTime.Now;
        var overrides = await store.GetActiveTimetableOverridesAsync(
            profileId, today.ToString("yyyy-MM-dd"), (int)today.DayOfWeek, cancellationToken);
        if (overrides.Count == 0)
        {
            return;
        }

        foreach (var row in overrides)
        {
            foreach (var plan in content.ClassPlans)
            {
                if (!string.IsNullOrEmpty(row.ClassPlanId)
                    && !string.Equals(plan.Id, row.ClassPlanId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var slot = plan.Slots.FirstOrDefault(s => s.Index == row.SlotIndex);
                if (slot is not null)
                {
                    slot.SubjectId = row.SubjectId;
                }
            }
        }
    }

    /// <summary>设备当前是否在线（由最近心跳时间与心跳超时共同决定）。</summary>
    public bool IsOnline(DeviceRow device) =>
        !device.Revoked
        && device.LastSeenAt.HasValue
        && (DateTimeOffset.UtcNow - device.LastSeenAt.Value).TotalSeconds < _options.OnlineTimeoutSeconds;

    /// <summary>把数据库行转换为面向 Web 管理端的摘要对象。</summary>
    public DeviceSummaryDto ToSummary(DeviceRow device, GroupRow? group, long serverRevision)
    {
        var online = IsOnline(device);

        var state = device.Revoked
            ? DeviceStates.Offline
            : online
                ? device.State
                : DeviceStates.Offline;

        return new DeviceSummaryDto
        {
            Id = device.Id,
            Name = device.Name,
            GroupId = device.GroupId,
            GroupName = group?.Name,
            ProfileId = device.ProfileId,
            State = state,
            Online = online,
            AppliedRevision = device.AppliedRevision,
            ServerRevision = serverRevision,
            AppliedPushEpoch = device.AppliedPushEpoch,
            ServerPushEpoch = device.PushEpoch,
            LastSeenAt = device.LastSeenAt,
            LastSyncAt = device.LastSyncAt,
            MachineName = device.MachineName,
            OsVersion = device.OsVersion,
            ClassIslandVersion = device.ClassIslandVersion,
            PluginVersion = device.PluginVersion,
            IpAddress = device.IpAddress,
            Remark = device.Remark,
            CurrentClassPlanName = device.CurrentClassPlanName,
            LastError = device.LastError,
            Revoked = device.Revoked,
            CreatedAt = device.CreatedAt,
            OfflineReason = BuildOfflineReason(device, online),
            OfflineMinutes = OfflineMinutes(device, online),
        };
    }

    /// <summary>
    /// 把「离线」细分成可行动的原因，而不是让管理员只看到一个灰点：
    /// 从未连接 / 已停用 / 离线多久 + 最近一次报错。
    /// </summary>
    public static string? BuildOfflineReason(DeviceRow device, bool online)
    {
        if (online)
        {
            return null;
        }

        if (device.Revoked)
        {
            return "已被停用（恢复后才会重新上报）";
        }

        if (device.LastSeenAt is null)
        {
            return "从未连接：设备已注册但从未成功心跳，检查教室端插件的服务器地址、网络与防火墙";
        }

        var minutes = (int)Math.Round((DateTimeOffset.UtcNow - device.LastSeenAt.Value).TotalMinutes);
        var elapsed = minutes >= 60
            ? $"已离线 {minutes / 60} 小时 {minutes % 60} 分钟"
            : $"已离线 {minutes} 分钟";

        if (!string.IsNullOrWhiteSpace(device.LastError))
        {
            var error = device.LastError.Trim();
            if (error.Length > 120)
            {
                error = error[..120] + "…";
            }

            return $"{elapsed}，最近一次报错：{error}";
        }

        return device.LastSyncAt is null
            ? $"{elapsed}（从未成功同步过配置）"
            : $"{elapsed}（常见原因：教室机断电 / 关机、网线或无线中断、插件被退出）";
    }

    /// <summary>离线时长（分钟）；在线或从未上线时为 <c>null</c>。</summary>
    public static double? OfflineMinutes(DeviceRow device, bool online) =>
        online || device.LastSeenAt is null
            ? null
            : Math.Max(0, (DateTimeOffset.UtcNow - device.LastSeenAt.Value).TotalMinutes);

    /// <summary>批量转换设备摘要，避免逐条查询分组。</summary>
    public async Task<List<DeviceSummaryDto>> GetDeviceSummariesAsync(
        IEnumerable<DeviceRow> devices, CancellationToken cancellationToken = default)
    {
        var revision = await store.GetRevisionAsync(cancellationToken);
        var groups = (await store.GetGroupsAsync(cancellationToken))
            .ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        var list = new List<DeviceSummaryDto>();
        foreach (var device in devices)
        {
            var group = device.GroupId is not null && groups.TryGetValue(device.GroupId, out var g) ? g : null;
            list.Add(ToSummary(device, group, revision));
        }

        return list;
    }

    /// <summary>查询全部设备的摘要。</summary>
    public async Task<List<DeviceSummaryDto>> GetDeviceSummariesAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = await store.GetDevicesAsync(cancellationToken);
        return await GetDeviceSummariesAsync(devices, cancellationToken);
    }

    /// <summary>递增全局版本并唤醒所有等待同步的客户端。</summary>
    public async Task<long> BumpRevisionAsync(CancellationToken cancellationToken = default)
    {
        var revision = await store.BumpRevisionAsync(cancellationToken);
        notifier.Publish(revision);
        return revision;
    }

    /// <summary>
    /// 执行一次「立即推送」。
    /// <para>
    /// 推送不改变配置内容，只改变生效时机：全局推送递增所有设备的推送世代号，
    /// 定向推送只递增目标设备的世代号。客户端因此可以精确统计「哪些设备还没取到新配置」。
    /// </para>
    /// </summary>
    /// <param name="request">推送请求。</param>
    /// <param name="issuedBy">发起人（写进下发记录供报表展示；取不到时传空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响设备数与当前全局配置版本号。</returns>
    public async Task<(int Affected, long Revision)> PushAsync(PushRequest request,
        string? issuedBy = null, CancellationToken cancellationToken = default)
    {
        var scope = (request.Scope ?? "all").Trim().ToLowerInvariant();

        // 大校「一键全推」保护：分批推进推送世代号，避免数百台设备在同一秒涌上来拉配置。
        // 默认关闭（PushBatchSize = 0），走下面的一次性路径。
        if (_options.PushBatchSize > 0)
        {
            return await PushInBatchesAsync(scope, request, issuedBy, cancellationToken);
        }

        int affected;
        // 这一批发给了谁 —— 报表要靠它算「还有多少台没跟上」，所以三个分支都要填。
        List<string> targetDeviceIds;

        switch (scope)
        {
            case "all":
                affected = await store.BumpPushEpochAllAsync(cancellationToken);
                // 「全部」这条路径只递增世代号、自己不解析设备列表，这里补一次查询。
                targetDeviceIds = (await store.GetDevicesAsync(cancellationToken))
                    .Where(d => !d.Revoked)
                    .Select(d => d.Id)
                    .ToList();
                break;

            case "group":
            {
                // 分组是树形（楼栋 → 楼层），推送给楼栋时要覆盖其下楼层里的设备。
                var groups = await store.GetGroupsAsync(cancellationToken);
                var ids = new List<string>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Queue<string>(request.TargetIds);

                while (pending.Count > 0)
                {
                    var groupId = pending.Dequeue();
                    if (!visited.Add(groupId))
                    {
                        continue;
                    }

                    var devices = await store.GetDevicesByGroupAsync(groupId, cancellationToken);
                    ids.AddRange(devices.Where(d => !d.Revoked).Select(d => d.Id));

                    foreach (var child in groups.Where(g => string.Equals(g.ParentId, groupId, StringComparison.Ordinal)))
                    {
                        pending.Enqueue(child.Id);
                    }
                }

                ids = ids.Distinct(StringComparer.Ordinal).ToList();
                if (ids.Count == 0)
                {
                    throw HubException.Validation("所选分组（含下级）内没有可推送的设备。");
                }

                affected = await store.BumpPushEpochAsync(ids, cancellationToken);
                targetDeviceIds = ids;
                break;
            }

            case "device":
            {
                var devices = new List<DeviceRow>();
                foreach (var deviceId in request.TargetIds)
                {
                    var device = await store.GetDeviceAsync(deviceId, cancellationToken);
                    if (device is not null && !device.Revoked)
                    {
                        devices.Add(device);
                    }
                }

                if (devices.Count == 0)
                {
                    throw HubException.Validation("所选设备不存在或已被停用。");
                }

                affected = await store.BumpPushEpochAsync(devices.Select(d => d.Id), cancellationToken);
                targetDeviceIds = devices.Select(d => d.Id).ToList();
                break;
            }

            default:
                throw HubException.Validation($"不支持的下发范围：{request.Scope}。");
        }

        // 附带消息写入设置，由心跳应答在有效期内带回给客户端展示。
        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            await store.SetSettingAsync("push_message", request.Message.Trim(), cancellationToken);
            await store.SetSettingAsync("push_message_expires",
                Ts(DateTimeOffset.UtcNow.AddMinutes(10)), cancellationToken);
        }

        var revision = await store.GetRevisionAsync(cancellationToken);

        // 留痕（#64）：所有下发入口都汇聚到这里，记在这儿才不会漏。
        await RecordSyncPushSafeAsync(scope, revision, issuedBy, request, targetDeviceIds, cancellationToken);

        // 定向推送不改变全局版本号，因此这里只唤醒等待者重新判定自身条件。
        notifier.Publish(scope == "all" ? revision : null);

        return (affected, revision);
    }

    /// <summary>
    /// 记录下发批次，失败只忽略不抛出（#64）。
    /// <para>报表是**观测**：写不进去最多是少一行统计，绝不该让「下发」这个真正的动作失败。</para>
    /// </summary>
    private async Task RecordSyncPushSafeAsync(string scope, long revision, string? issuedBy,
        PushRequest request, IReadOnlyList<string> targetDeviceIds, CancellationToken cancellationToken)
    {
        try
        {
            await store.RecordSyncPushAsync(new SyncPushRecord(
                Guid.NewGuid().ToString("n"),
                revision,
                scope,
                DateTimeOffset.UtcNow,
                issuedBy ?? string.Empty,
                request.Message?.Trim() ?? string.Empty,
                request.Force), targetDeviceIds, cancellationToken);
        }
        catch (Exception)
        {
            // 有意吞掉：统计缺失比「下发失败」轻得多。
        }
    }

    /// <summary>
    /// 分批下发：每批只推进 N 台设备的推送世代号并唤醒一次，批间留出间隔。
    /// <para>推送只改「生效时机」而不改内容，所以分批是安全的——所有设备最终都会拿到同一份配置，
    /// 但不会在同一秒一起涌上来。</para>
    /// </summary>
    private async Task<(int Affected, long Revision)> PushInBatchesAsync(string scope, PushRequest request,
        string? issuedBy, CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, _options.PushBatchSize);
        var delaySeconds = Math.Max(1, _options.PushBatchDelaySeconds);

        var all = await store.GetDevicesAsync(cancellationToken);
        var targets = scope switch
        {
            "all" => all.Where(d => !d.Revoked).ToList(),
            "group" => await ExpandGroupTargetsAsync(all, request.TargetIds, cancellationToken),
            "device" => all.Where(d => !d.Revoked && request.TargetIds.Contains(d.Id, StringComparer.Ordinal)).ToList(),
            _ => throw HubException.Validation($"不支持的下发范围：{scope}。"),
        };

        if (targets.Count == 0)
        {
            throw HubException.Validation(scope switch
            {
                "group" => "所选分组（含下级）内没有可推送的设备。",
                "device" => "所选设备不存在或已被停用。",
                _ => "没有可推送的设备。",
            });
        }

        // 附带消息与一次性路径保持一致：写入设置后由心跳应答在有效期内带回。
        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            await store.SetSettingAsync("push_message", request.Message.Trim(), cancellationToken);
            await store.SetSettingAsync("push_message_expires",
                Ts(DateTimeOffset.UtcNow.AddMinutes(10)), cancellationToken);
        }

        var affected = 0;
        var batches = (int)Math.Ceiling(targets.Count / (double)batchSize);
        for (var index = 0; index < batches; index++)
        {
            var chunk = targets.Skip(index * batchSize).Take(batchSize).Select(d => d.Id).ToList();
            affected += await store.BumpPushEpochAsync(chunk, cancellationToken);

            var currentRevision = await store.GetRevisionAsync(cancellationToken);
            notifier.Publish(scope == "all" ? currentRevision : null);

            if (index < batches - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
        }

        var revision = await store.GetRevisionAsync(cancellationToken);

        // 分批只是「错峰」，对管理员来说仍是一次下发动作，所以报表里记成**一批**。
        // 快照在这里落：循环已经把每台设备的 push_epoch 推到本批要求的世代号了。
        await RecordSyncPushSafeAsync(scope, revision, issuedBy, request,
            targets.Select(d => d.Id).ToList(), cancellationToken);

        return (affected, revision);
    }

    /// <summary>把分组（含下级楼层）展开成目标设备列表。</summary>
    private async Task<List<DeviceRow>> ExpandGroupTargetsAsync(List<DeviceRow> all, List<string> groupIds,
        CancellationToken cancellationToken)
    {
        var groups = await store.GetGroupsAsync(cancellationToken);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(groupIds);

        while (pending.Count > 0)
        {
            var groupId = pending.Dequeue();
            if (!visited.Add(groupId))
            {
                continue;
            }

            foreach (var device in all.Where(d => !d.Revoked
                         && string.Equals(d.GroupId, groupId, StringComparison.Ordinal)))
            {
                wanted.Add(device.Id);
            }

            foreach (var child in groups.Where(g => string.Equals(g.ParentId, groupId, StringComparison.Ordinal)))
            {
                pending.Enqueue(child.Id);
            }
        }

        return all.Where(d => wanted.Contains(d.Id)).ToList();
    }

    /// <summary>
    /// 仅唤醒等待者重新判定自身条件，不改变全局版本号。
    /// 用于定向推送（只影响部分设备的推送世代号）场景。
    /// </summary>
    public void PublishWakeUp() => notifier.Publish();

    /// <summary>读取仍然有效的推送附带消息。</summary>
    public async Task<string?> TakePushMessageAsync(CancellationToken cancellationToken = default)
    {
        var expiresText = await store.GetSettingAsync("push_message_expires", null, cancellationToken);
        if (expiresText is null || !DateTimeOffset.TryParse(expiresText, out var expires))
        {
            return null;
        }

        if (expires < DateTimeOffset.UtcNow)
        {
            return null;
        }

        return await store.GetSettingAsync("push_message", null, cancellationToken);
    }

    private static string Ts(DateTimeOffset value) => value.ToString("O");
}
