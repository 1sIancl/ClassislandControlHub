using System.Security.Cryptography;

using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;
using IslandHub.Server.Http;
using IslandHub.Server.Services;

namespace IslandHub.Server.Endpoints;

/// <summary>A 端备份接口，挂载在 <c>/api/v1/admin/backups</c> 下。</summary>
public static class BackupEndpoints
{
    /// <summary>注册备份路由。</summary>
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/backups", ListAsync).RequirePermission(PermissionKeys.BackupRead);
        group.MapPost("/backups", CreateAsync).RequirePermission(PermissionKeys.BackupWrite);
        group.MapDelete("/backups/{id}", DeleteAsync).RequirePermission(PermissionKeys.BackupWrite);
        group.MapGet("/backups/{id}/download", DownloadAsync).RequirePermission(PermissionKeys.BackupRead);
        // 加密导出（#60）：口令走请求体（不进 URL / 访问日志），返回 .zip.enc。
        group.MapPost("/backups/{id}/export", ExportAsync).RequirePermission(PermissionKeys.BackupRead);
        group.MapPost("/backups/{id}/restore", RestoreAsync).RequirePermission(PermissionKeys.BackupWrite);
        // 导入备份（#125）：接受 .zip 或 .zip.enc（加密包要给口令）。导入只是**落成一份备份**，
        // 真正的恢复仍走 /backups/{id}/restore（那条带操作审批），所以这里不需要再审批一次。
        group.MapPost("/backups/import", ImportAsync).RequirePermission(PermissionKeys.BackupWrite);

