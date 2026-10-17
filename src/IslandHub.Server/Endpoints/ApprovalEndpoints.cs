using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Http;
using IslandHub.Server.Services;

namespace IslandHub.Server.Endpoints;

/// <summary>
/// 操作审批（路线图 #35）：列出 / 通过 / 驳回。
///
/// <para>
/// 权限口径：<b>看</b>按「系统设置 · 查看」（非超管只能看到自己发起的，见 <see cref="ApprovalService.ListAsync"/>）；
/// <b>批</b>只要求「登录」，是不是超管在处理器里判断——这样普通账号得到的是明确的
/// 「只有超级管理员可以审批」，而不是含糊的「当前账号没有此操作所需的权限」。
/// </para>
/// </summary>
public static class ApprovalEndpoints
{
    /// <summary>注册路由。</summary>
    public static void MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        // 列表只要求「登录」：**提交人一定要能看到自己提交的申请**（他自己往往没有 settings.read），
        // 而非超管在服务端被过滤成「只看自己发起的」，看不到别人的任何记录。
        group.MapGet("/approvals", ListAsync).RequirePermission(PermissionKeys.Authenticated);
        group.MapPost("/approvals/{id}/approve", ApproveAsync).RequirePermission(PermissionKeys.Authenticated);
        group.MapPost("/approvals/{id}/reject", RejectAsync).RequirePermission(PermissionKeys.Authenticated);
    }

    private static async Task<ApiResult<ApprovalListDto>> ListAsync(
        string? status,
        HttpContext http,
        ApprovalService approvals,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var items = await approvals.ListAsync(session,
            string.IsNullOrWhiteSpace(status) ? null : status.Trim(), cancellationToken);

        return ApiResult<ApprovalListDto>.Success(new ApprovalListDto
        {
            CanDecide = ApprovalService.IsSuperAdmin(session),
            Items = items,
        });
    }

    private static async Task<ApiResult<ApprovalRequestDto>> ApproveAsync(
        string id,
        ApprovalDecisionRequest? request,
        HttpContext http,
        ApprovalService approvals,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        if (!ApprovalService.IsSuperAdmin(session))
        {
            throw HubException.Forbidden("只有超级管理员可以审批。");
        }

        var dto = await approvals.ApproveAsync(id, session.Username, request?.Comment,
            http.GetClientIpAddress(), cancellationToken);
        return ApiResult<ApprovalRequestDto>.Success(dto);
    }

    private static async Task<ApiResult<ApprovalRequestDto>> RejectAsync(
        string id,
        ApprovalDecisionRequest? request,
        HttpContext http,
        ApprovalService approvals,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        if (!ApprovalService.IsSuperAdmin(session))
        {
            throw HubException.Forbidden("只有超级管理员可以审批。");
        }

        var dto = await approvals.RejectAsync(id, session.Username, request?.Comment,
            http.GetClientIpAddress(), cancellationToken);
        return ApiResult<ApprovalRequestDto>.Success(dto);
    }
}
