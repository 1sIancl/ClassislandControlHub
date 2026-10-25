using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;
using IslandHub.Server.Http;
using IslandHub.Server.Services;

namespace IslandHub.Server.Endpoints;

/// <summary>
/// 插件市场代理缓存与插件下发（#121）。
/// <para>A 端把 ClassIsland 官方插件市场的索引与包缓存到本地，再由教室端从 A 端取包安装——
/// 教室端因此不需要访问外网。下发本身复用现有指令通道（<c>plugin.install</c> 等），
/// 逐台结果直接看指令状态。</para>
/// </summary>
public static class PluginMarketEndpoints
{
    /// <summary>插件指令的默认有效期，与远程指令保持一致。</summary>
    private static readonly TimeSpan CommandTtl = TimeSpan.FromHours(2);

    /// <summary>注册插件市场路由（管理端）。</summary>
    public static void MapPluginMarketEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/plugin-market", GetMarketAsync).RequirePermission(PermissionKeys.PluginsRead);
        group.MapPost("/plugin-market/sync", SyncMarketAsync).RequirePermission(PermissionKeys.PluginsWrite);
        group.MapPut("/plugin-market/settings", UpdateSettingsAsync).RequirePermission(PermissionKeys.PluginsWrite);

        group.MapGet("/plugin-packages", ListPackagesAsync).RequirePermission(PermissionKeys.PluginsRead);
        group.MapPost("/plugin-packages", CachePackageAsync).RequirePermission(PermissionKeys.PluginsWrite);
        group.MapDelete("/plugin-packages/{id}/{version}", DeletePackageAsync)
            .RequirePermission(PermissionKeys.PluginsWrite);

        group.MapGet("/plugin-inventory", GetInventoryAsync).RequirePermission(PermissionKeys.PluginsRead);
        group.MapPost("/plugin-inventory/refresh", RefreshInventoryAsync)
            .RequirePermission(PermissionKeys.PluginsWrite);

        group.MapGet("/plugin-deployments", ListDeploymentsAsync).RequirePermission(PermissionKeys.PluginsRead);
        group.MapGet("/plugin-deployments/{id}", GetDeploymentAsync).RequirePermission(PermissionKeys.PluginsRead);
        group.MapPost("/plugin-deployments", CreateDeploymentAsync).RequirePermission(PermissionKeys.PluginsWrite);

        // 运营标记（#104）：推荐 / 弃用 / 上次验证时间。人工维护，独立于市场索引（同步不会冲掉）。
        group.MapGet("/plugin-ops", ListOpsAsync).RequirePermission(PermissionKeys.PluginsRead);
        group.MapPut("/plugin-ops/{id}", SetOpsAsync).RequirePermission(PermissionKeys.PluginsWrite);
        // 上架自检：把"提交到官方市场"的审核清单里能自动判断的部分跑一遍。
        group.MapGet("/plugin-ops/{id}/submission", CheckSubmissionAsync)
            .RequirePermission(PermissionKeys.PluginsRead);

        // 插件图标（#122 后续）：**刻意免鉴权**——<img> 标签带不了 bearer 令牌，而图标本身就是
        // 公开的官方索引内容（插件 ID 也公开）。免鉴权才能让浏览器按 URL 缓存它们：
        // 一屏几十个图标，每次都由前端带鉴权 fetch 再转 blob 是没必要的负担。
        app.MapGet("/api/v1/plugin-icons/{id}", GetIconAsync).AllowAnonymous();

