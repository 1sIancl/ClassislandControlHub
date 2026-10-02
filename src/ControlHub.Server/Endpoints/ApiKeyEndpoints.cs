using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 开放 API 密钥的签发与撤销（#74），挂载在 <c>/api/v1/admin</c> 下。
/// <para>密钥用于脚本与第三方接入：不必再拿管理员账号密码，且权限可以只给只读、还能设期限与随时撤销。</para>
/// </summary>
public static class ApiKeyEndpoints
{
    /// <summary>注册路由。</summary>
    public static void MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        // 密钥本身就是凭证，因此按「账号」权限管理（能管账号的人才能发凭据）。
        group.MapGet("/api-keys", ListAsync).RequirePermission(PermissionKeys.AccountsRead);
        group.MapPost("/api-keys", CreateAsync).RequirePermission(PermissionKeys.AccountsWrite);
        group.MapDelete("/api-keys/{id}", RevokeAsync).RequirePermission(PermissionKeys.AccountsWrite);
        group.MapDelete("/api-keys/{id}/permanent", DeleteAsync).RequirePermission(PermissionKeys.AccountsWrite);
    }

    private static async Task<ApiResult<List<ApiKeyDto>>> ListAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var rows = await store.GetApiKeysAsync(cancellationToken);
        return ApiResult<List<ApiKeyDto>>.Success(rows.Select(ToDto).ToList());
    }

    private static async Task<ApiResult<ApiKeyCreateResultDto>> CreateAsync(
        ApiKeyCreateRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw HubException.Validation("请给密钥起个名字（写清谁在用），否则将来无法判断该不该撤销。");
        }

        if (name.Length > 60)
        {
            throw HubException.Validation("密钥名字过长（最多 60 个字符）。");
        }

        // 只接受已知权限键，且不允许把「密钥管理」权限再授出去（否则一把密钥可以无限自我复制）。
        var permissions = PermissionKeys.Normalize(request.Permissions ?? [])
            .Where(p => !string.Equals(p, PermissionKeys.AccountsWrite, StringComparison.Ordinal))
            .ToList();

        if (permissions.Count == 0)
        {
            throw HubException.Validation(
                "请至少授予一项权限（建议只勾只读；「密钥管理」权限不能授予密钥，避免自我复制）。");
        }

        var expiresDays = Math.Clamp(request.ExpiresInDays, 0, 3650);
        var secret = HubStore.NewApiKey();
        var row = new ApiKeyRow
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            Prefix = secret[..Math.Min(12, secret.Length)],
            KeyHash = HubStore.HashApiKey(secret),
            Permissions = permissions,
            CreatedBy = session.Username,
            ExpiresAt = expiresDays > 0 ? DateTimeOffset.UtcNow.AddDays(expiresDays) : null,
            Note = request.Note?.Trim() ?? string.Empty,
        };

        await store.CreateApiKeyAsync(row, cancellationToken);
        await store.AddAuditAsync(session.Username, "apikey.create", row.Name,
            $"签发 API 密钥（权限 {string.Join("/", permissions)}；" +
            $"{(expiresDays > 0 ? $"{expiresDays} 天后过期" : "长期有效")}）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<ApiKeyCreateResultDto>.Success(new ApiKeyCreateResultDto
        {
            Key = ToDto(row),
            Secret = secret,
        });
    }

    private static async Task<ApiResult<bool>> RevokeAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        if (!await store.RevokeApiKeyAsync(id, cancellationToken))
        {
            throw HubException.NotFound("密钥不存在或已被撤销。");
        }

        await store.AddAuditAsync(session.Username, "apikey.revoke", id,
            "撤销 API 密钥。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    private static async Task<ApiResult<bool>> DeleteAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        if (!await store.DeleteApiKeyAsync(id, cancellationToken))
        {
            throw HubException.NotFound("密钥不存在。");
        }

        // 删除会丢掉「这把密钥曾经存在过」的痕迹，因此审计里必须留一条。
        await store.AddAuditAsync(session.Username, "apikey.delete", id,
            "彻底删除 API 密钥记录。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    private static ApiKeyDto ToDto(ApiKeyRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Prefix = row.Prefix,
        Permissions = row.Permissions,
        CreatedBy = row.CreatedBy,
        CreatedAt = row.CreatedAt,
        ExpiresAt = row.ExpiresAt,
        LastUsedAt = row.LastUsedAt,
        Revoked = row.Revoked,
        Note = row.Note,
    };
}
