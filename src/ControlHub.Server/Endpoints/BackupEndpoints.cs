using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Services;

namespace ControlHub.Server.Endpoints;

/// <summary>A 端备份接口，挂载在 <c>/api/v1/admin/backups</c> 下。</summary>
public static class BackupEndpoints
{
    /// <summary>注册备份路由。</summary>
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>();

        group.MapGet("/backups", ListAsync);
        group.MapPost("/backups", CreateAsync);
        group.MapDelete("/backups/{id}", DeleteAsync);
        group.MapGet("/backups/{id}/download", DownloadAsync);
        group.MapPost("/backups/{id}/restore", RestoreAsync);
    }

    private static ApiResult<List<BackupEntry>> ListAsync(HttpContext http, BackupService backups)
    {
        http.RequireAdminSession();
        return ApiResult<List<BackupEntry>>.Success(backups.ListBackups());
    }

    private static async Task<ApiResult<BackupEntry>> CreateAsync(
        CreateBackupRequest request,
        HttpContext http,
        BackupService backups,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var entry = backups.CreateBackup("manual", request.Note ?? string.Empty);
        await store.AddAuditAsync(session.Username, "backup.create", entry.Id,
            $"创建备份（{entry.SizeBytes} 字节）。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<BackupEntry>.Success(entry);
    }

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

    private static async Task<ApiResult<bool>> RestoreAsync(
        string id,
        HttpContext http,
        BackupService backups,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var ok = backups.RestoreBackup(id);
        await store.AddAuditAsync(session.Username, "backup.restore", id,
            $"从备份恢复数据库（{(ok ? "成功" : "失败")}），需重启服务生效。",
            http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(ok);
    }
}

/// <summary>创建备份的请求体。</summary>
public sealed class CreateBackupRequest
{
    /// <summary>备注。</summary>
    public string? Note { get; set; }
}
