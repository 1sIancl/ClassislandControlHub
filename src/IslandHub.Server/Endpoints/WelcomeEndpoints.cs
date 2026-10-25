using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;
using IslandHub.Server.Http;

namespace IslandHub.Server.Endpoints;

/// <summary>
/// 首次使用引导页（#126）：记录「这个实例是否已经看过开始页」。
///
/// <para>
/// 为什么存服务端而不是 localStorage：这是**实例级**的，不是浏览器级的。
/// 存本地的话，换台电脑、换个浏览器、清一次缓存就又会看到一遍——
/// 而"要不要引导"回答的是"这个学校部署好之后，有没有人走过一遍"，与浏览器无关。
/// </para>
/// </summary>
public static class WelcomeEndpoints
{
    /// <summary>设置键：值为 <c>"true"</c> 表示已看完或已跳过。</summary>
    public const string CompletedKey = "welcome_completed";

    /// <summary>注册引导相关路由。</summary>
    public static void MapWelcomeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        // ⚠️ 每条路由都必须**显式声明**权限：AdminPermissionFilter 对"没声明权限"的接口
        //    一律 403（"该接口未声明所需权限，出于安全考虑已拒绝访问"）——不是靠默认放行。
        //    这两条是"登录即可"，所以声明 Authenticated。
        group.MapGet("/welcome/status", GetStatusAsync).RequirePermission(PermissionKeys.Authenticated);
        group.MapPost("/welcome/complete", CompleteAsync).RequirePermission(PermissionKeys.Authenticated);
        // 重置 = 让所有人下次登录又看到引导，属于改实例状态，要 settings.write。
        group.MapPost("/welcome/reset", ResetAsync).RequirePermission(PermissionKeys.SettingsWrite);
    }

    private static async Task<ApiResult<WelcomeStatusDto>> GetStatusAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var value = await store.GetSettingAsync(CompletedKey, "false", cancellationToken);
        return ApiResult<WelcomeStatusDto>.Success(new WelcomeStatusDto
        {
            Completed = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
        });
    }

    private static async Task<ApiResult<WelcomeStatusDto>> CompleteAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        await store.SetSettingAsync(CompletedKey, "true", cancellationToken);
        await store.AddAuditAsync(session.Username, "welcome.complete", "welcome",
            "标记首次使用引导为已完成。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<WelcomeStatusDto>.Success(new WelcomeStatusDto { Completed = true });
    }

    private static async Task<ApiResult<WelcomeStatusDto>> ResetAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        await store.SetSettingAsync(CompletedKey, "false", cancellationToken);
        // 这条审计有意义：重置之后**所有人**下次登录都会看到引导页。
        await store.AddAuditAsync(session.Username, "welcome.reset", "welcome",
            "重置首次使用引导（下次登录会重新显示开始页）。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<WelcomeStatusDto>.Success(new WelcomeStatusDto { Completed = false });
    }
}