        // 数据库健康检查与整理（#62）：SQLite 删数据不归还磁盘空间，需要一个能查、能整理的入口。
        group.MapGet("/database/health", DatabaseHealthAsync).RequirePermission(PermissionKeys.BackupRead);
        group.MapPost("/database/vacuum", DatabaseVacuumAsync).RequirePermission(PermissionKeys.BackupWrite);
    }

    /// <summary>导出加密备份（#60）。口令只走请求体：放进 URL 会进浏览器历史、反代日志与访问日志。</summary>
    private static async Task<IResult> ExportAsync(
        string id,
        BackupExportRequest request,
        HttpContext http,
        BackupService backups,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        var passphrase = request.Passphrase ?? string.Empty;
        if (passphrase.Length < 6)
        {
            throw HubException.Validation("口令至少 6 位。备份是整库数据，口令太短等于没加密。");
        }

        var zip = backups.ExportBackupZip(id)
                  ?? throw HubException.NotFound("备份不存在。");

        var encrypted = BackupCrypto.Encrypt(zip, passphrase);

        // 审计里**绝不记口令**，只记「导出过一份加密备份」这件事。
        await store.AddAuditAsync(session.Username, "backup.export", id,
            $"导出加密备份（{encrypted.Length} 字节；已设置口令，口令不记录）。",
            http.GetClientIpAddress(), cancellationToken);

        return Results.File(encrypted, "application/octet-stream", BackupCrypto.EncryptedFileName(id));
    }

    /// <summary>数据库健康检查（#62）：完整性 + 体积 + 可回收空间。</summary>
    private static async Task<ApiResult<DatabaseHealthDto>> DatabaseHealthAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        return ApiResult<DatabaseHealthDto>.Success(await store.GetDatabaseHealthAsync(cancellationToken));
    }

    /// <summary>整理数据库（#62）：执行 VACUUM 并回收空间，返回实际回收量。</summary>
    private static async Task<ApiResult<DatabaseVacuumResultDto>> DatabaseVacuumAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var result = await store.VacuumAsync(cancellationToken);

        // 整理会重写整库，属于对数据有实际影响的操作，留审计。
        await store.AddAuditAsync(session.Username, "database.vacuum", "数据库",
            $"整理数据库：{result.BeforeBytes / 1024} KB → {result.AfterBytes / 1024} KB"
            + $"（回收 {result.ReclaimedBytes / 1024} KB，耗时 {result.DurationMs} ms）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<DatabaseVacuumResultDto>.Success(result);
    }

    private static ApiResult<List<BackupEntryDto>> ListAsync(HttpContext http, BackupService backups)
    {
        http.RequireAdminSession();
        var list = backups.ListBackups().Select(ToDto).ToList();
        return ApiResult<List<BackupEntryDto>>.Success(list);
    }

    private static async Task<ApiResult<BackupEntryDto>> CreateAsync(
        CreateBackupRequest request,
        HttpContext http,
        BackupService backups,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var entry = backups.CreateBackup("manual", request.Note ?? string.Empty, request.Options);
        // 审计里必须能看出「这份备份有没有带密钥」——事后追查「备份到底泄露了什么」全靠这一行。
        await store.AddAuditAsync(session.Username, "backup.create", entry.Id,
            $"创建备份（{entry.FileCount} 个文件，{entry.SizeBytes} 字节"
            + $"{(entry.IncludesSecretsKey ? "，含加密密钥" : "，不含加密密钥")}）。",
            http.GetClientIpAddress(), cancellationToken);
        return ApiResult<BackupEntryDto>.Success(ToDto(entry));
    }

    /// <summary>
    /// 导入备份文件（#125）：<c>multipart/form-data</c>，字段 <c>file</c>（.zip / .zip.enc）、
    /// <c>passphrase</c>（加密包必填）、<c>note</c>（可选）。
    /// </summary>
    private static async Task<ApiResult<BackupEntryDto>> ImportAsync(
        HttpContext http,
        BackupService backups,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        if (!http.Request.HasFormContentType)
        {
            throw HubException.Validation("请用 multipart/form-data 上传备份文件。");
        }

        var form = await http.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file")
                   ?? throw HubException.Validation("没有收到文件（字段名应为 file）。");

        // 上限 256 MB：整库 + 配置文件正常远小于这个数；不设上限就会被一个超大上传拖死。
        const long maxBytes = 256L * 1024 * 1024;
        if (file.Length == 0)
        {
            throw HubException.Validation("文件是空的。");
        }

        if (file.Length > maxBytes)
        {
            throw HubException.Validation("文件超过 256 MB，不像是备份包。");
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        var payload = buffer.ToArray();

        // 加密包先解密：口令错、文件被改过，都会在 AES-GCM 的认证标签这一步被拦下
        // （不是「解出来一堆乱码」，而是直接抛异常——所以可以明确告诉用户是哪一类问题）。
        if (BackupCrypto.IsEncrypted(payload))
        {
            var passphrase = form["passphrase"].ToString();
            if (passphrase.Length < 6)
            {
                throw HubException.Validation("这是加密备份，需要填写口令（至少 6 位）。");
            }

            try
            {
                payload = BackupCrypto.Decrypt(payload, passphrase);
            }
            catch (CryptographicException)
            {
                throw HubException.Validation("口令不对，或文件已损坏。");
            }
        }

        var entry = backups.ImportBackupZip(payload, form["note"].ToString());

        // 审计只记「导入了一份多大、含不含密钥的备份」——**绝不记口令**。
        await store.AddAuditAsync(session.Username, "backup.import", entry.Id,
            $"导入备份文件（{entry.FileCount} 个文件，{entry.SizeBytes} 字节"
            + $"{(entry.IncludesSecretsKey ? "，含加密密钥" : "，不含加密密钥")}；口令不记录）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<BackupEntryDto>.Success(ToDto(entry));
    }

    private static BackupEntryDto ToDto(BackupEntry entry) => new()
    {
        Id = entry.Id,
        Type = entry.Type,
        CreatedAt = entry.CreatedAt,
        SizeBytes = entry.SizeBytes,
        Note = entry.Note,
        FileCount = entry.FileCount,
        IncludesSecretsKey = entry.IncludesSecretsKey,
    };

    private static async Task<ApiResult<bool>> DeleteAsync(
        string id,
        HttpContext http,
        BackupService backups,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var ok = backups.DeleteBackup(id);
        if (ok)
        {
            await store.AddAuditAsync(session.Username, "backup.delete", id,
                "删除备份。", http.GetClientIpAddress(), cancellationToken);
        }

        return ApiResult<bool>.Success(ok);
    }

    private static IResult DownloadAsync(string id, HttpContext http, BackupService backups)
    {
        http.RequireAdminSession();
        var bytes = backups.ExportBackupZip(id);
        if (bytes is null)
        {
            throw HubException.NotFound("备份不存在。");
        }

        return Results.File(bytes, "application/zip", $"{id}.zip");
    }

    private static async Task<ApiResult<DangerousActionResultDto>> RestoreAsync(
        string id,
        HttpContext http,
        BackupService backups,
        HubStore store,
        ApprovalService approvals,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        // 操作审批（#35）：恢复备份会**整库覆盖**，拿错备份等于把一学期的配置换掉。
        // 所以非超管点下去只是**提交一条审批**，由超级管理员确认后才真正执行。
        if (ApprovalService.NeedsApproval(session))
        {
            var request = await approvals.SubmitAsync(
                ApprovalKinds.BackupRestore,
                new BackupRestoreApprovalPayload { BackupId = id },
                session.Username,
                cancellationToken);

            await store.AddAuditAsync(session.Username, "backup.restore.request", id,
                $"提交恢复备份审批（申请 {request.Id}），等待超级管理员确认。",
                http.GetClientIpAddress(), cancellationToken);

            return ApiResult<DangerousActionResultDto>.Success(new DangerousActionResultDto
            {
                Executed = false,
                ApprovalId = request.Id,
                Status = ApprovalStatuses.Pending,
                Message = "已提交审批：恢复备份会整库覆盖，需要超级管理员在「操作审批」里确认后才执行。",
            });
        }

        var ok = backups.RestoreBackup(id);
        await store.AddAuditAsync(session.Username, "backup.restore", id,
            $"从备份恢复数据库（{(ok ? "成功" : "失败")}），需重启服务生效。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<DangerousActionResultDto>.Success(new DangerousActionResultDto
        {
            Executed = true,
            Status = ok ? "done" : "failed",
            Message = ok ? "已恢复备份，重启服务后生效。" : "恢复失败（备份文件可能已损坏或被删除）。",
        });
    }
}

/// <summary>创建备份的请求体。</summary>
public sealed class CreateBackupRequest
{
    /// <summary>备注。</summary>
    public string? Note { get; set; }

    /// <summary>可选内容（#59）：默认只含数据库与配置文件，**不含加密密钥**。</summary>
    public BackupOptionsDto? Options { get; set; }
}

/// <summary>加密导出备份的请求体（#60）。口令只走请求体，避免出现在 URL 与访问日志里。</summary>
public sealed class BackupExportRequest
{
    /// <summary>加密口令（至少 6 位）。服务端**不保存**它——口令丢了这份备份就解不开。</summary>
    public string? Passphrase { get; set; }
}
