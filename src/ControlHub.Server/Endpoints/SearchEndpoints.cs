using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;
using ControlHub.Server.Http;

namespace ControlHub.Server.Endpoints;

/// <summary>
/// 全局搜索（#41）：一个关键字同时查设备 / 分组 / 档案 / 账号。
///
/// 设计取舍：
///   - <b>只要求「已登录」</b>，不要求某个具体模块权限——搜索是跨模块能力，
///     要求 <c>devices.read</c> 才能搜反而挡住了只负责通知的老师；改为**按权限过滤结果类别**，
///     搜不到就是没权限看，而不是整个接口 403。
///   - **服务端聚合**而不是前端过滤已加载的列表：设备可能有几百台，前端不该为了搜索先拉全量；
///     而且账号列表本来就不该无权限时下发。
///   - 关键字**只做包含匹配**，不解释语法（没有引号 / 通配符 / 字段前缀），行为可预期。
/// </summary>
public static class SearchEndpoints
{
    /// <summary>默认返回条数。</summary>
    private const int DefaultLimit = 20;

    /// <summary>单次返回上限（防止把整库倒给前端）。</summary>
    private const int MaxLimit = 50;

    /// <summary>关键字长度上限。</summary>
    private const int MaxQueryLength = 64;

    /// <summary>每一类最多返回几条，避免「设备特别多」时把其它类别挤没。</summary>
    private const int PerKindLimit = 6;

    /// <summary>注册全局搜索路由。</summary>
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.NewVersionedGroup("admin")
            .AddEndpointFilter<AdminAuthFilter>()
            .AddEndpointFilter<AdminPermissionFilter>();

