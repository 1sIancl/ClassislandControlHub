using System.Text.Json;
using System.Text.Json.Nodes;
using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;

namespace IslandHub.Server.Services;

/// <summary>
/// 时间表库与档案之间的「投影 / 提取」（#136）。
///
/// <para>
/// 时间表提升为全局实体后，库里是**唯一来源**；但现有代码（下发、导入导出、前端编辑）
/// 都还在读 <c>content.timeLayouts</c>。与其把整条链路推倒重来，不如在档案读写的边界上做两件事：
/// </para>
///
/// <list type="bullet">
///   <item>**读**：按 <c>content.timeLayoutIds</c> 把库里的时间表投影进 <c>content.timeLayouts</c>；</item>
///   <item>**写**：把 <c>content.timeLayouts</c> 提取进库，并回填 <c>content.timeLayoutIds</c>。</item>
/// </list>
///
/// <para>
/// 于是"同一张时间表被多个档案引用"自然成立：两个档案的 ids 指向同一行，
/// 改一次两边同时变，而下游代码一行没改。
/// </para>
/// </summary>
public static class TimeLayoutLibrary
{
    /// <summary>档案内容里引用全局时间表的字段名。</summary>
    public const string IdsField = "timeLayoutIds";

    /// <summary>
    /// 读取档案内容时调用：把 <c>timeLayoutIds</c> 引用的时间表投影回 <c>timeLayouts</c>。
    ///
    /// <para>
    /// 老档案没有 <c>timeLayoutIds</c>（还没迁移）时原样返回，行为与改动前完全一致。
    /// </para>
    /// </summary>
    public static async Task<string> ProjectAsync(
        HubStore store, string contentJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
        {
            return contentJson;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(contentJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return contentJson;
        }

        var ids = ReadIds(root);
        if (ids.Count == 0)
        {
            return contentJson;
        }

        // 档案自带的那份快照：库里缺条目时用它兜底。
        var snapshot = root["timeLayouts"] as JsonArray;

        var projected = new JsonArray();
        foreach (var id in ids)
        {
            var row = await store.GetTimeLayoutAsync(id, cancellationToken);
            if (row is null)
            {
                // 引用的时间表在库里找不到（被删了、或引用了不存在的 ID）。
                //
                // 不能直接跳过：课表是按 timeLayoutId 指向时间表的，少一张就等于
                // 这些课表全部悬空——教室里排不出节次，而且界面上看不出哪里错了。
                // 用档案自带的快照兜底，至少内容还在；两者都没有才真的跳过。
                var fallback = snapshot?
                    .FirstOrDefault(node => string.Equals(
                        node?["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));

                if (fallback is not null)
                {
                    projected.Add(fallback.DeepClone());
                }

                continue;
            }

            projected.Add(new JsonObject
            {
                ["id"] = row.Id,
                ["name"] = row.Name,
                ["items"] = JsonNode.Parse(row.Items) ?? new JsonArray(),
            });
        }

        root["timeLayouts"] = projected;
        return root.ToJsonString();
    }

    /// <summary>
    /// 保存档案内容时调用：把 <c>timeLayouts</c> 提取进全局库，并回填 <c>timeLayoutIds</c>。
    ///
    /// <para>
    /// 提取后 <c>timeLayouts</c> 里保留一份投影（不删），因为版本历史与导出都按整包读；
    /// 但它只是快照，读取时会被库里的最新内容覆盖——不会有第二份真相。
    /// </para>
    /// </summary>
    public static async Task<string> ExtractAsync(
        HubStore store, string contentJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
        {
            return contentJson;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(contentJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return contentJson;
        }

        if (root["timeLayouts"] is not JsonArray arrays || arrays.Count == 0)
        {
            return contentJson;
        }

        var ids = new JsonArray();
        foreach (var node in arrays)
        {
            if (node is not JsonObject layout)
            {
                continue;
            }

            var id = layout["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = layout["name"]?.GetValue<string>() ?? string.Empty;
            var items = layout["items"]?.ToJsonString() ?? "[]";

            await store.UpsertTimeLayoutAsync(id, name, items, cancellationToken);
            ids.Add(id);
        }

        root[IdsField] = ids;
        return root.ToJsonString();
    }

    /// <summary>
    /// 迁移：把已有档案里内嵌的时间表搬进全局库，并给档案写上引用。
    /// <para>幂等：已经有 <c>timeLayoutIds</c> 的档案直接跳过，重复启动不会重复搬。</para>
    /// </summary>
    public static async Task<int> MigrateAsync(HubStore store, CancellationToken cancellationToken = default)
    {
        var profiles = await store.GetProfilesAsync(cancellationToken);
        var migrated = 0;

        foreach (var profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Content))
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(profile.Content);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node is not JsonObject root)
            {
                continue;
            }

            if (ReadIds(root).Count > 0)
            {
                continue;
            }

            if (root["timeLayouts"] is not JsonArray arrays || arrays.Count == 0)
            {
                continue;
            }

            var extracted = await ExtractAsync(store, profile.Content, cancellationToken);
            await store.UpdateProfileContentAsync(profile.Id, extracted, cancellationToken);
            migrated += 1;
        }

        return migrated;
    }

    private static List<string> ReadIds(JsonObject root)
    {
        var result = new List<string>();
        if (root[IdsField] is not JsonArray ids)
        {
            return result;
        }

        foreach (var node in ids)
        {
            var id = node?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id))
            {
                result.Add(id);
            }
        }

        return result;
    }
}
