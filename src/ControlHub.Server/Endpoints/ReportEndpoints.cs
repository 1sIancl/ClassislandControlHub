using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Options;
using Microsoft.Extensions.Options;

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
        group.MapGet("/reports/sync", SyncReportAsync).RequirePermission(PermissionKeys.AuditRead);
        // 通知到达率（#7 回执数据的聚合，#66 的底座）
        group.MapGet("/reports/notifications", NotificationArrivalAsync).RequirePermission(PermissionKeys.AuditRead);
        // 单条通知的回执明细：管理员点开「这条通知」看谁到了、谁没到
        group.MapGet("/notifications/{commandId}/receipt", NotificationReceiptAsync)
            .RequirePermission(PermissionKeys.RemoteRead);
    }

    private static async Task<ApiResult<NotificationArrivalReportDto>> NotificationArrivalAsync(
        int? days, HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        return ApiResult<NotificationArrivalReportDto>.Success(
            await store.GetNotificationArrivalReportAsync(ClampDays(days), 50, cancellationToken));
    }

    private static async Task<ApiResult<NotificationReceiptSummaryDto>> NotificationReceiptAsync(
        string commandId, HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var summary = await store.GetNotificationReceiptAsync(commandId, cancellationToken);
        if (summary is null)
        {
            throw HubException.NotFound("通知不存在。");
        }

        return ApiResult<NotificationReceiptSummaryDto>.Success(summary);
    }

    private static async Task<ApiResult<SyncReportDto>> SyncReportAsync(
        int? days, HttpContext http, HubStore store, IOptions<ServerOptions> options,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();

        // 在线判定沿用同一口径（与仪表盘、在线率报表一致），否则「离线」在三处的含义会不一样。
        var timeout = TimeSpan.FromSeconds(Math.Max(10, options.Value.OnlineTimeoutSeconds));
        return ApiResult<SyncReportDto>.Success(
            await store.GetSyncReportAsync(ClampDays(days), timeout, 40, cancellationToken));
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
