using System.Text;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;
using ControlHub.Server.Services;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 设备批量导入 / 导出（CSV），挂载在 <c>/api/v1/admin</c> 下。
/// <para>用途：大规模部署时先在办公室把「教室名称 / 分组 / 档案 / 备注」录进系统（**预注册**，此时设备还没有令牌），
/// 教室端首次注册时按机器名自动认领这条记录，从而省掉逐台点选分组与档案。</para>
/// </summary>
public static class DeviceCsvEndpoints
{
    /// <summary>导出的列顺序（同时也是导入时认得的列名）。</summary>
    private static readonly string[] Columns =
    [
        "设备ID", "设备名称", "机器名", "分组", "配置档案", "备注",
        "内网IP", "系统版本", "ClassIsland版本", "插件版本", "在线", "状态",
        "最后心跳", "最近同步", "创建时间",
    ];

    /// <summary>注册路由。</summary>
    public static void MapDeviceCsvEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/devices/export", ExportAsync).RequirePermission(PermissionKeys.DevicesRead);
        group.MapGet("/devices/import/template", TemplateAsync).RequirePermission(PermissionKeys.DevicesRead);
        group.MapPost("/devices/import", ImportAsync).RequirePermission(PermissionKeys.DevicesWrite);
    }

    // ────────────────────────────── 导出 ──────────────────────────────

    /// <summary>
    /// 导出设备清单为 CSV。
    /// <para><c>mask=true</c>（默认）会把内网 IP 的后两段打码：导出文件常被当作excel 附件传播，
    /// 默认脱敏比事后追责划算；需要完整地址用于迁移时显式传 <c>mask=false</c>。</para>
    /// </summary>
    private static async Task<IResult> ExportAsync(
        HttpContext http,
        HubStore store,
        SyncService sync,
        CancellationToken cancellationToken,
        bool mask = true)
    {
        var session = http.RequireAdminSession();
        var devices = await store.GetDevicesAsync(cancellationToken);
        var summaries = await sync.GetDeviceSummariesAsync(devices, cancellationToken);
        var groups = (await store.GetGroupsAsync(cancellationToken)).ToDictionary(g => g.Id, g => g.Name);
        var profiles = (await store.GetProfilesAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Name);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', Columns.Select(Escape)));

        foreach (var device in summaries)
        {
            var row = new[]
            {
                device.Id,
                device.Name,
                device.MachineName,
                device.GroupId is not null && groups.TryGetValue(device.GroupId, out var groupName) ? groupName : string.Empty,
                device.ProfileId is not null && profiles.TryGetValue(device.ProfileId, out var profileName) ? profileName : string.Empty,
                device.Remark,
                mask ? MaskIp(device.IpAddress) : device.IpAddress ?? string.Empty,
                device.OsVersion,
                device.ClassIslandVersion,
                device.PluginVersion,
                device.Online ? "在线" : "离线",
                device.State,
                device.LastSeenAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
                device.LastSyncAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
                device.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            };

            csv.AppendLine(string.Join(',', row.Select(Escape)));
        }

        await store.AddAuditAsync(session.Username, "device.export.csv", "多设备",
            $"导出 {devices.Count} 台设备（IP {(mask ? "已脱敏" : "未脱敏")}）。",
            http.GetClientIpAddress(), cancellationToken);

        // 带 UTF-8 BOM：否则中文列名在 Excel 里会变乱码（这是最常被抱怨的一步）。
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        var fileName = $"devices-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        return Results.File(bytes, "text/csv; charset=utf-8", fileName);
    }

    /// <summary>下载导入模板（表头 + 两行示例）。</summary>
    private static IResult TemplateAsync()
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', Columns.Select(Escape)));
        csv.AppendLine(",_教学楼A-301,PC-A301,教学楼A,夏季作息,\"三楼东侧，班主任张老师\",,,,,,,,");
        csv.AppendLine(",_教学楼A-302,PC-A302,教学楼A,夏季作息,,,,,,,,,");

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", "devices-import-template.csv");
    }

    // ────────────────────────────── 导入（预注册 / 批量更新） ──────────────────────────────

    /// <summary>
    /// 导入设备 CSV：已存在的设备按「设备ID → 机器名」顺序匹配并更新绑定；匹配不到的按行**预注册**
    /// （无令牌，等教室端上线认领）。<c>dryRun=true</c> 时只出报告不落库。
    /// </summary>
    private static async Task<ApiResult<DeviceImportResultDto>> ImportAsync(
        DeviceImportRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        if (string.IsNullOrWhiteSpace(request.Csv))
        {
            throw HubException.Validation("CSV 内容为空。");
        }

        var lines = SplitLines(request.Csv);
        if (lines.Count < 2)
        {
            throw HubException.Validation("CSV 至少需要表头与一行数据。");
        }

        var header = ParseLine(lines[0]);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            var key = header[i].Trim().Trim('\uFEFF');
            if (key.Length > 0)
            {
                index[key] = i;
            }
        }

        if (!index.ContainsKey("设备名称"))
        {
            throw HubException.Validation("CSV 缺少「设备名称」列，请用导入模板重新填写。");
        }

        var devices = await store.GetDevicesAsync(cancellationToken);
        var byId = devices.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var byMachine = devices
            .Where(d => !string.IsNullOrWhiteSpace(d.MachineName))
            .GroupBy(d => d.MachineName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var groups = (await store.GetGroupsAsync(cancellationToken))
            .GroupBy(g => g.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var profiles = (await store.GetProfilesAsync(cancellationToken))
            .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var result = new DeviceImportResultDto { DryRun = request.DryRun };

        for (var lineIndex = 1; lineIndex < lines.Count; lineIndex++)
        {
            var cells = ParseLine(lines[lineIndex]);
            string Cell(string name) => index.TryGetValue(name, out var at) && at < cells.Count ? cells[at].Trim() : string.Empty;

            var name = Cell("设备名称");
            var machineName = Cell("机器名");
            var id = Cell("设备ID");
            var groupName = Cell("分组");
            var profileName = Cell("配置档案");
            var remark = Cell("备注");

            if (name.Length == 0 && machineName.Length == 0 && id.Length == 0)
            {
                continue; // 空行
            }

            var lineNo = lineIndex + 1;
            var item = new DeviceImportRowResultDto { Line = lineNo, DeviceName = name.Length > 0 ? name : machineName };

            // 分组 / 档案按**名称**匹配：表格里手写名字比让人去查 ID 现实得多；找不到就明确报错，不猜。
            string? groupId = null;
            if (groupName.Length > 0)
            {
                if (!groups.TryGetValue(groupName, out var found))
                {
                    item.Action = "错误";
                    item.Message = $"分组「{groupName}」不存在：请先在分组管理里建好，或把该单元格留空。";
                    result.Failed++;
                    result.Rows.Add(item);
                    continue;
                }

                groupId = found;
            }

            string? profileId = null;
            if (profileName.Length > 0)
            {
                if (!profiles.TryGetValue(profileName, out var found))
                {
                    item.Action = "错误";
                    item.Message = $"配置档案「{profileName}」不存在：请先在配置档案里建好，或把该单元格留空。";
                    result.Failed++;
                    result.Rows.Add(item);
                    continue;
                }

                profileId = found;
            }

            DeviceRow? target = null;
            if (id.Length > 0)
            {
                byId.TryGetValue(id, out target);
            }

            if (target is null && machineName.Length > 0)
            {
                byMachine.TryGetValue(machineName, out target);
            }

            if (target is not null)
            {
                item.DeviceId = target.Id;
                item.Action = request.DryRun ? "将更新" : "更新";
                var changes = new List<string>();
                if (groupId is not null)
                {
                    changes.Add($"分组→{groupName}");
                }

                if (profileId is not null)
                {
                    changes.Add($"档案→{profileName}");
                }

                if (remark.Length > 0)
                {
                    changes.Add("备注");
                }

                item.Message = changes.Count > 0 ? string.Join("，", changes) : "无变化（空单元格不会清空已有值）";

                if (!request.DryRun)
                {
                    if (name.Length > 0 && !string.Equals(name, target.Name, StringComparison.Ordinal))
                    {
                        await store.RenameDeviceAsync(target.Id, name, cancellationToken);
                    }

                    await store.UpdateDeviceBindingsAsync(target.Id, groupId, profileId,
                        remark.Length > 0 ? remark : null, cancellationToken);
                }

                result.Updated++;
                result.Rows.Add(item);
                continue;
            }

            // 匹配不到：预注册。机器名是认领的唯一凭据，没有它就无法在教室端上线时自动匹配。
            if (machineName.Length == 0 || name.Length == 0)
            {
                item.Action = "错误";
                item.Message = "既没有匹配到已有设备，又缺少「机器名」或「设备名称」，无法预注册。";
                result.Failed++;
                result.Rows.Add(item);
                continue;
            }

            if (byMachine.ContainsKey(machineName))
            {
                item.Action = "错误";
                item.Message = $"机器名「{machineName}」在表内重复，无法确定认领哪一条。";
                result.Failed++;
                result.Rows.Add(item);
                continue;
            }

            var newId = "pre-" + Guid.NewGuid().ToString("n")[..12];
            item.DeviceId = newId;
            item.Action = request.DryRun ? "将预注册" : "预注册";
            item.Message = "等教室端以该机器名首次注册时自动认领";

            if (!request.DryRun)
            {
                await store.CreatePreRegisteredDeviceAsync(newId, name, machineName, groupId, profileId, remark,
                    cancellationToken);
                byMachine[machineName] = new DeviceRow { Id = newId, Name = name, MachineName = machineName };
            }

            result.Created++;
            result.Rows.Add(item);
        }

        if (!request.DryRun)
        {
            await store.AddAuditAsync(session.Username, "device.import.csv", "多设备",
                $"导入设备 CSV：预注册 {result.Created} 台，更新 {result.Updated} 台，失败 {result.Failed} 行。",
                http.GetClientIpAddress(), cancellationToken);
        }

        return ApiResult<DeviceImportResultDto>.Success(result);
    }

    // ────────────────────────────── CSV 小工具 ──────────────────────────────

    /// <summary>内网 IP 脱敏：保留前两段，后两段打码（例如 <c>192.168.*.*</c>）。</summary>
    private static string MaskIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return string.Empty;
        }

        var parts = ip.Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.*.*" : "***";
    }

    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r')
            ? '"' + text.Replace("\"", "\"\"") + '"'
            : text;
    }

    private static List<string> SplitLines(string csv) =>
        csv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    /// <summary>解析一行 CSV（支持双引号包裹与 <c>""</c> 转义）。</summary>
    private static List<string> ParseLine(string line)
    {
        var cells = new List<string>();
        var buffer = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        buffer.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    buffer.Append(ch);
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                cells.Add(buffer.ToString());
                buffer.Clear();
            }
            else
            {
                buffer.Append(ch);
            }
        }

        cells.Add(buffer.ToString());
        return cells;
    }
}
