using System.Collections.Concurrent;
using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;
using IslandHub.Server.Http;
using IslandHub.Server.Options;
using IslandHub.Server.Services;
using Microsoft.Extensions.Options;

namespace IslandHub.Server.Endpoints;

/// <summary>导入确认请求。</summary>
public sealed class MigrationApplyRequest
{
    /// <summary>预览时拿到的一次性令牌。</summary>
    public string? Token { get; set; }
}

/// <summary>
/// 数据迁移（路线图 #61）：导出 / 预览 / 导入。
///
/// <para>
/// 两段式（先预览、再确认）：导入是**会覆盖本机数据**的操作，不能一上传就动手。
/// 预览过的包先放在内存里（30 分钟），确认时按令牌取回——几 MB 的包不必来回传两遍，
/// 也不落盘（<c>.cidata</c> 那个是几十 MB 的 ZIP，必须落盘）。
/// </para>
///
/// <para>
/// 权限：导出按「系统设置 · 查看」，预览与导入按「系统设置 · 修改」，并要求审计留痕。
/// 导入前**自动备份**（类型「导入前」），出问题可在「系统维护」里恢复。
/// </para>
/// </summary>
public static class MigrationEndpoints
{
    private static readonly ConcurrentDictionary<string, Pending> Pendings = new(StringComparer.Ordinal);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    /// <summary>注册路由。</summary>
    public static void MapMigrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/migration/export", ExportAsync).RequirePermission(PermissionKeys.SettingsRead);
        group.MapPost("/migration/preview", PreviewAsync).RequirePermission(PermissionKeys.SettingsWrite);
        group.MapPost("/migration/apply", ApplyAsync).RequirePermission(PermissionKeys.SettingsWrite);
    }

    private static async Task<ApiResult<MigrationPackageDto>> ExportAsync(
        HubStore store,
        IOptionsMonitor<ServerOptions> options,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var package = MigrationService.Export(store.DatabasePath, options.CurrentValue.ServerName);

        await store.AddAuditAsync(session.Username, "migration.export", "数据迁移",
            $"导出迁移包：{package.Tables.Count} 张表、{package.Tables.Sum(t => t.Value.Rows.Count)} 行"
            + $"（产品 {package.Product} v{package.Version}）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<MigrationPackageDto>.Success(package);
    }

    private static async Task<ApiResult<MigrationPreviewDto>> PreviewAsync(
        MigrationPackageDto request,
        HubStore store,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();

        if (!string.Equals(request.Format, MigrationPackageDto.MigrationFormat, StringComparison.OrdinalIgnoreCase))
        {
            throw HubException.Validation("这不是 IslandHub 的迁移包（format 字段不匹配），请确认选对了文件。");
        }

        // 顺手清掉过期项，避免确认不了的大包长期占内存。
        var now = DateTimeOffset.UtcNow;
        foreach (var stale in Pendings.Where(p => p.Value.Expires <= now).Select(p => p.Key))
        {
            Pendings.TryRemove(stale, out _);
        }

        if (Pendings.Count >= 20)
        {
            throw HubException.Validation("待确认的迁移包过多，请先把之前的确认掉或等它们过期（30 分钟）。");
        }

        var preview = MigrationService.Preview(store.DatabasePath, request);
        Pendings[preview.Token] = new Pending(request, now.Add(Ttl));

        await store.AddAuditAsync(http.RequireAdminSession().Username, "migration.preview", "数据迁移",
            $"上传迁移包待确认：来源 {preview.ServerName} v{preview.Version}，"
            + $"新增 {preview.TotalAdded} / 覆盖 {preview.TotalUpdated} / 跳过 {preview.TotalSkipped}。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<MigrationPreviewDto>.Success(preview);
    }

    private static async Task<ApiResult<MigrationApplyResultDto>> ApplyAsync(
        MigrationApplyRequest request,
        HubStore store,
        SyncService sync,
        BackupService backups,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        if (string.IsNullOrWhiteSpace(request.Token) || !Pendings.TryRemove(request.Token, out var pending))
        {
            throw HubException.Validation("这份预览已过期（30 分钟）或已被确认过，请重新选择文件并预览一次。");
        }

        // 导入前必须留一份备份：迁移包会把分组/档案/设置替换成本包里那一版，
        // 万一拿错了包（例如把测试机的包导到正式机上），没有备份就只能手工重建。
        var backup = backups.CreateBackup("pre-import",
            $"数据迁移导入前（来源 {pending.Package.ServerName} v{pending.Package.Version}）");

        var result = MigrationService.Apply(store.DatabasePath, pending.Package);
        result.BackupId = backup.Id;

        // 档案 / 设备可能被换过，推进修订号并让教室端重新拉配置。
        var revision = await sync.BumpRevisionAsync(cancellationToken);
        result.Notes.Add($"已推进配置版本号到 {revision}，教室端会在下一次心跳时重新拉取配置。");

        await store.AddAuditAsync(session.Username, "migration.import", "数据迁移",
            $"导入迁移包：新增 {result.Tables.Sum(t => t.Added)} / 覆盖 {result.Tables.Sum(t => t.Updated)}"
            + $" / 跳过 {result.Tables.Sum(t => t.Skipped)}（导入前备份 {backup.Id}）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<MigrationApplyResultDto>.Success(result);
    }

    /// <summary>预览过、等待确认的包。</summary>
    private sealed record Pending(MigrationPackageDto Package, DateTimeOffset Expires);
}
