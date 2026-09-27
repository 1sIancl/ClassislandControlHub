using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Services;

namespace ControlHub.Server.Endpoints;

/// <summary>AI 辅助导入接口，挂载在 <c>/api/v1/admin/ai</c> 下。</summary>
public static class AiEndpoints
{
    /// <summary>注册 AI 辅助导入路由。</summary>
    public static void MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        // AI 接口地址与密钥属于系统设置；实际解析/并入档案属于档案编辑，按两者分别校验。
        group.MapGet("/ai/config", GetConfigAsync).RequirePermission(PermissionKeys.SettingsRead);
        group.MapPut("/ai/config", SetConfigAsync).RequirePermission(PermissionKeys.SettingsWrite);
        group.MapPost("/ai/test", TestAsync).RequirePermission(PermissionKeys.SettingsWrite);
        group.MapPost("/ai/parse", ParseAsync).RequirePermission(PermissionKeys.ProfilesWrite);
        group.MapPost("/ai/apply", ApplyAsync).RequirePermission(PermissionKeys.ProfilesWrite);
    }

    /// <summary>读取 AI 配置。接口密钥只回传掩码，避免明文出现在页面上。</summary>
    private static async Task<ApiResult<AiConfigDto>> GetConfigAsync(
        HttpContext http,
        AiTimetableService ai,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var config = await ai.LoadConfigAsync(cancellationToken);
        return ApiResult<AiConfigDto>.Success(ToDto(config));
    }

    /// <summary>保存 AI 配置。</summary>
    private static async Task<ApiResult<AiConfigDto>> SetConfigAsync(
        AiConfigUpdateRequest request,
        HttpContext http,
        HubStore store,
        AiTimetableService ai,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        var config = await ai.LoadConfigAsync(cancellationToken);
        config.Enabled = request.Enabled;
        config.BaseUrl = (request.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        config.Model = (request.Model ?? string.Empty).Trim();
        config.TimeoutSeconds = Math.Clamp(request.TimeoutSeconds <= 0 ? 180 : request.TimeoutSeconds, 10, 900);

        // 密钥留空表示「不修改」，需要清空时显式传 apiKeyClear。
        if (request.ApiKeyClear)
        {
            config.ApiKey = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            config.ApiKey = request.ApiKey.Trim();
        }

        await ai.SaveConfigAsync(config, cancellationToken);

        await store.AddAuditAsync(session.Username, "ai.config", "ai",
            $"更新 AI 辅助导入配置（{(config.Enabled ? "启用" : "停用")}，{config.BaseUrl}，{config.Model}）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<AiConfigDto>.Success(ToDto(config));
    }

    /// <summary>测试 AI 接口连通性。</summary>
    private static async Task<ApiResult<AiTestResult>> TestAsync(
        HttpContext http,
        AiTimetableService ai,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var (ok, message, elapsed) = await ai.TestAsync(cancellationToken);
        return ApiResult<AiTestResult>.Success(new AiTestResult
        {
            Ok = ok,
            Message = message,
            ElapsedMs = elapsed,
        });
    }

    /// <summary>解析课表文本，只返回结果供预览，不写入档案。</summary>
    private static async Task<ApiResult<AiParseResult>> ParseAsync(
        AiParseRequest request,
        HttpContext http,
        AiTimetableService ai,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();

        var bundle = await ai.ParseTimetableAsync(request.Text, cancellationToken);
        var subjects = bundle.ClassPlans
            .SelectMany(p => p.Slots)
            .Count(s => !string.IsNullOrEmpty(s.SubjectId));

        return ApiResult<AiParseResult>.Success(new AiParseResult
        {
            Bundle = bundle,
            PeriodCount = bundle.TimeLayouts.FirstOrDefault()?.Items.Count(i => i.Kind == TimeItemKind.Class) ?? 0,
            DayCount = bundle.ClassPlans.Count,
            SubjectCount = bundle.Subjects.Count,
            CourseCount = subjects,
        });
    }

    /// <summary>把预览过的解析结果合并进档案。</summary>
    private static async Task<ApiResult<AiApplyResult>> ApplyAsync(
        AiApplyRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        var profile = await store.GetProfileAsync(request.ProfileId, cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        if (request.Bundle is null || request.Bundle.TimeLayouts.Count == 0)
        {
            throw HubException.Validation("导入内容为空或缺少时间表。");
        }

        var content = HubJson.DeserializeOrDefault(profile.Content, new ContentBundleDto());
        var merged = ContentMerger.Merge(content, request.Bundle, includeClassPlans: true);

        _ = await store.UpdateProfileAsync(profile.Id, profile.Name, profile.Description,
                HubJson.Serialize(content), true, cancellationToken)
            ?? throw HubException.NotFound("配置档案不存在。");

        var revision = await sync.BumpRevisionAsync(cancellationToken);

        await store.AddAuditAsync(session.Username, "profile.ai-import", profile.Name,
            $"AI 导入：新增科目 {merged.AddedSubjects} 个、时间表 {merged.AddedTimeLayouts} 个、"
            + $"课表 {merged.AddedClassPlans} 张，更新课表 {merged.UpdatedClassPlans} 张。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<AiApplyResult>.Success(new AiApplyResult
        {
            AddedSubjects = merged.AddedSubjects,
            EnrichedSubjects = merged.EnrichedSubjects,
            AddedTimeLayouts = merged.AddedTimeLayouts,
            AddedClassPlans = merged.AddedClassPlans,
            UpdatedClassPlans = merged.UpdatedClassPlans,
            Revision = revision,
        });
    }

    private static AiConfigDto ToDto(AiConfig config) => new()
    {
        Enabled = config.Enabled,
        BaseUrl = config.BaseUrl,
        Model = config.Model,
        TimeoutSeconds = config.TimeoutSeconds,
        ApiKeySet = !string.IsNullOrEmpty(config.ApiKey),
        ApiKeyHint = MaskKey(config.ApiKey),
    };

    private static string MaskKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return string.Empty;
        }

        return apiKey.Length <= 10
            ? "已设置"
            : $"{apiKey[..4]}……{apiKey[^4..]}";
    }
}

