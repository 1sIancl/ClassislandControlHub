using ControlHub.Protocol;
using ControlHub.Server.Services;
using Microsoft.AspNetCore.Http;

namespace ControlHub.Server.Http;

/// <summary>声明某条管理端路由所需的权限键。</summary>
/// <param name="permission">权限键，见 <see cref="PermissionKeys"/>。</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true)]
public sealed class RequiredPermissionAttribute(string permission) : Attribute
{
    /// <summary>所需权限键。</summary>
    public string Permission { get; } = permission;
}

/// <summary>路由权限标注的扩展方法。</summary>
public static class PermissionRouteExtensions
{
    /// <summary>声明该路由所需的权限键（不调用则视为「仅需登录」之外的未声明，会被拒绝）。</summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission) =>
        builder.WithMetadata(new RequiredPermissionAttribute(permission));
}

/// <summary>
/// 管理端权限过滤器，挂在 <c>AdminAuthFilter</c> 之后。
/// <para>
/// <b>fail-closed</b>：凡是挂了这个过滤器的路由，若没有用
/// <see cref="RequiredPermissionAttribute"/> 声明所需权限，一律返回 403。
/// 这样将来新增路由时若忘了标权限，是「拒绝」而不是「默认放行」。
/// </para>
/// </summary>
public sealed class AdminPermissionFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var session = http.RequireAdminSession();

        var required = http.GetEndpoint()?.Metadata.GetMetadata<RequiredPermissionAttribute>()?.Permission;
        if (string.IsNullOrEmpty(required))
        {
            throw HubException.Forbidden("该接口未声明所需权限，出于安全考虑已拒绝访问。");
        }

        if (!PermissionKeys.Satisfies(session.Role, session.Permissions, required))
        {
            throw HubException.Forbidden(required == PermissionKeys.Authenticated
                ? "当前账号无权访问该接口。"
                : $"当前账号没有此操作所需的权限：{PermissionCatalog.Describe(required)}。");
        }

        return await next(context);
    }
}
