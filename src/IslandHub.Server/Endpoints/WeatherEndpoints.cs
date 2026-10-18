using System.Text.Json;
using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;
using IslandHub.Server.Http;
using IslandHub.Server.Services;

namespace IslandHub.Server.Endpoints;

/// <summary>
/// 天气管理端点（新功能）：读取配置与状态 / 保存配置 / 立即抓取。
///
/// <para>
/// 教室端那条读取接口在 <see cref="ClientEndpoints"/> 里（`GET /client/weather`，走设备令牌），
/// 因为那里有现成的设备鉴权分组。
/// </para>
/// </summary>
public static class WeatherEndpoints
{
    /// <summary>注册天气管理端点。</summary>
    public static void MapWeatherEndpoints(this IEndpointRouteBuilder app)
    {
        // 与本仓其它管理端分组一致：先用 NewVersionedGroup("admin") 建组，
        // 再挂「登录校验 + 权限过滤」两个过滤器，最后逐条路由声明所需权限。
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/weather", GetAsync).RequirePermission(PermissionKeys.SettingsRead);
        group.MapPut("/weather", SaveAsync).RequirePermission(PermissionKeys.SettingsWrite);
        group.MapPost("/weather/refresh", RefreshAsync).RequirePermission(PermissionKeys.SettingsWrite);
    }

    /// <summary>读取天气配置与当前数据。</summary>
    private static ApiResult<WeatherStateDto> GetAsync(HttpContext http, WeatherService weather)
    {
        http.RequireAdminSession();
        return ApiResult<WeatherStateDto>.Success(weather.State);
    }

    /// <summary>保存配置并立刻抓一次（保存完界面就能看到天气，不必等下一轮）。</summary>
    private static async Task<ApiResult<WeatherStateDto>> SaveAsync(
        WeatherConfigDto request,
        HttpContext http,
        WeatherService weather,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var state = await weather.SaveConfigAsync(request, cancellationToken);

        await store.AddAuditAsync(session.Username, "weather.updated", "weather",
            $"天气：{(state.Config.Enabled ? "启用" : "停用")}"
            + (state.Config.City.Length > 0
                ? $"，城市「{state.Config.City}」（{state.Config.Latitude:0.##}, {state.Config.Longitude:0.##}），"
                  + $"每 {state.Config.RefreshMinutes} 分钟刷新"
                : string.Empty)
            + "。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<WeatherStateDto>.Success(state);
    }

    /// <summary>立即抓取一次（界面上的「立即刷新」）。失败不报错，原因写在 state.error 里。</summary>
    private static async Task<ApiResult<WeatherStateDto>> RefreshAsync(
        HttpContext http,
        WeatherService weather,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var state = await weather.RefreshAsync(cancellationToken);
        await store.AddAuditAsync(session.Username, "weather.refresh", "weather",
            state.Error is null ? "手动刷新天气。" : $"手动刷新天气失败：{state.Error}",
            http.GetClientIpAddress(), cancellationToken);
        return ApiResult<WeatherStateDto>.Success(state);
    }
}