/// <summary>AI 辅助导入配置（对外视图，不含明文密钥）。</summary>
public sealed class AiConfigDto
{
    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>接口地址。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>模型名称。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>单次请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>是否已保存过密钥。</summary>
    public bool ApiKeySet { get; set; }

    /// <summary>密钥掩码，仅用于展示。</summary>
    public string ApiKeyHint { get; set; } = string.Empty;
}

/// <summary>保存 AI 配置的请求体。</summary>
public sealed class AiConfigUpdateRequest
{
    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>接口地址。</summary>
    public string? BaseUrl { get; set; }

    /// <summary>模型名称。</summary>
    public string? Model { get; set; }

    /// <summary>单次请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>新的接口密钥。留空表示保持原值。</summary>
    public string? ApiKey { get; set; }

    /// <summary>是否清空已保存的密钥。</summary>
    public bool ApiKeyClear { get; set; }
}

/// <summary>AI 接口连通性测试结果。</summary>
public sealed class AiTestResult
{
    /// <summary>是否连通。</summary>
    public bool Ok { get; set; }

    /// <summary>结果描述。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>耗时（毫秒）。</summary>
    public long ElapsedMs { get; set; }
}

/// <summary>AI 解析课表的请求体。</summary>
public sealed class AiParseRequest
{
    /// <summary>课表原文。</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>AI 解析课表的结果（供预览）。</summary>
public sealed class AiParseResult
{
    /// <summary>解析出的内容包。</summary>
    public ContentBundleDto Bundle { get; set; } = new();

    /// <summary>作息节数。</summary>
    public int PeriodCount { get; set; }

    /// <summary>识别出的天数。</summary>
    public int DayCount { get; set; }

    /// <summary>科目数。</summary>
    public int SubjectCount { get; set; }

    /// <summary>有课的格子数。</summary>
    public int CourseCount { get; set; }
}

/// <summary>确认导入的请求体。</summary>
public sealed class AiApplyRequest
{
    /// <summary>目标配置档案 ID。</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>预览过的内容包。</summary>
    public ContentBundleDto? Bundle { get; set; }
}

/// <summary>AI 导入的合并结果。</summary>
public sealed class AiApplyResult
{
    /// <summary>新增科目数。</summary>
    public int AddedSubjects { get; set; }

    /// <summary>补全了简称或教师的科目数。</summary>
    public int EnrichedSubjects { get; set; }

    /// <summary>新增时间表数。</summary>
    public int AddedTimeLayouts { get; set; }

    /// <summary>新增课表数。</summary>
    public int AddedClassPlans { get; set; }

    /// <summary>覆盖更新的课表数。</summary>
    public int UpdatedClassPlans { get; set; }

    /// <summary>更新后的全局配置版本。</summary>
    public long Revision { get; set; }
}
