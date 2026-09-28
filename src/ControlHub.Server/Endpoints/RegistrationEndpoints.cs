using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Services;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 自助注册申请接口（用户提交、管理员审批），挂载在 <c>/api/v1/admin</c> 下。
/// <para>
/// 与邀请码注册的区别：申请只进入待审批队列，必须由管理员批准才会写入 users 表；
/// 申请里保存的是申请人自设密码的哈希，批准时原样复用，全程不接触明文。
/// </para>
/// </summary>
public static class RegistrationEndpoints
{
    /// <summary>自助注册申请总开关（默认关闭），与邀请码注册开关相互独立。</summary>
    public const string ApprovalSettingKey = "selfRegistrationApprovalEnabled";

    /// <summary>注册自助申请与审批路由。</summary>
    public static void MapRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        // 提交申请：登录页调用，无需登录。
        app.NewVersionedGroup("admin")
            .MapPost("/register-requests", SubmitAsync)
            .AllowAnonymous()
            .WithTags("public");

        // 审批相关：需要账号管理权限。
        var authed = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        authed.MapGet("/register-requests", ListAsync).RequirePermission(PermissionKeys.AccountsRead);
        authed.MapPost("/register-requests/{id}/approve", ApproveAsync)
            .RequirePermission(PermissionKeys.AccountsWrite);
        authed.MapPost("/register-requests/{id}/reject", RejectAsync)
            .RequirePermission(PermissionKeys.AccountsWrite);
        authed.MapDelete("/register-requests/{id}", DeleteAsync)
            .RequirePermission(PermissionKeys.AccountsWrite);
        authed.MapPut("/registration-approval", SetApprovalAsync)
            .RequirePermission(PermissionKeys.AccountsWrite);
    }

    /// <summary>是否允许提交自助注册申请。</summary>
    public static async Task<bool> IsApprovalEnabledAsync(HubStore store, CancellationToken cancellationToken)
        => await store.GetSettingAsync(ApprovalSettingKey, "0", cancellationToken) == "1";

    private static RegisterRequestDto ToDto(RegisterRequestRow row) => new()
    {
        Id = row.Id,
        Username = row.Username,
        DisplayName = row.DisplayName,
        Note = row.Note,
        Status = row.Status,
        Reason = row.Reason,
        ReviewedBy = row.ReviewedBy,
        ReviewedAt = row.ReviewedAt,
        CreatedAt = row.CreatedAt,
    };

    /// <summary>提交自助注册申请（无需登录，开关关闭时一律拒绝）。</summary>
    private static async Task<ApiResult<RegisterRequestDto>> SubmitAsync(
        RegisterRequestSubmit request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        if (!await IsApprovalEnabledAsync(store, cancellationToken))
        {
            throw HubException.Forbidden("当前未开放自助注册申请，请联系管理员开通账号。");
        }

        var username = AdminEndpoints.NormalizeUsername(request.Username);
        var password = request.Password ?? string.Empty;
        var strengthError = PasswordHasher.ValidateStrength(password);
        if (!string.IsNullOrEmpty(strengthError))
        {
            throw HubException.Validation(strengthError);
        }

        // 用户名已被占用、或已有一个待审批的同名申请，都不允许重复提交。
        if (await store.GetUserByUsernameAsync(username, cancellationToken) is not null)
        {
            throw HubException.Conflict("该用户名已被占用，请换一个。");
        }

        if (await store.GetPendingRegisterRequestAsync(username, cancellationToken) is not null)
        {
            throw HubException.Conflict("该用户名已提交过申请，请耐心等待管理员审批。");
        }

        var row = new RegisterRequestRow
        {
            Id = HubChecksum.NewId(),
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            Note = (request.Note ?? string.Empty).Trim(),
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateRegisterRequestAsync(row, cancellationToken);
        await store.AddAuditAsync(username, "account.register.request", username,
            $"提交自助注册申请（说明：{(string.IsNullOrWhiteSpace(row.Note) ? "无" : row.Note)}）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<RegisterRequestDto>.Success(ToDto(row));
    }

    /// <summary>申请列表；<c>status</c> 为空表示全部（待审批排在最前）。</summary>
    private static async Task<ApiResult<List<RegisterRequestDto>>> ListAsync(
        string? status,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var rows = await store.GetRegisterRequestsAsync(status, cancellationToken);
        return ApiResult<List<RegisterRequestDto>>.Success(rows.Select(ToDto).ToList());
    }

    /// <summary>批准申请：按勾选的权限建号，并把申请标记为已批准。</summary>
    private static async Task<ApiResult<RegisterRequestDto>> ApproveAsync(
        string id,
        RegisterRequestApprove request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await store.GetRegisterRequestAsync(id, cancellationToken)
                  ?? throw HubException.NotFound("申请不存在。");

        if (!string.Equals(row.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw HubException.Conflict("该申请已经处理过了。");
        }

        // 排队期间可能已有同名账号被建出来，批准前必须再确认一次。
        if (await store.GetUserByUsernameAsync(row.Username, cancellationToken) is not null)
        {
            throw HubException.Conflict($"用户名 {row.Username} 已被占用，请先处理占用或拒绝该申请。");
        }

        var permissions = PermissionKeys.Normalize(request.Permissions ?? PermissionKeys.DefaultForNewUser);
        if (permissions.Count == 0)
        {
            throw HubException.Validation("请至少勾选一项权限，否则该账号什么都做不了。");
        }

        AdminEndpoints.EnsureCanGrant(session, PermissionKeys.CustomRole, permissions);

        var user = new UserRow
        {
            Id = HubChecksum.NewId(),
            Username = row.Username,
            PasswordHash = row.PasswordHash,
            DisplayName = string.IsNullOrWhiteSpace(row.DisplayName) ? row.Username : row.DisplayName,
            Role = PermissionKeys.CustomRole,
            Permissions = permissions,
            MustChangePassword = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateUserAsync(user, cancellationToken);

        // 乐观锁：只有仍为 pending 才置为 approved。两个管理员同时点批准时，只有一个能生效。
        if (!await store.SetRegisterRequestStatusAsync(id, "approved", string.Empty, session.Username,
                cancellationToken))
        {
            await store.DeleteUserAsync(user.Id, cancellationToken);
            throw HubException.Conflict("该申请已被其他管理员处理。");
        }

        await store.AddAuditAsync(session.Username, "account.register.approve", row.Username,
            $"批准自助注册申请并建号（权限 {permissions.Count} 项）。",
            http.GetClientIpAddress(), cancellationToken);

        row.Status = "approved";
        row.ReviewedBy = session.Username;
        row.ReviewedAt = DateTimeOffset.UtcNow;
        return ApiResult<RegisterRequestDto>.Success(ToDto(row));
    }

    /// <summary>拒绝申请（不建号），可记录理由。</summary>
    private static async Task<ApiResult<RegisterRequestDto>> RejectAsync(
        string id,
        RegisterRequestReject request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await store.GetRegisterRequestAsync(id, cancellationToken)
                  ?? throw HubException.NotFound("申请不存在。");

        if (!string.Equals(row.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw HubException.Conflict("该申请已经处理过了。");
        }

        var reason = (request.Reason ?? string.Empty).Trim();
        if (!await store.SetRegisterRequestStatusAsync(id, "rejected", reason, session.Username, cancellationToken))
        {
            throw HubException.Conflict("该申请已被其他管理员处理。");
        }

        await store.AddAuditAsync(session.Username, "account.register.reject", row.Username,
            $"拒绝自助注册申请（理由：{(string.IsNullOrWhiteSpace(reason) ? "未填写" : reason)}）。",
            http.GetClientIpAddress(), cancellationToken);

        row.Status = "rejected";
        row.Reason = reason;
        row.ReviewedBy = session.Username;
        row.ReviewedAt = DateTimeOffset.UtcNow;
        return ApiResult<RegisterRequestDto>.Success(ToDto(row));
    }

    /// <summary>删除申请记录（仅清理已处理的记录，待审批的需先批准或拒绝）。</summary>
    private static async Task<ApiResult<bool>> DeleteAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var row = await store.GetRegisterRequestAsync(id, cancellationToken)
                  ?? throw HubException.NotFound("申请不存在。");

        if (string.Equals(row.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw HubException.Validation("该申请还在待审批状态，请先批准或拒绝。");
        }

        await store.DeleteRegisterRequestAsync(id, cancellationToken);
        await store.AddAuditAsync(session.Username, "account.register.delete", row.Username,
            "删除注册申请记录。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    /// <summary>开关自助注册申请。默认关闭：开放后任何人都能提交申请（仍需管理员批准）。</summary>
    private static async Task<ApiResult<bool>> SetApprovalAsync(
        bool? enabled,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var value = enabled ?? false;
        await store.SetSettingAsync(ApprovalSettingKey, value ? "1" : "0", cancellationToken);
        await store.AddAuditAsync(session.Username, "registration.approval.switch", "selfRegistrationApproval",
            value ? "开启了自助注册申请（需审批）。" : "关闭了自助注册申请。",
            http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(value);
    }
}
