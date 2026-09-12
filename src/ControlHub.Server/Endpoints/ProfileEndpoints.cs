using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Services;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 配置档案接口，挂载在 <c>/api/v1/admin/profiles</c> 下。
/// <para>
/// 一个档案包含时间表、课表、科目与自定义设置，是集控下发的最小单元。
/// 档案内容变更会递增该档案版本以及全局配置版本，从而触发客户端重新同步。
/// </para>
/// </summary>
public static class ProfileEndpoints
{
    /// <summary>注册档案相关路由。</summary>
    public static void MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>();

        group.MapGet("/profiles", ListAsync);
        group.MapPost("/profiles", CreateAsync);
        group.MapPost("/profiles/sample", CreateSampleAsync);
        group.MapGet("/profiles/{id}", GetAsync);
        group.MapPut("/profiles/{id}", UpdateAsync);
        group.MapDelete("/profiles/{id}", DeleteAsync);
        group.MapPost("/profiles/{id}/default", SetDefaultAsync);
        group.MapPost("/profiles/{id}/push", PushAsync);
    }

    /// <summary>列出全部档案（不含内容，减少传输量）。</summary>
    private static async Task<ApiResult<List<ProfileDto>>> ListAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();

        var profiles = await store.GetProfilesAsync(cancellationToken);
        var bindings = await store.GetProfileBindingCountsAsync(cancellationToken);
        var devices = await store.GetDevicesAsync(cancellationToken);

        // 统计「实际生效设备数」：直接绑定 + 经分组默认档案间接生效。
        var effective = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices.Where(d => !d.Revoked))
        {
            var profileId = await ResolveEffectiveProfileIdAsync(store, device, cancellationToken);
            if (profileId is not null)
            {
                effective[profileId] = effective.GetValueOrDefault(profileId) + 1;
            }
        }

        return ApiResult<List<ProfileDto>>.Success(profiles.Select(p =>
        {
            var dto = ToDto(p, includeContent: false);
            dto.BoundDeviceCount = effective.GetValueOrDefault(p.Id, bindings.GetValueOrDefault(p.Id));
            return dto;
        }).ToList());
    }

    /// <summary>获取单个档案的完整内容。</summary>
    private static async Task<ApiResult<ProfileDto>> GetAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var profile = await store.GetProfileAsync(id, cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        return ApiResult<ProfileDto>.Success(ToDto(profile, includeContent: true));
    }

    /// <summary>新建空白档案。</summary>
    private static Task<ApiResult<ProfileSaveResult>> CreateAsync(
        ProfileUpsertRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken) =>
        CreateCoreAsync(request, http, store, sync, cancellationToken);

    /// <summary>新建档案并预填示例内容，便于快速上手。</summary>
    private static Task<ApiResult<ProfileSaveResult>> CreateSampleAsync(
        ProfileUpsertRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var payload = new ProfileUpsertRequest
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? "示例档案（可直接修改）" : request.Name,
            Description = string.IsNullOrWhiteSpace(request.Description)
                ? "由集控系统生成的示例作息与课表。"
                : request.Description,
            Content = SampleContentFactory.Create(),
        };

        return CreateCoreAsync(payload, http, store, sync, cancellationToken);
    }

    private static async Task<ApiResult<ProfileSaveResult>> CreateCoreAsync(
        ProfileUpsertRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw HubException.Validation("档案名称不能为空。");
        }

        var content = request.Content is null
            ? new ContentBundleDto()
            : ContentNormalizer.Clone(request.Content);
        var notes = ContentNormalizer.Normalize(content);

        var row = new ProfileRow
        {
            Id = HubChecksum.NewId(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            Code = await GenerateUniqueCodeAsync(store, cancellationToken),
            Revision = 1,
            Content = HubJson.Serialize(content),
            IsDefault = await store.CountProfilesAsync(cancellationToken) == 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        if (await store.GetProfileAsync(row.Id, cancellationToken) is not null)
        {
            throw HubException.Conflict("档案 ID 冲突，请重试。");
        }

        await store.CreateProfileAsync(row, cancellationToken);
        var revision = await sync.BumpRevisionAsync(cancellationToken);

        await store.AddAuditAsync(session.Username, "profile.create", row.Name,
            $"创建配置档案「{row.Name}」，内容版本 1。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<ProfileSaveResult>.Success(new ProfileSaveResult
        {
            Profile = ToDto(row, includeContent: true),
            Notes = notes,
            Revision = revision,
        });
    }

    /// <summary>更新档案。内容发生变化时递增档案与全局版本。</summary>
    private static async Task<ApiResult<ProfileSaveResult>> UpdateAsync(
        string id,
        ProfileUpsertRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var existing = await store.GetProfileAsync(id, cancellationToken)
                       ?? throw HubException.NotFound("配置档案不存在。");

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw HubException.Validation("档案名称不能为空。");
        }

        var notes = new List<string>();
        var contentChanged = request.Content is not null;
        var contentText = existing.Content;

        if (contentChanged)
        {
            var content = ContentNormalizer.Clone(request.Content!);
            notes = ContentNormalizer.Normalize(content);
            contentText = HubJson.Serialize(content);
        }

        var updated = await store.UpdateProfileAsync(id, request.Name.Trim(),
            request.Description?.Trim() ?? existing.Description, contentText, contentChanged, cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        long revision;
        if (contentChanged)
        {
            revision = await sync.BumpRevisionAsync(cancellationToken);
        }
        else
        {
            revision = await store.GetRevisionAsync(cancellationToken);
        }

        await store.AddAuditAsync(session.Username, "profile.update", updated.Name,
            contentChanged
                ? $"更新配置档案内容，档案版本升至 {updated.Revision}，全局版本 {revision}。"
                : "更新配置档案信息（内容未变）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<ProfileSaveResult>.Success(new ProfileSaveResult
        {
            Profile = ToDto(updated, includeContent: true),
            Notes = notes,
            Revision = revision,
        });
    }

    /// <summary>删除档案。</summary>
    private static async Task<ApiResult<bool>> DeleteAsync(
        string id,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var profile = await store.GetProfileAsync(id, cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        if (await store.CountProfilesAsync(cancellationToken) <= 1)
        {
            throw HubException.Validation("至少需要保留一个配置档案。");
        }

        await store.DeleteProfileAsync(id, cancellationToken);
        await sync.BumpRevisionAsync(cancellationToken);
        await store.AddAuditAsync(session.Username, "profile.delete", profile.Name,
            "删除配置档案，相关设备已恢复为继承分组/默认档案。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    /// <summary>设为默认档案。</summary>
    private static async Task<ApiResult<bool>> SetDefaultAsync(
        string id,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var profile = await store.GetProfileAsync(id, cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        await store.SetDefaultProfileAsync(id, cancellationToken);
        await sync.BumpRevisionAsync(cancellationToken);
        await store.AddAuditAsync(session.Username, "profile.setdefault", profile.Name,
            "设为默认配置档案。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<bool>.Success(true);
    }

    /// <summary>把该档案立即推送给所有实际使用它的设备。</summary>
    private static async Task<ApiResult<object>> PushAsync(
        string id,
        PushRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var profile = await store.GetProfileAsync(id, cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        var devices = await store.GetDevicesAsync(cancellationToken);
        var targets = new List<string>();
        foreach (var device in devices.Where(d => !d.Revoked))
        {
            var effective = await ResolveEffectiveProfileIdAsync(store, device, cancellationToken);
            if (string.Equals(effective, id, StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(device.Id);
            }
        }

        var affected = await store.BumpPushEpochAsync(targets, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            await store.SetSettingAsync("push_message", request.Message.Trim(), cancellationToken);
            await store.SetSettingAsync("push_message_expires",
                DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"), cancellationToken);
        }

        sync.PublishWakeUp();

        await store.AddAuditAsync(session.Username, "profile.push", profile.Name,
            $"向使用该档案的 {affected} 台设备发起推送。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<object>.Success(new { affected, profile = profile.Name });
    }

    /// <summary>
    /// 解析设备最终生效的档案 ID。与 <see cref="SyncService.ResolveProfileAsync"/> 保持一致的优先级。
    /// </summary>
    private static async Task<string?> ResolveEffectiveProfileIdAsync(HubStore store, DeviceRow device,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(device.ProfileId))
        {
            return device.ProfileId;
        }

        if (!string.IsNullOrEmpty(device.GroupId))
        {
            var group = await store.GetGroupAsync(device.GroupId, cancellationToken);
            if (!string.IsNullOrEmpty(group?.DefaultProfileId))
            {
                return group.DefaultProfileId;
            }
        }

        var fallback = await store.GetDefaultProfileAsync(cancellationToken);
        return fallback?.Id;
    }

    private static ProfileDto ToDto(ProfileRow row, bool includeContent) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Code = row.Code,
        Description = row.Description,
        Revision = row.Revision,
        Content = includeContent
            ? HubJson.DeserializeOrDefault(row.Content, new ContentBundleDto())
            : new ContentBundleDto(),
        IsDefault = row.IsDefault,
        UpdatedAt = row.UpdatedAt,
    };

    /// <summary>生成唯一的四位识别码（去易混淆字符，撞码则重试）。</summary>
    private static async Task<string> GenerateUniqueCodeAsync(HubStore store, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 10; i++)
        {
            var code = HubChecksum.NewEnrollCode(4);
            if (!await store.CodeExistsAsync(code, cancellationToken))
            {
                return code;
            }
        }

        // 极端情况下退回用 GUID 前 4 位大写，仍保证非空且大概率唯一。
        return Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
    }
}
