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
    /// 优先级：设备单独指定 &gt; 设备所属分组的默认档案 &gt; 全局默认档案。
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

        if (!string.IsNullOrEmpty(device.GroupId))
        {
            var group = await store.GetGroupAsync(device.GroupId, cancellationToken);
            if (group is not null && !string.IsNullOrEmpty(group.DefaultProfileId))
            {
                var fromGroup = await store.GetProfileAsync(group.DefaultProfileId, cancellationToken);
                if (fromGroup is not null)
                {
                    return fromGroup;
                }
            }
        }

        return await store.GetDefaultProfileAsync(cancellationToken);
    }

    /// <summary>
    /// 构建设备的同步应答。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="clientRevision">客户端当前持有的版本号。与服务器一致且非强制时返回 <c>null</c> 表示无需下发。</param>
    /// <param name="sections">客户端声明支持的同步分区，为空表示全部。</param>
    /// <param name="force">是否强制下发。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SyncResponse?> BuildSyncResponseAsync(DeviceRow device, long? clientRevision,
        IReadOnlyCollection<string>? sections, bool force,
        CancellationToken cancellationToken = default)
    {
        var revision = await store.GetRevisionAsync(cancellationToken);
        if (!force && clientRevision.HasValue && clientRevision.Value == revision)
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

    /// <summary>把数据库行转换为面向 Web 管理端的摘要对象。</summary>
    public DeviceSummaryDto ToSummary(DeviceRow device, GroupRow? group, long serverRevision)
    {
        var online = !device.Revoked
                     && device.LastSeenAt.HasValue
                     && (DateTimeOffset.UtcNow - device.LastSeenAt.Value).TotalSeconds < _options.OnlineTimeoutSeconds;

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
            CurrentClassPlanName = device.CurrentClassPlanName,
            LastError = device.LastError,
            Revoked = device.Revoked,
            CreatedAt = device.CreatedAt,
        };
    }

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
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响设备数与当前全局配置版本号。</returns>
    public async Task<(int Affected, long Revision)> PushAsync(PushRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = (request.Scope ?? "all").Trim().ToLowerInvariant();
        int affected;

        switch (scope)
        {
            case "all":
                affected = await store.BumpPushEpochAllAsync(cancellationToken);
                break;

            case "group":
            {
                var ids = new List<string>();
                foreach (var groupId in request.TargetIds)
                {
                    var devices = await store.GetDevicesByGroupAsync(groupId, cancellationToken);
                    ids.AddRange(devices.Where(d => !d.Revoked).Select(d => d.Id));
                }

                if (ids.Count == 0)
                {
                    throw HubException.Validation("所选分组内没有可推送的设备。");
                }

                affected = await store.BumpPushEpochAsync(ids, cancellationToken);
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

        // 定向推送不改变全局版本号，因此这里只唤醒等待者重新判定自身条件。
        notifier.Publish(scope == "all" ? revision : null);

        return (affected, revision);
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