        // 插件自述文件（#121 后续）：同样免鉴权。**首次访问会触发 A 端按需下载**（缓存命中即直出），
        // 地址只取索引里登记的 readme_path，不接受任意 URL——否则这就成了开放的抓取代理。
        app.MapGet("/api/v1/plugin-readmes/{id}", GetReadmeAsync).AllowAnonymous();
    }

    /// <summary>读取全部运营标记（前端一次性拿到，避免每行一个请求）。</summary>
    private static async Task<ApiResult<List<PluginOpsDto>>> ListOpsAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var rows = await store.ListPluginOpsAsync(cancellationToken);
        return ApiResult<List<PluginOpsDto>>.Success(rows.Values
            .OrderBy(r => r.PluginId, StringComparer.Ordinal)
            .Select(ToOpsDto)
            .ToList());
    }

    /// <summary>保存一个插件的运营标记。</summary>
    private static async Task<ApiResult<PluginOpsDto>> SetOpsAsync(
        string id,
        PluginOpsRequest request,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();

        var status = (request.Status ?? "normal").Trim().ToLowerInvariant();
        if (status is not ("normal" or "recommended" or "deprecated"))
        {
            throw HubException.Validation("运营状态只能是 normal / recommended / deprecated。");
        }

        var row = new Data.PluginOpsRow
        {
            PluginId = id,
            Status = status,
            Note = (request.Note ?? string.Empty).Trim(),
            VerifiedAt = DateTimeOffset.UtcNow,
            UpdatedBy = session.Username,
        };
        await store.SetPluginOpsAsync(row, cancellationToken);

        // 审计有意义：这条标记会影响所有人看到的"推荐 / 别用"。
        await store.AddAuditAsync(session.Username, "plugin.ops", id,
            $"设置插件运营标记：{status}{(row.Note.Length > 0 ? $"，备注「{row.Note}」" : string.Empty)}。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<PluginOpsDto>.Success(ToOpsDto(row));
    }

    /// <summary>
    /// 上架自检（#104）：审核清单里"能自动判断"的那几项。
    /// <para>
    /// 只读本地已同步的索引与缓存状态，**不访问外网**——自检不该因为外部站点慢而失败。
    /// 判断不了的项目（文案质量、截图好不好看）不在这里装样子，写进文档让人看。
    /// </para>
    /// </summary>
    private static async Task<ApiResult<PluginSubmissionCheckDto>> CheckSubmissionAsync(
        string id,
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken)
    {
        http.RequireAdminSession();

        var item = await store.GetMarketItemAsync(id, cancellationToken)
                   ?? throw HubException.NotFound("市场索引里没有这个插件（先同步一次市场）。");
        var packages = await store.ListPackagesAsync(cancellationToken);
        var cached = packages.Where(p => string.Equals(p.PluginId, id, StringComparison.Ordinal)).ToList();

        var items = new List<PluginSubmissionItemDto>
        {
            Item("插件 ID", item.PluginId.Length > 0, item.PluginId, "官方索引里必须有唯一 ID"),
            Item("名称", item.Name.Length > 0, item.Name, "列表里显示的名字"),
            Item("版本号", item.Version.Length > 0, item.Version, "发版时打标签的那个版本"),
            Item("API 版本", item.ApiVersion.Length > 0, item.ApiVersion, "与编译所用 PluginSdk 对齐"),
            Item("作者", item.Author.Length > 0, item.Author, "署名"),
            Item("简介", item.Description.Length > 0, item.Description, "一句话说明这个插件做什么"),
            Item("图标", item.IconPath.Length > 0, item.IconPath, "清单里的 icon"),
            Item("自述文件", item.ReadmePath.Length > 0, item.ReadmePath, "清单里的 readme"),
            Item("仓库地址", item.RepoUrl.Length > 0, item.RepoUrl, "用户要能看到源码与 issue"),
            Item("安装包已缓存", cached.Count > 0,
                cached.Count > 0 ? $"{cached.Count} 个版本" : "未缓存（教室端装不了）", "缓存后才能下发给教室端"),
        };

        return ApiResult<PluginSubmissionCheckDto>.Success(new PluginSubmissionCheckDto
        {
            PluginId = id,
            Ready = items.Where(i => i.Required).All(i => i.Ok),
            Items = items,
        });

        static PluginSubmissionItemDto Item(string name, bool ok, string detail, string hint) => new()
        {
            Name = name,
            Ok = ok,
            Detail = ok ? detail : hint,
        };
    }

    private static PluginOpsDto ToOpsDto(Data.PluginOpsRow row) => new()
    {
        PluginId = row.PluginId,
        Status = row.Status,
        Note = row.Note,
        VerifiedAt = row.VerifiedAt.ToString("O"),
        UpdatedBy = row.UpdatedBy,
    };

    /// <summary>
    /// 取一个插件的自述文件（Markdown 原文）。取不到返回 404，前端据此显示「去仓库看」而不是空白。
    /// </summary>
    private static async Task<IResult> GetReadmeAsync(string id, HttpContext http,
        PluginPackageCacheService cache, CancellationToken cancellationToken)
    {
        var path = await cache.EnsureReadmeAsync(id, cancellationToken);
        if (path is null || !File.Exists(path))
        {
            return Results.NotFound();
        }

        // 文件名带地址哈希 ⇒ 内容变了 URL 才会变，可以长期缓存。
        http.Response.Headers.CacheControl = "public, max-age=604800";
        return Results.File(path, "text/markdown; charset=utf-8");
    }

    /// <summary>
    /// 取一个已缓存到本地的插件图标。
    /// <para>没缓存（或缓存文件被清掉，例如只搬了数据库换机器）就回 404——管理端据此退化成
    /// 首字母头像，而不是留一张碎图。</para>
    /// </summary>
    private static async Task<IResult> GetIconAsync(string id, HttpContext http, HubStore store,
        PluginPackageCacheService cache, CancellationToken cancellationToken)
    {
        var row = await store.GetMarketItemAsync(id, cancellationToken);
        if (row is null || row.IconFile.Length == 0)
        {
            return Results.NotFound();
        }

        var path = cache.ResolveIconPath(row.IconFile);
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        // 文件名里带地址哈希 ⇒ URL 变了内容才可能变，可以放心让浏览器长期缓存。
        http.Response.Headers.CacheControl = "public, max-age=604800";
        return Results.File(path, IconContentType(row.IconFile));
    }

    /// <summary>按扩展名给出图标的 MIME 类型。</summary>
    private static string IconContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };

    // ────────────────────────────── 市场与缓存 ──────────────────────────────

    /// <summary>市场列表（含缓存状态）与市场设置。</summary>
    private static async Task<ApiResult<object>> GetMarketAsync(HttpContext http, HubStore store,
        PluginPackageCacheService cache, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        return ApiResult<object>.Success(new
        {
            items = await BuildMarketItemsAsync(store, cache, cancellationToken),
            settings = await BuildSettingsAsync(store, cache, cancellationToken),
        });
    }

    /// <summary>立刻同步一次索引。</summary>
    private static async Task<ApiResult<PluginMarketSyncResultDto>> SyncMarketAsync(HttpContext http,
        HubStore store, PluginMarketSyncService sync, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var result = await sync.SyncAsync(cancellationToken);

        await store.AddAuditAsync(session.Username, "plugin.market.sync", "插件市场",
            result.Ok
                ? $"同步插件索引成功，共 {result.Count} 个插件（来源 {result.Source}）。"
                : $"同步插件索引失败：{result.Error}",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<PluginMarketSyncResultDto>.Success(result);
    }

    /// <summary>修改市场设置（索引地址 / 首选镜像）。</summary>
    private static async Task<ApiResult<PluginMarketSettingsDto>> UpdateSettingsAsync(
        PluginMarketSettingsUpdateRequest request, HttpContext http, HubStore store,
        PluginPackageCacheService cache, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var changed = new List<string>();

        if (request.IndexUrl is { } indexUrl)
        {
            var value = indexUrl.Trim();
            // 用 "-" 表示「恢复官方默认」：留空无法区分「不改」与「清空」，而这一个地址不能为空。
            value = value == "-" ? PluginMarketSyncService.DefaultIndexUrl : value;
            if (value.Length == 0)
            {
                throw HubException.Validation("索引地址不能为空；要恢复默认请填 -。");
            }

            await store.SetSettingAsync(PluginMarketSyncService.IndexUrlKey, value, cancellationToken);
            changed.Add("索引地址");
        }

        if (request.PreferredMirror is { } mirror)
        {
            var value = mirror.Trim();
            if (value == "-")
            {
                value = string.Empty;
            }

            if (value.Length > 0)
            {
                var mirrors = await cache.LoadMirrorsAsync(cancellationToken);
                if (!mirrors.ContainsKey(value))
                {
                    throw HubException.Validation($"索引里没有名为 {value} 的镜像。");
                }
            }

            await store.SetSettingAsync(PluginMarketSyncService.PreferredMirrorKey, value, cancellationToken);
            changed.Add("首选镜像");
        }

        if (changed.Count > 0)
        {
            await store.AddAuditAsync(session.Username, "plugin.market.settings", "插件市场",
                $"修改插件市场设置：{string.Join("、", changed)}。", http.GetClientIpAddress(), cancellationToken);
        }

        return ApiResult<PluginMarketSettingsDto>.Success(
            await BuildSettingsAsync(store, cache, cancellationToken));
    }

    /// <summary>已缓存的插件包列表。</summary>
    private static async Task<ApiResult<List<PluginPackageDto>>> ListPackagesAsync(HttpContext http,
        HubStore store, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var rows = await store.ListPackagesAsync(cancellationToken);
        return ApiResult<List<PluginPackageDto>>.Success(rows.Select(ToPackageDto).ToList());
    }

    /// <summary>下载并缓存一个插件包（<c>Version</c> 留空 = 索引里的最新版）。</summary>
    private static async Task<ApiResult<PluginPackageDto>> CachePackageAsync(PluginCacheRequest request,
        HttpContext http, HubStore store, PluginPackageCacheService cache, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        if (string.IsNullOrWhiteSpace(request.PluginId))
        {
            throw HubException.Validation("缺少插件 ID。");
        }

        var row = await cache.CacheAsync(request.PluginId.Trim(), request.Version, cancellationToken);
        await store.AddAuditAsync(session.Username, "plugin.cache", row.PluginId,
            $"缓存插件包 {row.PluginId} {row.Version}（{row.SizeBytes} 字节，镜像 {row.SourceMirror}，SHA256 {row.Sha256}）。",
            http.GetClientIpAddress(), cancellationToken);

        return ApiResult<PluginPackageDto>.Success(ToPackageDto(row));
    }

    /// <summary>删除一个已缓存的插件包。</summary>
    private static async Task<ApiResult<bool>> DeletePackageAsync(string id, string version, HttpContext http,
        HubStore store, PluginPackageCacheService cache, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        if (!await cache.DeleteAsync(id, version, cancellationToken))
        {
            throw HubException.NotFound("没有这个缓存。");
        }

        await store.AddAuditAsync(session.Username, "plugin.cache.delete", id,
            $"删除插件包缓存 {id} {version}。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<bool>.Success(true);
    }

    // ────────────────────────────── 已安装清单 ──────────────────────────────

    /// <summary>全部设备的已安装插件清单（按设备维度）。</summary>
    private static async Task<ApiResult<object>> GetInventoryAsync(HttpContext http, HubStore store,
        SyncService sync, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var devices = await store.GetDevicesAsync(cancellationToken);
        var items = new List<object>();
        foreach (var device in devices)
        {
            var raw = await store.GetSettingAsync($"device_plugins.{device.Id}", "[]", cancellationToken);
            var plugins = HubJson.DeserializeOrDefault(raw ?? "[]", new List<PluginInfoDto>());
            items.Add(new
            {
                deviceId = device.Id,
                deviceName = device.Name,
                online = sync.IsOnline(device),
                revoked = device.Revoked,
                classislandVersion = device.ClassIslandVersion,
                pluginVersion = device.PluginVersion,
                reportedAt = await store.GetSettingAsync($"device_plugins_at.{device.Id}", string.Empty,
                    cancellationToken),
                plugins,
            });
        }

        return ApiResult<object>.Success(new { devices = items });
    }

    /// <summary>让在线教室端重新上报插件清单（批量 <c>plugin.refresh</c>）。</summary>
    private static async Task<ApiResult<int>> RefreshInventoryAsync(HttpContext http, HubStore store,
        SyncService sync, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var devices = await store.GetDevicesAsync(cancellationToken);
        var online = devices.Where(d => !d.Revoked && sync.IsOnline(d)).ToList();

        foreach (var device in online)
        {
            await store.CreateCommandAsync(NewCommand(device, RemoteCommandKinds.PluginRefresh, string.Empty,
                session.Username), cancellationToken);
        }

        if (online.Count > 0)
        {
            sync.PublishWakeUp();
        }

        await store.AddAuditAsync(session.Username, "plugin.inventory.refresh", "多设备",
            $"请求 {online.Count} 台在线设备重新上报插件清单。", http.GetClientIpAddress(), cancellationToken);
        return ApiResult<int>.Success(online.Count);
    }

    // ────────────────────────────── 批量下发 ──────────────────────────────

    /// <summary>
    /// 下发插件操作。安装走 <c>plugin.install</c>（包由 A 端缓存提供），
    /// 卸载 / 启停复用已有指令。
    /// </summary>
    private static async Task<ApiResult<PluginDeployResultDto>> CreateDeploymentAsync(
        PluginDeployRequest request, HttpContext http, HubStore store, SyncService sync,
        WebhookService webhooks, CancellationToken cancellationToken)
    {
        var session = http.RequireAdminSession();
        var pluginId = (request.PluginId ?? string.Empty).Trim();
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();

        if (!PluginDeployActions.IsValid(action))
        {
            throw HubException.Validation("动作只能是 install / uninstall / enable / disable。");
        }

        if (pluginId.Length == 0)
        {
            throw HubException.Validation("缺少插件 ID。");
        }

        PluginMarketRow? item = null;
        PluginPackageRow? package = null;
        var version = (request.Version ?? string.Empty).Trim();

        if (action == PluginDeployActions.Install)
        {
            item = await store.GetMarketItemAsync(pluginId, cancellationToken)
                   ?? throw HubException.Validation("插件不在本地索引里，请先同步插件市场索引。");

            if (version.Length == 0)
            {
                version = item.Version;
            }

            if (version.Length == 0)
            {
                throw HubException.Validation("无法确定要安装的版本，请显式指定。");
            }

            package = await store.GetPackageAsync(pluginId, version, cancellationToken)
                      ?? throw HubException.Validation(
                          $"A 端还没有缓存 {pluginId} {version}，请先在插件市场里点「缓存」。");
        }

        var devices = await store.GetDevicesAsync(cancellationToken);
        var (targets, skippedOffline) = SelectTargets(devices, request.DeviceIds, request.IncludeOffline, sync);

        var skips = new List<PluginDeploySkipDto>();
        if (skippedOffline > 0)
        {
            skips.Add(new PluginDeploySkipDto
            {
                DeviceId = string.Empty,
                DeviceName = "（离线设备）",
                Reason = $"{skippedOffline} 台离线设备被跳过；需要它们上线后也执行的话请勾选「包含离线设备」。",
            });
        }

        var final = new List<DeviceRow>();
        foreach (var device in targets)
        {
            var reason = await CheckAsync(device, action, item, package, version, store, cancellationToken);
            if (reason is not null)
            {
                skips.Add(new PluginDeploySkipDto
                {
                    DeviceId = device.Id,
                    DeviceName = device.Name,
                    Reason = reason,
                });
                continue;
            }

            final.Add(device);
        }

        var result = new PluginDeployResultDto { Skips = skips, Skipped = skips.Count };
        if (final.Count == 0)
        {
            // 一台都没下发就不留记录：否则历史里会堆满「什么都没做」的空批次。
            return ApiResult<PluginDeployResultDto>.Success(result);
        }

        var deployment = new PluginDeploymentRow
        {
            Id = HubChecksum.NewId(),
            PluginId = pluginId,
            PluginName = item?.Name ?? string.Empty,
            Version = version,
            Action = action,
            TargetSummary = DescribeTargets(request, devices, targets.Count),
            CreatedBy = session.Username,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.CreateDeploymentAsync(deployment, cancellationToken);

        var (kind, payload) = BuildPayload(action, pluginId, version, package, request.RestartAfter);
        foreach (var device in final)
        {
            var row = NewCommand(device, kind, payload, session.Username,
                deploymentId: deployment.Id, issuedAt: deployment.CreatedAt);
            await store.CreateCommandAsync(row, cancellationToken);
            result.CommandIds.Add(row.Id);

            // 安装只有重启才生效：勾了「顺带重启」就紧跟一条 restart（不进本批记录，
            // 免得进度面板里每台设备出现两行、看不出哪行才是「包到位了」）。
            if (action == PluginDeployActions.Install && request.RestartAfter)
            {
                await store.CreateCommandAsync(
                    NewCommand(device, RemoteCommandKinds.Restart, string.Empty, session.Username,
                        issuedAt: deployment.CreatedAt.AddSeconds(1)), cancellationToken);
            }
        }

        sync.PublishWakeUp();
        result.DeploymentId = deployment.Id;
        result.Affected = final.Count;

        await store.AddAuditAsync(session.Username, "plugin.deploy", pluginId,
            $"插件 {action}：{pluginId} {version} → {final.Count} 台设备"
            + (request.RestartAfter && action == PluginDeployActions.Install ? "（含重启）" : string.Empty)
            + (skips.Count > 0 ? $"，跳过 {skips.Count} 台。" : "。"),
            http.GetClientIpAddress(), cancellationToken);

        // 外部通知（#75）：插件下发是「改了教室机上跑什么代码」的动作，订阅方要能看见。
        // 这里报的是**已发出**（逐台安装结果要等教室端回报，管理端在进度面板里看）。
        await webhooks.NotifyAsync(WebhookEvents.PluginDeployed,
            $"插件已下发：{deployment.PluginName ?? pluginId}",
            $"「{deployment.PluginName ?? pluginId}」{version} 已下发到 {final.Count} 台设备"
            + (skips.Count > 0 ? $"，跳过 {skips.Count} 台" : string.Empty) + "。",
            new Dictionary<string, object?>
            {
                ["pluginId"] = pluginId,
                ["pluginName"] = deployment.PluginName ?? pluginId,
                ["version"] = version,
                ["action"] = action,
                ["affected"] = final.Count,
                ["skipped"] = skips.Count,
            }, cancellationToken);

        return ApiResult<PluginDeployResultDto>.Success(result);
    }

    /// <summary>下发记录列表（含逐台进度统计）。</summary>
    private static async Task<ApiResult<List<PluginDeploymentDto>>> ListDeploymentsAsync(HttpContext http,
        HubStore store, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var rows = await store.ListDeploymentsAsync(30, cancellationToken);
        var result = new List<PluginDeploymentDto>();
        foreach (var row in rows)
        {
            result.Add(await ToDeploymentDtoAsync(row, store, cancellationToken));
        }

        return ApiResult<List<PluginDeploymentDto>>.Success(result);
    }

    /// <summary>单条下发记录（含逐台明细）。</summary>
    private static async Task<ApiResult<PluginDeploymentDto>> GetDeploymentAsync(string id, HttpContext http,
        HubStore store, CancellationToken cancellationToken)
    {
        http.RequireAdminSession();
        var row = await store.GetDeploymentAsync(id, cancellationToken)
                  ?? throw HubException.NotFound("没有这条下发记录。");
        return ApiResult<PluginDeploymentDto>.Success(await ToDeploymentDtoAsync(row, store, cancellationToken));
    }

    // ────────────────────────────── 内部工具 ──────────────────────────────

    /// <summary>
    /// 下发前的逐台预校验。返回非空 = 跳过该设备的理由。
    /// <para>卸载 / 启停<b>刻意不做</b>「是否已安装」判断：清单是上一次上报的快照，
    /// 拿它拦人会在插件刚装好还没上报时把操作拦掉，管理员却以为下发成功了；
    /// 真没装的话教室端会回 <c>COMMAND_TARGET_NOT_FOUND</c>，理由同样清楚。</para>
    /// </summary>
    private static async Task<string?> CheckAsync(DeviceRow device, string action, PluginMarketRow? item,
        PluginPackageRow? package, string version, HubStore store, CancellationToken cancellationToken)
    {
        if (action != PluginDeployActions.Install)
        {
            return null;
        }

        // 宿主版本兼容：插件声明的 apiVersion 是**最低**宿主要求（宿主只拦 < 2.0.0.0，等于不拦），
        // 因此这条判断只能由 A 端来做——不拦的话，教室里会装上一个根本加载不起来的插件。
        if (item is not null && item.ApiVersion.Length > 0
            && Version.TryParse(item.ApiVersion, out var required)
            && Version.TryParse(device.ClassIslandVersion, out var actual)
            && actual < required)
        {
            return $"教室端 ClassIsland {actual} 低于插件要求的最低版本 {required}";
        }

        // 平台：教室端都是 Windows（本插件的 B 端就是 Windows 专用），
        // 所以只拦「声明了支持平台且不含 Windows」的插件。
        if (item is not null)
        {
            var platforms = HubJson.DeserializeOrDefault(item.SupportedOs, new List<string>());
            if (platforms.Count > 0
                && !platforms.Any(p => p.Contains("Windows", StringComparison.OrdinalIgnoreCase)))
            {
                return $"插件声明仅支持 {string.Join(" / ", platforms)}，教室端是 Windows";
            }
        }

        // 已装同版本：重复安装要重启一次宿主，纯属折腾。
        var installedRaw = await store.GetSettingAsync($"device_plugins.{device.Id}", "[]", cancellationToken);
        var installed = HubJson.DeserializeOrDefault(installedRaw ?? "[]", new List<PluginInfoDto>());
        var current = installed.FirstOrDefault(p => string.Equals(p.Id, item?.PluginId, StringComparison.OrdinalIgnoreCase))
                      ?? installed.FirstOrDefault(p =>
                          package is not null
                          && string.Equals(p.Id, package.PluginId, StringComparison.OrdinalIgnoreCase));
        if (current is not null && string.Equals(current.Version, version, StringComparison.OrdinalIgnoreCase))
        {
            return $"已安装同一版本（{current.Version}）";
        }

        return null;
    }

    /// <summary>构造下发指令的类型与载荷。</summary>
    private static (string Kind, string Payload) BuildPayload(string action, string pluginId, string version,
        PluginPackageRow? package, bool restartAfter)
    {
        switch (action)
        {
            case PluginDeployActions.Install when package is not null:
                // 地址给**相对路径**：教室端用自己的服务器地址与设备令牌去取，
                // 免得 A 端把自己的外网地址（可能经过反代）写死进指令。
                var payload = new PluginInstallPayload
                {
                    PluginId = package.PluginId,
                    Version = package.Version,
                    CipxUrl = $"/client/plugin-packages/{Uri.EscapeDataString(package.PluginId)}"
                              + $"/{Uri.EscapeDataString(package.Version)}/download",
                    Sha256 = package.Sha256,
                    FileName = package.FileName,
                };
                return (RemoteCommandKinds.PluginInstall, HubJson.Serialize(payload));

            case PluginDeployActions.Uninstall:
                return (RemoteCommandKinds.PluginUninstall,
                    HubJson.Serialize(new PluginTogglePayload { PluginId = pluginId }));

            case PluginDeployActions.Enable:
            case PluginDeployActions.Disable:
                return (RemoteCommandKinds.PluginToggle,
                    HubJson.Serialize(new PluginTogglePayload
                    {
                        PluginId = pluginId,
                        Enabled = action == PluginDeployActions.Enable,
                    }));

            default:
                throw HubException.Validation($"动作 {action} 不支持下发。");
        }
    }

    /// <summary>市场条目 DTO（展开镜像、带缓存状态）。</summary>
    private static async Task<List<PluginMarketItemDto>> BuildMarketItemsAsync(HubStore store,
        PluginPackageCacheService cache, CancellationToken cancellationToken)
    {
        var rows = await store.ListMarketIndexAsync(cancellationToken);
        var packages = await store.ListPackagesAsync(cancellationToken);
        var mirrors = await cache.LoadMirrorsAsync(cancellationToken);
        var preferred = await store.GetSettingAsync(PluginMarketSyncService.PreferredMirrorKey, string.Empty,
            cancellationToken);
        var ordered = PluginMirrorResolver.Order(mirrors, preferred ?? string.Empty);
        var root = ordered.Count > 0 ? ordered[0].Root : string.Empty;

        var byPlugin = packages
            .GroupBy(p => p.PluginId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var items = new List<PluginMarketItemDto>();
        foreach (var row in rows)
        {
            byPlugin.TryGetValue(row.PluginId, out var cachedList);
            var current = cachedList?.FirstOrDefault(p =>
                string.Equals(p.Version, row.Version, StringComparison.OrdinalIgnoreCase));

            items.Add(new PluginMarketItemDto
            {
                Id = row.PluginId,
                Name = string.IsNullOrWhiteSpace(row.Name) ? row.PluginId : row.Name,
                Version = row.Version,
                ApiVersion = row.ApiVersion,
                Author = row.Author,
                Description = row.Description,
                DownloadUrl = PluginMirrorResolver.Expand(row.DownloadUrl, root),
                DownloadMd5 = row.DownloadMd5,
                IconUrl = PluginMirrorResolver.Expand(row.IconPath, root),
                // 界面用的图标地址（A 端代理，避免浏览器直连 GitHub）；空 = 还没缓存到本地，
                // 前端显示首字母头像。
                IconLocalUrl = row.IconFile.Length > 0
                    ? "/api/v1/plugin-icons/" + Uri.EscapeDataString(row.PluginId)
                    : string.Empty,
                // 自述文件是**按需取**的，所以这里恒给出地址（空表示索引里没有自述文件）：
                // 点开详情时前端请求它，服务端那时才去下载。
                ReadmeLocalUrl = row.ReadmePath.Length > 0
                    ? "/api/v1/plugin-readmes/" + Uri.EscapeDataString(row.PluginId)
                    : string.Empty,
                ReadmeUrl = PluginMirrorResolver.Expand(row.ReadmePath, root),
                RepositoryUrl = row.RepoUrl,
                SupportedOSPlatforms = HubJson.DeserializeOrDefault(row.SupportedOs, new List<string>()),
                DownloadCount = row.DownloadCount,
                StarsCount = row.StarsCount,
                Cached = current is not null,
                CachedVersions = (cachedList ?? []).Select(p => p.Version).OrderBy(v => v).ToList(),
                Sha256 = current?.Sha256 ?? string.Empty,
                SizeBytes = current?.SizeBytes ?? 0,
                CachedAt = current?.CachedAt.ToString("O") ?? string.Empty,
            });
        }

        return items;
    }

    /// <summary>市场设置 DTO。</summary>
    private static async Task<PluginMarketSettingsDto> BuildSettingsAsync(HubStore store,
        PluginPackageCacheService cache, CancellationToken cancellationToken)
    {
        return new PluginMarketSettingsDto
        {
            IndexUrl = await store.GetSettingAsync(PluginMarketSyncService.IndexUrlKey,
                PluginMarketSyncService.DefaultIndexUrl, cancellationToken) ?? PluginMarketSyncService.DefaultIndexUrl,
            PreferredMirror = await store.GetSettingAsync(PluginMarketSyncService.PreferredMirrorKey, string.Empty,
                cancellationToken) ?? string.Empty,
            Mirrors = await cache.LoadMirrorsAsync(cancellationToken),
            LastSyncAt = await store.GetSettingAsync(PluginMarketSyncService.LastSyncKey, string.Empty,
                cancellationToken) ?? string.Empty,
            LastError = await store.GetSettingAsync(PluginMarketSyncService.LastErrorKey, string.Empty,
                cancellationToken) ?? string.Empty,
        };
    }

    /// <summary>下发记录 DTO（含进度统计）。</summary>
    private static async Task<PluginDeploymentDto> ToDeploymentDtoAsync(PluginDeploymentRow row, HubStore store,
        CancellationToken cancellationToken)
    {
        var items = await store.ListDeploymentItemsAsync(row.Id, cancellationToken);
        return new PluginDeploymentDto
        {
            Id = row.Id,
            PluginId = row.PluginId,
            PluginName = string.IsNullOrWhiteSpace(row.PluginName) ? row.PluginId : row.PluginName,
            Version = row.Version,
            Action = row.Action,
            TargetSummary = row.TargetSummary,
            CreatedBy = row.CreatedBy,
            CreatedAt = row.CreatedAt.ToString("O"),
            Total = items.Count,
            // 状态词与教室端指令一致：pending / dispatched / done / failed / expired（见 CompleteCommandAsync）。
            // 成功是 "done" 而不是 "success"——写错的话进度会永远显示「成功 0 台」。
            Done = items.Count(i => i.Status == "done"),
            Failed = items.Count(i => i.Status is "failed" or "expired" or "canceled"),
            Pending = items.Count(i => i.Status is "pending" or "dispatched"),
            Items = items.Select(i => new PluginDeploymentItemDto
            {
                DeviceId = i.DeviceId,
                DeviceName = i.DeviceName,
                CommandId = i.CommandId,
                Status = i.Status,
                ErrorCode = i.ErrorCode,
                Output = i.Output,
                FinishedAt = i.FinishedAt?.ToString("O") ?? string.Empty,
            }).ToList(),
        };
    }

    private static PluginPackageDto ToPackageDto(PluginPackageRow row) => new()
    {
        Id = row.PluginId,
        Version = row.Version,
        FileName = row.FileName,
        Sha256 = row.Sha256,
        SizeBytes = row.SizeBytes,
        SourceMirror = row.SourceMirror,
        CachedAt = row.CachedAt.ToString("O"),
    };

    /// <summary>目标描述：让历史记录一眼看出「发给了谁」。</summary>
    private static string DescribeTargets(PluginDeployRequest request, List<DeviceRow> devices, int count)
    {
        if (request.DeviceIds is { Count: > 0 })
        {
            return $"{count} 台指定设备";
        }

        return request.IncludeOffline
            ? $"全部设备（{count} 台，含离线 / 共 {devices.Count(d => !d.Revoked)} 台）"
            : $"全部在线设备（{count} 台）";
    }

    /// <summary>构造一条待执行指令（与远程指令同样的有效期规则）。</summary>
    private static RemoteCommandRow NewCommand(DeviceRow device, string kind, string payload, string issuedBy,
        string? deploymentId = null, DateTimeOffset? issuedAt = null)
    {
        var now = issuedAt ?? DateTimeOffset.UtcNow;
        return new RemoteCommandRow
        {
            Id = HubChecksum.NewId(),
            DeviceId = device.Id,
            Kind = kind,
            Payload = payload,
            Status = "pending",
            IssuedAt = now,
            IssuedBy = issuedBy,
            ExpiresAt = now.Add(CommandTtl),
            DeploymentId = deploymentId,
        };
    }

    /// <summary>与远程指令一致的到位口径：默认只发在线设备，离线要显式勾选。</summary>
    private static (List<DeviceRow> Targets, int Skipped) SelectTargets(List<DeviceRow> devices,
        List<string>? deviceIds, bool includeOffline, SyncService sync)
    {
        IEnumerable<DeviceRow> pool = devices.Where(d => !d.Revoked);
        if (deviceIds is { Count: > 0 })
        {
            var wanted = new HashSet<string>(deviceIds, StringComparer.OrdinalIgnoreCase);
            pool = pool.Where(d => wanted.Contains(d.Id));
        }

        var candidates = pool.ToList();
        if (includeOffline)
        {
            return (candidates, 0);
        }

        var online = candidates.Where(sync.IsOnline).ToList();
        return (online, candidates.Count - online.Count);
    }
}
