using System.Net;
using System.Text;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Options;
using ControlHub.Server.Services;
using Microsoft.Extensions.Options;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// Prometheus 文本格式指标（<c>GET /metrics</c>），给 Prometheus / Zabbix / VictoriaMetrics 这类系统抓取。
/// <para>采集端通常走不了登录流程，所以放行方式二选一：配置了 <c>ControlHub:MetricsToken</c> 就必须带
/// <c>?token=</c> 或 <c>Authorization: Bearer</c>；**没有配令牌时只允许本机访问**（同机采集器的场景）。
/// 这样默认不会把设备规模、数据目录这些信息暴露给整个内网。</para>
/// </summary>
public static class MetricsEndpoints
{
    /// <summary>注册路由。</summary>
    public static void MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/metrics", MetricsAsync).AllowAnonymous().WithTags("public");
    }

    private static async Task<IResult> MetricsAsync(
        HttpContext http,
        HubStore store,
        IOptions<ServerOptions> options,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!IsAllowed(http, settings))
        {
            return Results.Json(
                ApiResult<object>.Failure(HubErrorCodes.AuthInvalid,
                    "指标接口未授权：请在请求里带上 ?token=，或从本机访问。"),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var devices = await store.GetDevicesAsync(cancellationToken);
        var revision = await store.GetRevisionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var online = devices.Count(d => !d.Revoked && d.LastSeenAt.HasValue
            && (now - d.LastSeenAt.Value).TotalSeconds < settings.OnlineTimeoutSeconds);

        var text = new StringBuilder();

        Append(text, "controlhub_up", "服务端是否在运行（1 = 是）", "gauge", 1);
        Append(text, "controlhub_uptime_seconds", "服务端进程已运行时长（秒）", "gauge",
            (long)ServerRuntime.Uptime.TotalSeconds);
        Append(text, "controlhub_config_revision", "当前配置版本号（有变更即递增）", "gauge", revision);
        Append(text, "controlhub_devices_total", "设备总数（不含已停用）", "gauge",
            devices.Count(d => !d.Revoked));
        Append(text, "controlhub_devices_online", "在线设备数", "gauge", online);
        Append(text, "controlhub_devices_offline", "离线设备数", "gauge", devices.Count(d => !d.Revoked) - online);
        Append(text, "controlhub_devices_pending_sync", "配置待同步的设备数", "gauge",
            devices.Count(d => !d.Revoked && d.AppliedRevision < revision));
        Append(text, "controlhub_devices_revoked", "已停用设备数", "gauge", devices.Count(d => d.Revoked));
        Append(text, "controlhub_devices_error", "处于异常状态的设备数", "gauge",
            devices.Count(d => !d.Revoked
                && string.Equals(d.State, DeviceStates.Error, StringComparison.OrdinalIgnoreCase)));

        // 数据库与磁盘：学校最常见的故障就是磁盘写满，因此这两个一起给。
        var dataDirectory = settings.ResolveDataDirectory(environment.ContentRootPath);
        try
        {
            var databasePath = settings.ResolveDatabasePath(environment.ContentRootPath);
            if (File.Exists(databasePath))
            {
                Append(text, "controlhub_database_size_bytes", "SQLite 数据文件体积（字节）", "gauge",
                    new FileInfo(databasePath).Length);
            }
        }
        catch
        {
            // 取不到就跳过该项，不影响其它指标。
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dataDirectory));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                Append(text, "controlhub_disk_free_bytes", "数据目录所在磁盘剩余空间（字节）", "gauge",
                    drive.AvailableFreeSpace);
                Append(text, "controlhub_disk_total_bytes", "数据目录所在磁盘总容量（字节）", "gauge", drive.TotalSize);
            }
        }
        catch
        {
            // 同上。
        }

        return Results.Text(text.ToString(), "text/plain; version=0.0.4; charset=utf-8");
    }

    /// <summary>
    /// 放行判定：配了令牌就校验令牌（查询参数或 Bearer 均可，方便不同采集器）；
    /// 没配令牌时**只允许本机**，避免默认把内部状态暴露到内网。
    /// </summary>
    private static bool IsAllowed(HttpContext http, ServerOptions settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.MetricsToken))
        {
            var provided = http.Request.Query["token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(provided))
            {
                var header = http.Request.Headers.Authorization.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    provided = header["Bearer ".Length..].Trim();
                }
            }

            return !string.IsNullOrWhiteSpace(provided)
                   && string.Equals(provided, settings.MetricsToken, StringComparison.Ordinal);
        }

        var remote = http.Connection.RemoteIpAddress;
        return remote is not null && IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);
    }

    private static void Append(StringBuilder text, string name, string help, string type, double value)
    {
        text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        text.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        text.Append(name).Append(' ').Append(value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            .Append('\n');
    }
}
