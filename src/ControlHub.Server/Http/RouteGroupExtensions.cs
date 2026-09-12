using ControlHub.Protocol;

namespace ControlHub.Server.Http;

/// <summary>
/// 路由分组辅助方法，统一维护 API 前缀与标签，避免各端点文件重复书写路径。
/// </summary>
public static class RouteGroupExtensions
{
    /// <summary>创建一个带版本前缀的路由分组，例如 <c>/api/v1/client</c>。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <param name="name">分组名。</param>
    public static RouteGroupBuilder NewVersionedGroup(this IEndpointRouteBuilder app, string name) =>
        app.MapGroup($"{HubProtocol.ApiPrefix}/{name}").WithTags(name);
}
