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
    }

    private static async Task<ApiResult<List<TimetableTemplateDto>>> ListAsync(
        HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var rows = await store.GetTimetableTemplatesAsync(cancellationToken);
        return ApiResult<List<TimetableTemplateDto>>.Success(rows.Select(ToDto).ToList());
    }

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
