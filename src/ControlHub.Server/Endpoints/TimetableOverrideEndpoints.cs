using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Services;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 临时换课（跨天 / 跨周换课）接口：把某节课在一段时间内临时换成别的科目。
/// <para>
/// 覆盖<b>不写回配置档案</b>，只在该档案下发给设备时合成，
/// 因此过了截止日期设备下一次同步就自动还原；管理员也可以随时撤销。
/// </para>
/// </summary>
public static class TimetableOverrideEndpoints
{
    /// <summary>注册临时换课路由。</summary>
    public static void MapTimetableOverrideEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/timetable-overrides", ListAsync).RequirePermission(PermissionKeys.ProfilesRead);
        group.MapPost("/timetable-overrides", CreateAsync).RequirePermission(PermissionKeys.ProfilesWrite);
        group.MapDelete("/timetable-overrides/{id}", DeleteAsync).RequirePermission(PermissionKeys.ProfilesWrite);
        group.MapGet("/timetable-overrides/preview", PreviewAsync).RequirePermission(PermissionKeys.ProfilesRead);
    }

    private static TimetableOverrideDto ToDto(TimetableOverrideRow row) => new()
    {
        Id = row.Id,
        ProfileId = row.ProfileId,
        ClassPlanId = row.ClassPlanId,
        SlotIndex = row.SlotIndex,
        SubjectId = row.SubjectId,
        StartDate = row.StartDate,
        EndDate = row.EndDate,
        Weekdays = row.Weekdays,
        Reason = row.Reason,
        CreatedBy = row.CreatedBy,
        CreatedAt = row.CreatedAt,
    };

    /// <summary>某个档案的临时换课列表。</summary>
    private static async Task<ApiResult<List<TimetableOverrideDto>>> ListAsync(
        string? profileId,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw HubException.Validation("请指定 profileId。");
        }

        var rows = await store.GetTimetableOverridesAsync(profileId.Trim(), cancellationToken);
        return ApiResult<List<TimetableOverrideDto>>.Success(rows.Select(ToDto).ToList());
    }

    /// <summary>新建临时换课；建好立即唤醒设备重新拉取。</summary>
    private static async Task<ApiResult<TimetableOverrideDto>> CreateAsync(
        TimetableOverrideUpsertRequest request,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var profile = await store.GetProfileAsync((request.ProfileId ?? string.Empty).Trim(), cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        if (!DateOnly.TryParse((request.StartDate ?? string.Empty).Trim(), out var start))
        {
            throw HubException.Validation("请填写合法的开始日期（格式 yyyy-MM-dd）。");
        }

        var endText = (request.EndDate ?? string.Empty).Trim();
        if (endText.Length > 0)
        {
            if (!DateOnly.TryParse(endText, out var end))
            {
                throw HubException.Validation("结束日期格式应为 yyyy-MM-dd。");
            }

            if (end < start)
            {
                throw HubException.Validation("结束日期不能早于开始日期。");
            }
        }

        if (request.SlotIndex < 0)
        {
            throw HubException.Validation("节次不能为负数。");
        }

        var row = new TimetableOverrideRow
        {
            Id = HubChecksum.NewId(),
            ProfileId = profile.Id,
            ClassPlanId = (request.ClassPlanId ?? string.Empty).Trim(),
            SlotIndex = request.SlotIndex,
            SubjectId = string.IsNullOrWhiteSpace(request.SubjectId) ? null : request.SubjectId.Trim(),
            StartDate = start.ToString("yyyy-MM-dd"),
            EndDate = endText.Length > 0 ? DateOnly.Parse(endText).ToString("yyyy-MM-dd") : null,
            Weekdays = NormalizeWeekdays(request.Weekdays),
            Reason = (request.Reason ?? string.Empty).Trim(),
            CreatedBy = session.Username,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateTimetableOverrideAsync(row, cancellationToken);

        // 立即唤醒设备：让换课在下一次同步就生效（到期还原同理，由跨天调度负责）。
        await sync.BumpRevisionAsync(cancellationToken);

        await store.AddAuditAsync(session.Username, "timetable.override.create", profile.Name,
            $"临时换课：{row.StartDate} ~ {(row.EndDate ?? "撤销为止")} 第 {row.SlotIndex + 1} 节"
            + $"{(string.IsNullOrWhiteSpace(row.Reason) ? "" : "（" + row.Reason + "）")}。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<TimetableOverrideDto>.Success(ToDto(row));
    }

    /// <summary>撤销临时换课（立即唤醒设备还原）。</summary>
    private static async Task<ApiResult<bool>> DeleteAsync(
        string id,
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await store.GetTimetableOverrideAsync(id, cancellationToken)
                  ?? throw HubException.NotFound("换课记录不存在。");

        await store.DeleteTimetableOverrideAsync(id, cancellationToken);
        await sync.BumpRevisionAsync(cancellationToken);

        await store.AddAuditAsync(session.Username, "timetable.override.delete", row.ProfileId,
            "撤销了一条临时换课。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    /// <summary>
    /// 预览某个档案在指定日期（默认今天）合成后的课表：管理员据此确认换课是否按期生效。
    /// </summary>
    private static async Task<ApiResult<List<TimetablePreviewDto>>> PreviewAsync(
        string? profileId,
        string? date,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw HubException.Validation("请指定 profileId。");
        }

        var profile = await store.GetProfileAsync(profileId.Trim(), cancellationToken)
                      ?? throw HubException.NotFound("配置档案不存在。");

        var day = string.IsNullOrWhiteSpace(date) || !DateOnly.TryParse(date.Trim(), out var parsed)
            ? DateOnly.FromDateTime(DateTime.Now)
            : parsed;

        var content = HubJson.DeserializeOrDefault(profile.Content, new ContentBundleDto());
        var overrides = await store.GetActiveTimetableOverridesAsync(profile.Id,
            day.ToString("yyyy-MM-dd"), (int)day.DayOfWeek, cancellationToken);

        var result = new List<TimetablePreviewDto>();
        foreach (var plan in content.ClassPlans)
        {
            foreach (var slot in plan.Slots)
            {
                var hit = overrides.FirstOrDefault(o =>
                    (string.IsNullOrEmpty(o.ClassPlanId)
                     || string.Equals(o.ClassPlanId, plan.Id, StringComparison.OrdinalIgnoreCase))
                    && o.SlotIndex == slot.Index);

                var subjectId = hit is null ? slot.SubjectId : hit.SubjectId;
                result.Add(new TimetablePreviewDto
                {
                    ClassPlanId = plan.Id,
                    ClassPlanName = plan.Name,
                    SlotIndex = slot.Index,
                    SubjectId = subjectId,
                    SubjectName = string.IsNullOrEmpty(subjectId)
                        ? "（空堂）"
                        : content.Subjects.FirstOrDefault(s => s.Id == subjectId)?.Name ?? "（未知科目）",
                    IsOverridden = hit is not null,
                    OverrideReason = hit?.Reason ?? string.Empty,
                });
            }
        }

        return ApiResult<List<TimetablePreviewDto>>.Success(result);
    }

    /// <summary>星期集合归一化：去重、排序、只保留 0~6；为空表示每天。</summary>
    private static string NormalizeWeekdays(List<int>? weekdays)
    {
        if (weekdays is null || weekdays.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(",", weekdays.Where(d => d is >= 0 and <= 6).Distinct().OrderBy(d => d));
    }
}

/// <summary>临时换课记录。</summary>
public sealed class TimetableOverrideDto
{
    /// <summary>覆盖 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>作用的目标档案 ID。</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>目标课表 ID；为空表示该档案下所有课表。</summary>
    public string ClassPlanId { get; set; } = string.Empty;

    /// <summary>目标节次序号（从 0 开始）。</summary>
    public int SlotIndex { get; set; }

    /// <summary>临时科目 ID；为空表示改为空堂。</summary>
    public string? SubjectId { get; set; }

    /// <summary>开始日期（yyyy-MM-dd）。</summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>结束日期（含当天）；为空表示持续到撤销。</summary>
    public string? EndDate { get; set; }

    /// <summary>限定星期几（0=周日）；为空表示每天。</summary>
    public string Weekdays { get; set; } = string.Empty;

    /// <summary>换课原因。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>操作人。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>新建临时换课。</summary>
public sealed class TimetableOverrideUpsertRequest
{
    /// <summary>目标档案 ID。</summary>
    public string? ProfileId { get; set; }

    /// <summary>目标课表 ID；留空表示该档案下所有课表。</summary>
    public string? ClassPlanId { get; set; }

    /// <summary>节次序号（从 0 开始，与课表节点 index 一致）。</summary>
    public int SlotIndex { get; set; }

    /// <summary>临时科目 ID；留空表示改为空堂。</summary>
    public string? SubjectId { get; set; }

    /// <summary>开始日期（yyyy-MM-dd）。</summary>
    public string? StartDate { get; set; }

    /// <summary>结束日期（含当天）；留空表示持续到撤销。</summary>
    public string? EndDate { get; set; }

    /// <summary>限定星期几（0=周日 .. 6=周六）；留空表示每天。</summary>
    public List<int>? Weekdays { get; set; }

    /// <summary>换课原因。</summary>
    public string? Reason { get; set; }
}

/// <summary>换课预览中的一节课。</summary>
public sealed class TimetablePreviewDto
{
    /// <summary>课表 ID。</summary>
    public string ClassPlanId { get; set; } = string.Empty;

    /// <summary>课表名称。</summary>
    public string ClassPlanName { get; set; } = string.Empty;

    /// <summary>节次序号。</summary>
    public int SlotIndex { get; set; }

    /// <summary>当前生效的科目 ID。</summary>
    public string? SubjectId { get; set; }

    /// <summary>科目名称（空堂显示为「（空堂）」）。</summary>
    public string SubjectName { get; set; } = string.Empty;

    /// <summary>这一节是否被临时换课覆盖。</summary>
    public bool IsOverridden { get; set; }

    /// <summary>覆盖原因（未覆盖时为空）。</summary>
    public string OverrideReason { get; set; } = string.Empty;
}
