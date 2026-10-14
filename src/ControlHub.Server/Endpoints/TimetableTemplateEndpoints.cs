using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 课表模板（#9）：把一套排好的课表存下来，供其它档案套用。
///
/// 权限沿用**档案**（<c>profiles.read</c> / <c>profiles.write</c>）：模板是档案内容的一部分，
/// 单独发权限键会造出「能编辑档案但不能存模板」这种没人要的组合。
/// </summary>
public static class TimetableTemplateEndpoints
{
    /// <summary>注册课表模板路由。</summary>
    public static void MapTimetableTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/timetable-templates", ListAsync).RequirePermission(PermissionKeys.ProfilesRead);
        group.MapPost("/timetable-templates", CreateAsync).RequirePermission(PermissionKeys.ProfilesWrite);
        group.MapDelete("/timetable-templates/{id}", DeleteAsync).RequirePermission(PermissionKeys.ProfilesWrite);
        // 套用（#9 的一半）：把模板的「天 × 节次」网格写进某个档案的课表里。
        group.MapPost("/timetable-templates/{id}/apply", ApplyAsync).RequirePermission(PermissionKeys.ProfilesWrite);
    }

    private static async Task<ApiResult<List<TimetableTemplateDto>>> ListAsync(
        HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var rows = await store.GetTimetableTemplatesAsync(cancellationToken);
        return ApiResult<List<TimetableTemplateDto>>.Success(rows.Select(ToDto).ToList());
    }

    /// <summary>
    /// 把模板套用到某个档案：按科目**名称**匹配（科目 ID 跨档案必然对不上），
    /// 按天匹配课表（一张课表覆盖多天而模板里这几天不一致时**跳过并说明**，不替管理员猜）。
    /// <para>套用会写内容 ⇒ 档案版本号 +1；**不自动下发**——下发仍然由管理员在「配置下发」里决定
    /// （与在档案编辑器里改课表的行为一致，避免编辑过程中就把全校配置推出去）。</para>
    /// </summary>
    private static async Task<ApiResult<TimetableTemplateApplyResultDto>> ApplyAsync(
        string id, TimetableTemplateApplyRequest request, HttpContext http, HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        if (string.IsNullOrWhiteSpace(request.ProfileId))
        {
            throw HubException.Validation("请选择要套用的档案。");
        }

        var template = (await store.GetTimetableTemplatesAsync(cancellationToken))
            .FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal))
            ?? throw HubException.NotFound("模板不存在，可能已被别人删除。");

        var profile = await store.GetProfileAsync(request.ProfileId, cancellationToken)
            ?? throw HubException.Validation("目标档案不存在，可能已被删除。");

        var content = HubJson.Deserialize<ContentBundleDto>(profile.Content ?? string.Empty)
                      ?? new ContentBundleDto();
        var outcome = TimetableTemplateApplier.Apply(content, template.Grid, template.PeriodCount);

        if (!outcome.Changed)
        {
            throw HubException.Validation("没有可套用的课表：" + DescribeSkip(outcome));
        }

        var updated = await store.UpdateProfileAsync(profile.Id, profile.Name, profile.Description,
            HubJson.Serialize(content), contentChanged: true, cancellationToken);

        await store.AddAuditAsync(session.Username, "template.apply", profile.Name,
            $"套用课表模板「{template.Name}」到档案「{profile.Name}」：改写 {outcome.UpdatedPlans.Count} 张课表"
            + (outcome.MissingSubjects.Count > 0 ? $"，{outcome.MissingSubjects.Count} 个科目名没匹配上" : string.Empty)
            + (outcome.ConflictedPlans.Count > 0 ? $"，{outcome.ConflictedPlans.Count} 张课表因多天不一致跳过" : string.Empty)
            + "。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<TimetableTemplateApplyResultDto>.Success(new TimetableTemplateApplyResultDto
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Revision = updated?.Revision ?? 0,
            UpdatedPlans = outcome.UpdatedPlans,
            UpdatedDays = outcome.UpdatedDays,
            ConflictedPlans = outcome.ConflictedPlans,
            MissingSubjects = outcome.MissingSubjects,
            UncoveredDays = outcome.UncoveredDays,
            PeriodMismatches = outcome.PeriodMismatches,
        });
    }

    /// <summary>把「为什么没套上任何东西」讲清楚（管理员照这句话就知道下一步做什么）。</summary>
    private static string DescribeSkip(TimetableTemplateApplier.ApplyOutcome outcome)
    {
        var reasons = new List<string>();
        if (outcome.ConflictedPlans.Count > 0)
        {
            reasons.Add($"模板里同一张课表覆盖的几天内容不一致（{string.Join('、', outcome.ConflictedPlans)}）"
                + "——请把课表按天拆开后再套用");
        }

        if (outcome.UncoveredDays.Count > 0)
        {
            reasons.Add($"模板里有课的天没有任何课表覆盖（{string.Join('、', outcome.UncoveredDays.Select(DayName))}）");
        }

        return reasons.Count > 0 ? string.Join("；", reasons) + "。" : "模板里没有可套用的课（或目标档案没有课表）。";
    }

    /// <summary>星期几的中文名。</summary>
    private static string DayName(int day) =>
        day is >= 0 and <= 6 ? $"周{new[] { "日", "一", "二", "三", "四", "五", "六" }[day]}" : day.ToString();

    private static async Task<ApiResult<TimetableTemplateDto>> CreateAsync(
        TimetableTemplateRequest request, HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await store.CreateTimetableTemplateAsync(
            request.Name, request.Grid, request.PeriodCount, session.Username, cancellationToken);

        await store.AddAuditAsync(session.Username, "template.create", row.Id,
            $"新建课表模板「{row.Name}」（{row.PeriodCount} 节 × {row.Grid.Count} 天）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<TimetableTemplateDto>.Success(ToDto(row));
    }

    private static async Task<ApiResult<object>> DeleteAsync(
        string id, HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        if (!await store.DeleteTimetableTemplateAsync(id, cancellationToken))
        {
            throw HubException.NotFound("模板不存在，可能已被别人删除。");
        }

        await store.AddAuditAsync(session.Username, "template.delete", id,
            "删除课表模板。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<object>.Success(new { ok = true });
    }

    private static TimetableTemplateDto ToDto(TimetableTemplateRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Grid = row.Grid,
        PeriodCount = row.PeriodCount,
        CreatedBy = row.CreatedBy,
        CreatedAt = row.CreatedAt,
    };
}