        group.MapGet("/search", SearchAsync).RequirePermission(PermissionKeys.Authenticated);
    }

    private static async Task<ApiResult<SearchResultDto>> SearchAsync(
        HttpContext http,
        HubStore store,
        CancellationToken cancellationToken,
        string? q = null,
        int? limit = null)
    {
        var session = http.RequireAdminSession();
        var keyword = (q ?? string.Empty).Trim();

        if (keyword.Length == 0)
        {
            throw HubException.Validation("请输入搜索关键字。");
        }

        if (keyword.Length > MaxQueryLength)
        {
            throw HubException.Validation($"关键字过长（最多 {MaxQueryLength} 个字符）。");
        }

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var canDevices = PermissionKeys.Satisfies(session.Role, session.Permissions, PermissionKeys.DevicesRead);
        var canProfiles = PermissionKeys.Satisfies(session.Role, session.Permissions, PermissionKeys.ProfilesRead);
        var canAccounts = PermissionKeys.Satisfies(session.Role, session.Permissions, PermissionKeys.AccountsRead);

        var hits = new List<Hit>();

        if (canDevices)
        {
            await CollectDevicesAsync(store, keyword, hits, cancellationToken);
            await CollectGroupsAsync(store, keyword, hits, cancellationToken);
        }

        if (canProfiles)
        {
            await CollectProfilesAsync(store, keyword, hits, cancellationToken);
        }

        if (canAccounts)
        {
            await CollectAccountsAsync(store, keyword, hits, cancellationToken);
        }

        var items = hits
            .OrderBy(h => h.Rank)
            .ThenBy(h => h.KindWeight)
            .ThenBy(h => h.Title.Length)
            .ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(h => h.Item)
            .ToList();

        return ApiResult<SearchResultDto>.Success(new SearchResultDto
        {
            Query = keyword,
            Items = items,
        });
    }

    /// <summary>命中项：排序用元数据 + 给前端的条目。</summary>
    private sealed record Hit(int Rank, int KindWeight, string Title, SearchItemDto Item);

    /// <summary>类别权重：同等相关度时，设备排在档案前（日常最常找设备）。</summary>
    private static int KindWeight(string kind) => kind switch
    {
        "device" => 0,
        "profile" => 1,
        "group" => 2,
        _ => 3,
    };

    /// <summary>
    /// 匹配打分：0 = 标题前缀命中，1 = 标题包含，2 = 仅副字段命中，-1 = 不命中。
    /// <para>前缀优先是刻意的：搜「高一」时，「高一(1)班」应该排在「应届高一(2)班」之前。</para>
    /// </summary>
    private static int Rank(string keyword, string title, params string?[] extraFields)
    {
        if (title.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        foreach (var field in extraFields)
        {
            if (!string.IsNullOrEmpty(field) && field.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
        }

        return -1;
    }

    private static async Task CollectDevicesAsync(HubStore store, string keyword, List<Hit> hits,
        CancellationToken cancellationToken)
    {
        var devices = await store.GetDevicesAsync(cancellationToken);
        var added = 0;
        foreach (var device in devices)
        {
            if (added >= PerKindLimit)
            {
                break;
            }

            var rank = Rank(keyword, device.Name, device.MachineName, device.IpAddress, device.Remark);
            if (rank < 0)
            {
                continue;
            }

            var subtitle = string.Join(" · ", new[]
            {
                string.IsNullOrWhiteSpace(device.MachineName) ? null : device.MachineName,
                string.IsNullOrWhiteSpace(device.IpAddress) ? null : device.IpAddress,
                string.IsNullOrWhiteSpace(device.Remark) ? null : device.Remark,
                device.Revoked ? "已停用" : null,
            }.Where(s => s is not null));

            hits.Add(new Hit(rank, KindWeight("device"), device.Name, new SearchItemDto
            {
                Kind = "device",
                Title = device.Name,
                Subtitle = subtitle,
                // 跳到设备页并预填搜索框：用户落地就能看到那一台（而不是落在几百台的列表里自己再找一遍）。
                Hash = $"#/devices?kw={Uri.EscapeDataString(device.Name)}",
            }));
            added++;
        }
    }

    private static async Task CollectGroupsAsync(HubStore store, string keyword, List<Hit> hits,
        CancellationToken cancellationToken)
    {
        var groups = await store.GetGroupsAsync(cancellationToken);
        var added = 0;
        foreach (var group in groups)
        {
            if (added >= PerKindLimit)
            {
                break;
            }

            var rank = Rank(keyword, group.Name, group.Description);
            if (rank < 0)
            {
                continue;
            }

            hits.Add(new Hit(rank, KindWeight("group"), group.Name, new SearchItemDto
            {
                Kind = "group",
                Title = group.Name,
                Subtitle = string.Join(" · ", new[]
                {
                    DescribeGroupKind(group.Kind),
                    string.IsNullOrWhiteSpace(group.Description) ? null : group.Description,
                }.Where(s => !string.IsNullOrEmpty(s))),
                // 设备页支持按分组筛选（#/devices?group=xxx）。
                Hash = $"#/devices?group={Uri.EscapeDataString(group.Id)}",
            }));
            added++;
        }
    }

    private static async Task CollectProfilesAsync(HubStore store, string keyword, List<Hit> hits,
        CancellationToken cancellationToken)
    {
        var profiles = await store.GetProfilesAsync(cancellationToken);
        var added = 0;
        foreach (var profile in profiles)
        {
            if (added >= PerKindLimit)
            {
                break;
            }

            var rank = Rank(keyword, profile.Name, profile.Description, profile.Code);
            if (rank < 0)
            {
                continue;
            }

            hits.Add(new Hit(rank, KindWeight("profile"), profile.Name, new SearchItemDto
            {
                Kind = "profile",
                Title = profile.Name,
                Subtitle = string.Join(" · ", new[]
                {
                    string.IsNullOrWhiteSpace(profile.Code) ? null : $"识别码 {profile.Code}",
                    profile.IsDefault ? "默认档案" : null,
                    string.IsNullOrWhiteSpace(profile.Description) ? null : profile.Description,
                }.Where(s => s is not null)),
                // 直接进档案编辑器（#/profiles/<id>）。
                Hash = $"#/profiles/{Uri.EscapeDataString(profile.Id)}",
            }));
            added++;
        }
    }

    private static async Task CollectAccountsAsync(HubStore store, string keyword, List<Hit> hits,
        CancellationToken cancellationToken)
    {
        var users = await store.GetUsersAsync(cancellationToken);
        var added = 0;
        foreach (var user in users)
        {
            if (added >= PerKindLimit)
            {
                break;
            }

            var rank = Rank(keyword, user.DisplayName, user.Username);
            if (rank < 0)
            {
                continue;
            }

            var display = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
            hits.Add(new Hit(rank, KindWeight("account"), display, new SearchItemDto
            {
                Kind = "account",
                Title = display,
                Subtitle = string.Join(" · ", new[]
                {
                    $"@{user.Username}",
                    string.Equals(user.Role, PermissionKeys.AdministratorRole, StringComparison.OrdinalIgnoreCase)
                        ? "超级管理员"
                        : "自定义权限",
                }),
                // 账号管理在系统设置页。
                Hash = "#/settings",
            }));
            added++;
        }
    }

    private static string DescribeGroupKind(string kind) => kind switch
    {
        "building" => "楼栋",
        "floor" => "楼层",
        _ => "分组",
    };
}
