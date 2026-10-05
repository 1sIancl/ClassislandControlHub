using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 报表接口（#63 在线率 / #65 指令执行 / #67 操作热点）。
///
/// 设计取舍：
///   - **三个接口分开**，而不是一个把三类数据打包的 <c>/reports</c>：报表页是分标签看的，
///     分开请求才能「点哪个标签先看哪块」，而不是被最慢的那块拖住。
///   - **只返回聚合结果**，不回传原始明细：这些数字的用途是「看出异常」；
///     真要逐条追查该去审计日志与指令历史，那里有完整上下文与筛选。
///   - 权限沿用 <c>audit.read</c>：报表读的就是历史数据，能看审计的人才该看报表。
/// </summary>
public static class ReportEndpoints
{
    /// <summary><c>days</c> 的默认值与上限。</summary>
    private const int DefaultDays = 7;
    private const int MaxDays = 90;

    /// <summary>注册报表路由。</summary>
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/reports/commands", CommandReportAsync).RequirePermission(PermissionKeys.AuditRead);
        group.MapGet("/reports/operations", OperationReportAsync).RequirePermission(PermissionKeys.AuditRead);
        group.MapGet("/reports/online", OnlineReportAsync).RequirePermission(PermissionKeys.AuditRead);
    }

    private static async Task<ApiResult<CommandReportDto>> CommandReportAsync(
        int? days, HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        return ApiResult<CommandReportDto>.Success(
            await store.GetCommandReportAsync(ClampDays(days), cancellationToken));
    }

    private static async Task<ApiResult<OperationReportDto>> OperationReportAsync(
        int? days, HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        return ApiResult<OperationReportDto>.Success(
            await store.GetOperationReportAsync(ClampDays(days), cancellationToken));
    }

    private static async Task<ApiResult<OnlineReportDto>> OnlineReportAsync(
        int? days, HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        return ApiResult<OnlineReportDto>.Success(
            await store.GetOnlineReportAsync(ClampDays(days), cancellationToken));
    }

    /// <summary>夹取天数：0 与负数收到 1、超过上限收到 90，避免有人手改 URL 把库拖垮。</summary>
    private static int ClampDays(int? days) => Math.Clamp(days ?? DefaultDays, 1, MaxDays);
}
