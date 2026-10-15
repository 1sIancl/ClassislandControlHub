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
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/profiles", ListAsync).RequirePermission(PermissionKeys.ProfilesRead);
        // 冲突检测（#44）：扫描全部档案，提前发现「引用坏了 / 时间重叠 / 同一老师被排到两个班」。
        // 路径段是字面量，路由优先级高于 /profiles/{id}，不会被当成档案 ID。
        group.MapGet("/profiles/conflicts", DetectConflictsAsync).RequirePermission(PermissionKeys.ProfilesRead);
        group.MapPost("/profiles", CreateAsync).RequirePermission(PermissionKeys.ProfilesWrite);
        // 档案复制：在既有配置上改出新方案（例如「夏季作息」→「夏季作息（九年级）」）
        group.MapPost("/profiles/{id}/duplicate", DuplicateAsync).RequirePermission(PermissionKeys.ProfilesWrite);
        group.MapPost("/profiles/sample", CreateSampleAsync).RequirePermission(PermissionKeys.ProfilesWrite);
        group.MapGet("/profiles/{id}", GetAsync).RequirePermission(PermissionKeys.ProfilesRead);
        group.MapPut("/profiles/{id}", UpdateAsync).RequirePermission(PermissionKeys.ProfilesWrite);
        group.MapDelete("/profiles/{id}", DeleteAsync).RequirePermission(PermissionKeys.ProfilesWrite);
        group.MapPost("/profiles/{id}/default", SetDefaultAsync).RequirePermission(PermissionKeys.ProfilesWrite);
        group.MapPost("/profiles/{id}/push", PushAsync).RequirePermission(PermissionKeys.DeployWrite);
        group.MapPost("/profiles/import-cses", ImportCsesAsync).RequirePermission(PermissionKeys.ProfilesWrite);
        // 从 ClassIsland 的 .cidata 数据迁移包导入：先上传解析出预览，确认后再落库。
        // 分两步是因为包里的东西**只有四块能导进来**（时间表 / 课表 / 科目 / 课表群），
        // 管理员得先看清「哪些会丢」再决定写不写。
        group.MapPost("/profiles/import-cidata/parse", ImportCidataParseAsync)
            .RequirePermission(PermissionKeys.ProfilesWrite)
            .DisableAntiforgery();
        group.MapPost("/profiles/import-cidata/apply", ImportCidataApplyAsync)
            .RequirePermission(PermissionKeys.ProfilesWrite);

        // 历史版本：保存前自动留快照，改坏了可以一键回滚
        group.MapGet("/profiles/{id}/versions", ListVersionsAsync).RequirePermission(PermissionKeys.ProfilesRead);
        group.MapPost("/profiles/{id}/versions/{versionId}/restore", RestoreVersionAsync)
            .RequirePermission(PermissionKeys.ProfilesWrite);
        group.MapDelete("/profiles/{id}/versions/{versionId}", DeleteVersionAsync)
            .RequirePermission(PermissionKeys.ProfilesWrite);
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

    /// <summary>扫描全部档案的冲突（#44）。</summary>
    private static async Task<ApiResult<ProfileConflictReportDto>> DetectConflictsAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var report = await store.DetectProfileConflictsAsync(cancellationToken);
        return ApiResult<ProfileConflictReportDto>.Success(report);
    }

    /// <summary>
    /// 复制一个档案（含课表 / 时间表 / 科目 / 自定义设置），用于在既有配置上改出新方案。
    /// <para>内容**原样搬运**（直接沿用源档案的 JSON 文本，不重新序列化）——
    /// 重新序列化可能引入字段顺序或默认值差异，让「复制品」与原件对不上。</para>
    /// <para>复制品总是新建的一版（Revision = 1）且**不继承「默认档案」标记**：
    /// 复制这个动作不该悄悄改变设备的生效配置。</para>
    /// </summary>
    private static async Task<ApiResult<ProfileSaveResult>> DuplicateAsync(
        string id,
        ProfileUpsertRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var source = await store.GetProfileAsync(id, cancellationToken)
                     ?? throw HubException.NotFound("配置档案不存在。");

        var name = string.IsNullOrWhiteSpace(request.Name) ? $"{source.Name}（副本）" : request.Name.Trim();
        var content = HubJson.DeserializeOrDefault(source.Content, new ContentBundleDto());
        var notes = ContentNormalizer.Normalize(content);

        var row = new ProfileRow
        {
            Id = HubChecksum.NewId(),
            Name = name,
            Description = string.IsNullOrWhiteSpace(request.Description)
                ? source.Description
                : request.Description.Trim(),
            Code = await GenerateUniqueCodeAsync(store, cancellationToken),
            Revision = 1,
            Content = source.Content,
            IsDefault = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateProfileAsync(row, cancellationToken);
        var revision = await sync.BumpRevisionAsync(cancellationToken);

        await store.AddAuditAsync(session.Username, "profile.duplicate", row.Name,
            $"由「{source.Name}」复制出「{row.Name}」（内容原样搬移，未绑定到任何设备）。",
            http.GetClientIpAddress(), cancellationToken);

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

        if (contentChanged)
        {
            // 保存前先给「旧内容」留一份快照：换课 / 改课表改坏时可以一键回滚。
            await store.CreateProfileVersionAsync(new ProfileVersionRow
            {
                Id = HubChecksum.NewId(),
                ProfileId = id,
                Revision = existing.Revision,
                Name = existing.Name,
                Description = existing.Description,
                Content = existing.Content,
                Reason = "保存前自动备份",
                CreatedBy = session.Username,
                CreatedAt = DateTimeOffset.UtcNow,
            }, cancellationToken);
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
        await store.DeleteProfileVersionsOfProfileAsync(id, cancellationToken);
        await store.DeleteTimetableOverridesOfProfileAsync(id, cancellationToken);
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

    /// <summary>从 CSES（The Course Schedule Exchange Schema）文本导入时间表与科目到指定档案。</summary>
    private static async Task<ApiResult<CsesImportResult>> ImportCsesAsync(
        CsesImportRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        if (string.IsNullOrWhiteSpace(request.Yaml))
        {
            throw HubException.Validation("CSES 内容为空。");
        }

        var profile = await store.GetProfileAsync(request.ProfileId, cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        CsesImporter.CsesDocument cses;
        try
        {
            cses = CsesImporter.Parse(request.Yaml);
        }
        catch (Exception ex)
        {
            throw HubException.Validation("CSES 解析失败：" + ex.Message);
        }

        var imported = CsesImporter.ToContent(cses);
        var content = HubJson.DeserializeOrDefault(profile.Content, new ContentBundleDto());

        // CSES 会一并并入逐格课表（按 enable_day 生成），与 AI 导入保持一致。
        var merged = ContentMerger.Merge(content, imported, includeClassPlans: true);

        var updated = await store.UpdateProfileAsync(profile.Id, profile.Name, profile.Description,
            HubJson.Serialize(content), true, cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        var revision = await sync.BumpRevisionAsync(cancellationToken);

        await store.AddAuditAsync(session.Username, "profile.import-cses", profile.Name,
            $"从 CSES 导入：新增科目 {merged.AddedSubjects} 个、时间表 {merged.AddedTimeLayouts} 个、"
            + $"课表 {merged.AddedClassPlans} 张（覆盖 {merged.UpdatedClassPlans} 张）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<CsesImportResult>.Success(new CsesImportResult
        {
            Profile = ToDto(updated, includeContent: true),
            AddedSubjects = merged.AddedSubjects,
            EnrichedSubjects = merged.EnrichedSubjects,
            AddedTimeLayouts = merged.AddedTimeLayouts,
            AddedClassPlans = merged.AddedClassPlans,
            UpdatedClassPlans = merged.UpdatedClassPlans,
            Revision = revision,
        });
    }

    /// <summary>
    /// 解析上传的 `.cidata`（ClassIsland 数据迁移包）并返回预览，**不写库**。
    ///
    /// <para>
    /// 上传的包先落进 `data/imports/` 临时目录换一个一次性 token，确认导入时按 token 取回——
    /// 这样几十 MB 的包不必传两遍，服务端也不用挂着会话状态。
    /// </para>
    /// </summary>
    private static async Task<ApiResult<CidataPreviewResult>> ImportCidataParseAsync(
        HttpContext http,
        CidataImportStore imports,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();

        var form = await http.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["file"];
        if (file is null || file.Length == 0)
        {
            throw HubException.Validation("请选择要导入的 .cidata 文件（表单字段名 file）。");
        }

        var token = await imports.SaveAsync(file.OpenReadStream(), cancellationToken);

        CidataArchive archive;
        try
        {
            await using var stream = File.OpenRead(imports.ResolvePath(token)!);
            archive = CidataImporter.Inspect(stream);
        }
        catch (InvalidDataException ex)
        {
            imports.Remove(token);
            throw HubException.Validation(ex.Message);
        }
        catch (Exception ex) when (ex is not HubException)
        {
            imports.Remove(token);
            throw HubException.Validation("解析 .cidata 失败：" + ex.Message);
        }

        return ApiResult<CidataPreviewResult>.Success(new CidataPreviewResult
        {
            Token = token,
            FileName = Path.GetFileName(file.FileName),
            Profiles = archive.Profiles.Select(ToPreview).ToList(),
            NotImported = archive.NotImportedNotes(),
        });
    }

    /// <summary>把 `.cidata` 里的某一份档案导入到指定档案（合并语义，与 CSES / AI 导入一致）。</summary>
    private static async Task<ApiResult<CidataImportResult>> ImportCidataApplyAsync(
        CidataImportRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CidataImportStore imports,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        var path = imports.ResolvePath(request.Token)
                   ?? throw HubException.Validation(
                       "导入会话已过期（临时文件只保留 2 小时），请重新上传 .cidata 文件。");

        var profile = await store.GetProfileAsync(request.ProfileId, cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        CidataProfileEntry entry;
        try
        {
            await using var stream = File.OpenRead(path);
            var archive = CidataImporter.Inspect(stream);
            entry = archive.Profiles.FirstOrDefault(p =>
                        string.Equals(p.FileName, request.ProfileFile, StringComparison.OrdinalIgnoreCase))
                    ?? throw HubException.Validation("包里没有这份档案，请重新上传后再试。");
        }
        catch (InvalidDataException ex)
        {
            imports.Remove(request.Token);
            throw HubException.Validation(ex.Message);
        }

        var content = HubJson.DeserializeOrDefault(profile.Content, new ContentBundleDto());
        var merged = ContentMerger.Merge(content, entry.Content, includeClassPlans: true);

        // 与「保存档案」走同一条规范化路径：悬空引用、越界节次等当场修掉并如实回报，
        // 而不是写出一份接口看着没问题、教室端却渲染不出来的档案。
        var repairs = ContentNormalizer.Normalize(content);

        var updated = await store.UpdateProfileAsync(profile.Id, profile.Name, profile.Description,
            HubJson.Serialize(content), true, cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        var revision = await sync.BumpRevisionAsync(cancellationToken);
        sync.PublishWakeUp();

        // 导入成功后删掉临时包：它是整份教室端配置的副本，留着没有意义还占盘。
        imports.Remove(request.Token);

        var skipped = entry.Warnings.Count > 0
            ? $"；另有 {entry.Warnings.Count} 处内容集控装不下，已在预览里说明"
            : string.Empty;

        await store.AddAuditAsync(session.Username, "profile.import-cidata", profile.Name,
            $"从 .cidata（档案「{entry.ProfileName}」）导入：新增科目 {merged.AddedSubjects} 个、"
            + $"时间表 {merged.AddedTimeLayouts} 个、课表 {merged.AddedClassPlans} 张"
            + $"（覆盖 {merged.UpdatedClassPlans} 张）{skipped}。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<CidataImportResult>.Success(new CidataImportResult
        {
            Profile = ToDto(updated, includeContent: true),
            AddedSubjects = merged.AddedSubjects,
            EnrichedSubjects = merged.EnrichedSubjects,
            AddedTimeLayouts = merged.AddedTimeLayouts,
            AddedClassPlans = merged.AddedClassPlans,
            UpdatedClassPlans = merged.UpdatedClassPlans,
            Revision = revision,
            Repairs = repairs,
        });
    }

    /// <summary>把解析结果压成预览用的小对象（不把整份内容包发给浏览器）。</summary>
    private static CidataProfilePreview ToPreview(CidataProfileEntry entry) => new()
    {
        FileName = entry.FileName,
        ProfileName = entry.ProfileName,
        Subjects = entry.Content.Subjects.Count,
        TimeLayouts = entry.Content.TimeLayouts.Count,
        ClassPlans = entry.Content.ClassPlans.Count,
        ClassPlanNames = entry.Content.ClassPlans.Select(p => p.Name).Take(8).ToList(),
        Groups = entry.Groups,
        OrderedSchedules = entry.SkippedOrderedSchedules,
        OverlayPlans = entry.SkippedOverlayPlans,
        Warnings = [.. entry.Warnings],
    };

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

    // ────────────────────────────── 历史版本（快照与回滚） ──────────────────────────────

    private static ProfileVersionDto ToVersionDto(ProfileVersionRow row) => new()
    {
        Id = row.Id,
        ProfileId = row.ProfileId,
        Revision = row.Revision,
        Name = row.Name,
        Description = row.Description,
        Reason = row.Reason,
        CreatedBy = row.CreatedBy,
        CreatedAt = row.CreatedAt,
    };

    /// <summary>档案的历史版本列表（不含内容，减少传输量）。</summary>
    private static async Task<ApiResult<List<ProfileVersionDto>>> ListVersionsAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        if (await store.GetProfileAsync(id, cancellationToken) is null)
        {
            throw HubException.NotFound("配置档案不存在。");
        }

        var rows = await store.GetProfileVersionsAsync(id, cancellationToken);
        return ApiResult<List<ProfileVersionDto>>.Success(rows.Select(ToVersionDto).ToList());
    }

    /// <summary>回滚到指定历史版本；回滚前会先把「当前内容」也留一份快照。</summary>
    private static async Task<ApiResult<ProfileSaveResult>> RestoreVersionAsync(
        string id,
        string versionId,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var profile = await store.GetProfileAsync(id, cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        var version = await store.GetProfileVersionAsync(versionId, cancellationToken);
        if (version is null || !string.Equals(version.ProfileId, id, StringComparison.OrdinalIgnoreCase))
        {
            throw HubException.NotFound("历史版本不存在。");
        }

        var content = HubJson.DeserializeOrDefault(version.Content, new ContentBundleDto());
        var notes = ContentNormalizer.Normalize(content);

        // 回滚本身也是一次内容变更：先把当前内容存一份，避免「回滚后又想回到刚才」。
        await store.CreateProfileVersionAsync(new ProfileVersionRow
        {
            Id = HubChecksum.NewId(),
            ProfileId = id,
            Revision = profile.Revision,
            Name = profile.Name,
            Description = profile.Description,
            Content = profile.Content,
            Reason = "回滚前自动备份",
            CreatedBy = session.Username,
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

        var updated = await store.UpdateProfileAsync(id, profile.Name, profile.Description,
            HubJson.Serialize(content), true, cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        var revision = await sync.BumpRevisionAsync(cancellationToken);
        await store.AddAuditAsync(session.Username, "profile.restore", updated.Name,
            $"回滚到 {version.CreatedAt:yyyy-MM-dd HH:mm} 的快照（版本 v{version.Revision}）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<ProfileSaveResult>.Success(new ProfileSaveResult
        {
            Profile = ToDto(updated, includeContent: true),
            Notes = notes,
            Revision = revision,
        });
    }

    /// <summary>删除某个历史版本。</summary>
    private static async Task<ApiResult<bool>> DeleteVersionAsync(
        string id,
        string versionId,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var version = await store.GetProfileVersionAsync(versionId, cancellationToken);
        if (version is null || !string.Equals(version.ProfileId, id, StringComparison.OrdinalIgnoreCase))
        {
            throw HubException.NotFound("历史版本不存在。");
        }

        await store.DeleteProfileVersionAsync(versionId, cancellationToken);
        await store.AddAuditAsync(session.Username, "profile.version.delete", version.Name,
            "删除了一份档案历史版本。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(true);
    }
}

/// <summary>配置档案历史版本（快照）。</summary>
public sealed class ProfileVersionDto
{
    /// <summary>快照 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>所属档案 ID。</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>快照对应的档案内容版本号。</summary>
    public long Revision { get; set; }

    /// <summary>快照时的档案名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>快照时的档案说明。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>产生快照的原因（保存前 / 回滚前自动备份）。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>操作人。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>从 CSES 导入的请求体。</summary>
public sealed class CsesImportRequest
{
    /// <summary>目标配置档案 ID。</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>CSES YAML 文本内容。</summary>
    public string Yaml { get; set; } = string.Empty;
}

/// <summary>从 CSES 导入的结果。</summary>
public sealed class CsesImportResult
{
    /// <summary>导入后的档案。</summary>
    public ProfileDto Profile { get; set; } = new();

    /// <summary>新增的科目数量。</summary>
    public int AddedSubjects { get; set; }

    /// <summary>被补全了简称或教师的科目数量。</summary>
    public int EnrichedSubjects { get; set; }

    /// <summary>新增的时间表数量。</summary>
    public int AddedTimeLayouts { get; set; }

    /// <summary>新增的课表数量。</summary>
    public int AddedClassPlans { get; set; }

    /// <summary>被覆盖更新的课表数量。</summary>
    public int UpdatedClassPlans { get; set; }

    /// <summary>保存后的全局配置版本号。</summary>
    public long Revision { get; set; }
}

/// <summary>`.cidata` 上传解析后的预览。</summary>
public sealed class CidataPreviewResult
{
    /// <summary>一次性导入令牌：确认导入时带上它，服务端据此取回临时文件。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>上传时的原始文件名。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>包里包含的档案（ClassIsland 的 `Profiles/*.json`，可能有多份）。</summary>
    public List<CidataProfilePreview> Profiles { get; set; } = [];

    /// <summary>包里那些集控装不下的部分（应用设置 / 其它配置 / 插件），逐条说明。</summary>
    public List<string> NotImported { get; set; } = [];
}

/// <summary>包里一份档案的预览。</summary>
public sealed class CidataProfilePreview
{
    /// <summary>包内文件名（确认导入时用它指定这一份）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>档案名。</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>能导入的科目数。</summary>
    public int Subjects { get; set; }

    /// <summary>能导入的时间表数。</summary>
    public int TimeLayouts { get; set; }

    /// <summary>能导入的课表数。</summary>
    public int ClassPlans { get; set; }

    /// <summary>前几张课表的名称（让管理员能对上号）。</summary>
    public List<string> ClassPlanNames { get; set; } = [];

    /// <summary>包内定义的课表群。</summary>
    public List<CidataGroupInfo> Groups { get; set; } = [];

    /// <summary>包内按日期调休 / 换课的条数（未导入）。</summary>
    public int OrderedSchedules { get; set; }

    /// <summary>包内临时层课表的张数（未导入）。</summary>
    public int OverlayPlans { get; set; }

    /// <summary>导入会丢东西的地方，逐条写清「丢了什么、为什么」。</summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>确认导入 `.cidata` 的请求体。</summary>
public sealed class CidataImportRequest
{
    /// <summary>预览时拿到的一次性令牌。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>要导入包里哪一份档案（<see cref="CidataProfilePreview.FileName"/>）。</summary>
    public string ProfileFile { get; set; } = string.Empty;

    /// <summary>导入到哪个配置档案（合并语义）。</summary>
    public string ProfileId { get; set; } = string.Empty;
}

/// <summary>从 `.cidata` 导入的结果。</summary>
public sealed class CidataImportResult
{
    /// <summary>导入后的档案。</summary>
    public ProfileDto Profile { get; set; } = new();

    /// <summary>新增的科目数量。</summary>
    public int AddedSubjects { get; set; }

    /// <summary>被补全了简称或教师的科目数量。</summary>
    public int EnrichedSubjects { get; set; }

    /// <summary>新增的时间表数量。</summary>
    public int AddedTimeLayouts { get; set; }

    /// <summary>新增的课表数量。</summary>
    public int AddedClassPlans { get; set; }

    /// <summary>被覆盖更新的课表数量。</summary>
    public int UpdatedClassPlans { get; set; }

    /// <summary>保存后的全局配置版本号。</summary>
    public long Revision { get; set; }

    /// <summary>落库时自动修复的问题（悬空引用、越界节次等），如实回报而不是悄悄改。</summary>
    public List<string> Repairs { get; set; } = [];
}
