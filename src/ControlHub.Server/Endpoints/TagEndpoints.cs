using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 设备标签（#10）：标签维护 + 给设备贴标签。
///
/// 权限沿用**设备管理**（<c>devices.read</c> / <c>devices.write</c>）而不是另发一套权限键：
/// 标签只是设备的另一个维度，单独发键会造出「能改设备档案但不能贴标签」这种没人想要的授权组合。
/// </summary>
public static class TagEndpoints
{
    /// <summary>注册标签路由。</summary>
    public static void MapTagEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/tags", ListAsync).RequirePermission(PermissionKeys.DevicesRead);
        group.MapPost("/tags", CreateAsync).RequirePermission(PermissionKeys.DevicesWrite);
        group.MapPut("/tags/{id}", UpdateAsync).RequirePermission(PermissionKeys.DevicesWrite);
        group.MapDelete("/tags/{id}", DeleteAsync).RequirePermission(PermissionKeys.DevicesWrite);
        group.MapPut("/devices/{id}/tags", SetDeviceTagsAsync).RequirePermission(PermissionKeys.DevicesWrite);
    }

    private static async Task<ApiResult<List<TagDto>>> ListAsync(
        HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var tags = await store.GetTagsAsync(cancellationToken);
        return ApiResult<List<TagDto>>.Success(tags.Select(ToDto).ToList());
    }

    private static async Task<ApiResult<TagDto>> CreateAsync(
        TagRequest request, HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var tag = await store.CreateTagAsync(request.Name, request.Color, cancellationToken);

        await store.AddAuditAsync(session.Username, "tag.create", tag.Id,
            $"新建标签「{tag.Name}」。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<TagDto>.Success(ToDto(tag));
    }

    private static async Task<ApiResult<TagDto>> UpdateAsync(
        string id, TagRequest request, HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        if (!await store.UpdateTagAsync(id, request.Name, request.Color, cancellationToken))
        {
            throw HubException.NotFound("标签不存在，可能已被别人删除。");
        }

        var tag = await store.GetTagAsync(id, cancellationToken);
        await store.AddAuditAsync(session.Username, "tag.update", id,
            $"修改标签为「{tag?.Name}」。", http.GetClientIpAddress(), cancellationToken);

        return ApiResult<TagDto>.Success(ToDto(tag!));
    }

    private static async Task<ApiResult<object>> DeleteAsync(
        string id, HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var tag = await store.GetTagAsync(id, cancellationToken);

        // 先取出来再删：删完就查不到名字了，审计里只剩一个 ID 没法读。
        if (!await store.DeleteTagAsync(id, cancellationToken))
        {
            throw HubException.NotFound("标签不存在，可能已被别人删除。");
        }

        await store.AddAuditAsync(session.Username, "tag.delete", id,
            $"删除标签「{tag?.Name}」（已从 {tag?.DeviceCount ?? 0} 台设备上移除）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<object>.Success(new { ok = true });
    }

    /// <summary>设置某台设备的标签集合（整体替换）。</summary>
    private static async Task<ApiResult<object>> SetDeviceTagsAsync(
        string id, DeviceTagsRequest request, HttpContext http, HubStore store, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var tagIds = request.TagIds ?? [];

        if (!await store.SetDeviceTagsAsync(id, tagIds, cancellationToken))
        {
            throw HubException.NotFound("设备不存在，可能已被删除。");
        }

        var device = await store.GetDeviceAsync(id, cancellationToken);
        await store.AddAuditAsync(session.Username, "tag.assign", id,
            $"设置「{device?.Name ?? id}」的标签（共 {tagIds.Count} 个）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<object>.Success(new { ok = true });
    }

    private static TagDto ToDto(TagRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Color = row.Color,
        DeviceCount = row.DeviceCount,
        CreatedAt = row.CreatedAt,
    };
}
