using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Services;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 考试模式（`#21`）：把选定教室的档案**临时**换成考试档案，考完一键还原。
///
/// <para>权限沿用**设备管理**（<c>devices.read</c> / <c>devices.write</c>）：它做的正是
/// 「改一批设备的档案 + 下发」，另发一套权限键只会造出「能下发配置但不能开考试模式」这种怪组合。</para>
///
/// <para><b>还原靠的是「当时那一行的原值」，不是一个默认值</b>：设备本来没单独指定档案时
/// 用的是分组默认档案，还原必须把它写回 <c>NULL</c>——否则一次考试之后，全校设备都会
/// 被钉死在某个档案上，而管理员在界面上看不出为什么。</para>
/// </summary>
public static class ExamModeEndpoints
{
    /// <summary>默认的说明文案（管理员没填时用）。</summary>
    private const string DefaultMessage = "考试进行中，请勿操作教室设备。";

    /// <summary>通知类指令的有效期：给足两小时（考试期间教室端可能一直没拉指令）。</summary>
    private static readonly TimeSpan NotifyTtl = TimeSpan.FromHours(2);

    /// <summary>注册考试模式路由。</summary>
    public static void MapExamModeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/exam-mode", StatusAsync).RequirePermission(PermissionKeys.DevicesRead);
        group.MapPost("/exam-mode/start", StartAsync).RequirePermission(PermissionKeys.DevicesWrite);
        group.MapPost("/exam-mode/end", EndAsync).RequirePermission(PermissionKeys.DevicesWrite);
    }

    /// <summary>当前（或最近一次）的考试模式状态。</summary>
    private static async Task<ApiResult<ExamModeDto>> StatusAsync(
        HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        return ApiResult<ExamModeDto>.Success(await BuildStatusAsync(store, cancellationToken));
    }

    /// <summary>开始考试模式：切档案 → 记原值 → 下发 → （可选）通知教室端。</summary>
    private static async Task<ApiResult<ExamModeDto>> StartAsync(
        ExamModeStartRequest request, HttpContext http, HubStore store, SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        if (await store.GetActiveExamModeAsync(cancellationToken) is not null)
        {
            throw HubException.Validation("已有一个进行中的考试模式，请先结束它——"
                + "否则「原档案」会被覆盖成考试档案，考完就还原不回去了。");
        }

        if (string.IsNullOrWhiteSpace(request.ProfileId))
        {
            throw HubException.Validation("请选择考试用的配置档案。");
        }

        var profile = await store.GetProfileAsync(request.ProfileId, cancellationToken)
            ?? throw HubException.Validation("指定的档案不存在，可能已被删除。");

        var devices = await ResolveTargetsAsync(store, request, cancellationToken);
        if (devices.Count == 0)
        {
            throw HubException.Validation("这个范围里没有匹配到教室（已停用的设备不计入）。");
        }

        var now = DateTimeOffset.UtcNow;
        var modeId = Guid.NewGuid().ToString("N");
        var message = string.IsNullOrWhiteSpace(request.Message) ? DefaultMessage : request.Message.Trim();
        var targets = devices.Select(d => new HubStore.ExamModeTargetRow
        {
            ExamModeId = modeId,
            DeviceId = d.Id,
            DeviceName = d.Name,
            // 空串按 NULL 存：它代表「当时没有单独指定档案」，还原时要写回 NULL。
            PreviousProfileId = string.IsNullOrEmpty(d.ProfileId) ? null : d.ProfileId,
        }).ToList();

        foreach (var device in devices)
        {
            await store.SetDeviceProfileAsync(device.Id, profile.Id, cancellationToken);
        }

        await store.CreateExamModeAsync(new HubStore.ExamModeRow
        {
            Id = modeId,
            ProfileId = profile.Id,
            Message = message,
            StartedAt = now,
            StartedBy = session.Username,
        }, targets, cancellationToken);

        // 改完档案必须推一次：教室端不会自己发现档案变了（推送世代号才是它的信号）。
        await sync.PushAsync(new PushRequest
        {
            Scope = "device",
            TargetIds = devices.Select(d => d.Id).ToList(),
            Message = $"考试模式：已切到「{profile.Name}」",
        }, session.Username, cancellationToken);

        if (request.Notify)
        {
            await NotifyAsync(store, devices, message, now, cancellationToken);
        }

        await store.AddAuditAsync(session.Username, "exam.start", profile.Name,
            $"开启考试模式：{devices.Count} 台教室切到档案「{profile.Name}」。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<ExamModeDto>.Success(await BuildStatusAsync(store, cancellationToken));
    }

    /// <summary>结束考试模式：按记录的原值逐个还原档案 → 下发。</summary>
    private static async Task<ApiResult<ExamModeDto>> EndAsync(
        HttpContext http, HubStore store, SyncService sync, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var active = await store.GetActiveExamModeAsync(cancellationToken)
            ?? throw HubException.Validation("当前没有进行中的考试模式。");

        var (mode, targets) = active;

        foreach (var target in targets)
        {
            // 关键：还原成**原值**（可能是 NULL），不是某个默认档案。
            await store.SetDeviceProfileAsync(target.DeviceId,
                string.IsNullOrEmpty(target.PreviousProfileId) ? null : target.PreviousProfileId,
                cancellationToken);
        }

        await store.EndExamModeAsync(mode.Id, DateTimeOffset.UtcNow, cancellationToken);

        if (targets.Count > 0)
        {
            await sync.PushAsync(new PushRequest
            {
                Scope = "device",
                TargetIds = targets.Select(t => t.DeviceId).ToList(),
                Message = "考试模式已结束：已恢复原档案",
            }, session.Username, cancellationToken);
        }

        await store.AddAuditAsync(session.Username, "exam.end", mode.ProfileId,
            $"结束考试模式：{targets.Count} 台教室已恢复各自原档案。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<ExamModeDto>.Success(await BuildStatusAsync(store, cancellationToken));
    }

    /// <summary>给目标教室排一条通知指令（复用提醒那套下发通道）。</summary>
    private static async Task NotifyAsync(HubStore store, IReadOnlyList<DeviceRow> devices, string message,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var device in devices)
        {
            await store.CreateCommandAsync(new RemoteCommandRow
            {
                Id = Guid.NewGuid().ToString("N"),
                DeviceId = device.Id,
                Kind = RemoteCommandKinds.Notify,
                Payload = HubJson.Serialize(new NotifyRequestDto
                {
                    Title = "考试模式",
                    Message = message,
                }),
                Status = "pending",
                IssuedAt = now,
                IssuedBy = "exam-mode",
                ExpiresAt = now.Add(NotifyTtl),
            }, cancellationToken);
        }
    }

    /// <summary>组装状态（含目标教室与各自的原档案名）。</summary>
    private static async Task<ExamModeDto> BuildStatusAsync(HubStore store, CancellationToken cancellationToken)
    {
        var active = await store.GetActiveExamModeAsync(cancellationToken);
        if (active is null)
        {
            return new ExamModeDto();
        }

        var (mode, targets) = active.Value;
        var profile = await store.GetProfileAsync(mode.ProfileId, cancellationToken);
        var dto = new ExamModeDto
        {
            Active = true,
            ProfileId = mode.ProfileId,
            ProfileName = profile?.Name ?? "（档案已删除）",
            Message = mode.Message,
            StartedAt = mode.StartedAt,
            StartedBy = mode.StartedBy,
            TargetCount = targets.Count,
        };

        foreach (var target in targets)
        {
            var previous = string.IsNullOrEmpty(target.PreviousProfileId)
                ? null
                : await store.GetProfileAsync(target.PreviousProfileId, cancellationToken);
            dto.Targets.Add(new ExamModeTargetDto
            {
                DeviceId = target.DeviceId,
                DeviceName = target.DeviceName,
                PreviousProfileId = target.PreviousProfileId ?? string.Empty,
                PreviousProfileName = previous?.Name ?? string.Empty,
            });
        }

        return dto;
    }

    /// <summary>按范围取目标教室（已停用的设备不算）。</summary>
    private static async Task<List<DeviceRow>> ResolveTargetsAsync(HubStore store,
        ExamModeStartRequest request, CancellationToken cancellationToken)
    {
        var all = (await store.GetDevicesAsync(cancellationToken)).Where(d => !d.Revoked).ToList();
        var scope = (request.Scope ?? "all").Trim().ToLowerInvariant();
        var ids = request.TargetIds ?? [];

        return scope switch
        {
            "group" => all.Where(d => d.GroupId is not null && ids.Contains(d.GroupId)).ToList(),
            "device" => all.Where(d => ids.Contains(d.Id)).ToList(),
            _ => all,
        };
    }
}
