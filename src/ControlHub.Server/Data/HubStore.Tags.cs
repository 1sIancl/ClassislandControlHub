using System.Text.RegularExpressions;
using ControlHub.Protocol;
using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 设备标签（#10）：标签维护、设备与标签的关联、按标签取设备。
/// </summary>
public sealed partial class HubStore
{
    /// <summary>标签名长度上限（与界面输入框一致）。</summary>
    public const int MaxTagNameLength = 24;

    /// <summary>单台设备最多挂几个标签（免得列表里铺满徽标）。</summary>
    public const int MaxTagsPerDevice = 12;

    /// <summary>颜色白名单：只接受 <c>#rgb</c> / <c>#rrggbb</c>。</summary>
    private static readonly Regex TagColorPattern = new("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");

    /// <summary>全部标签（按名称排序），带关联设备数。</summary>
    public async Task<List<TagRow>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // 只统计**未撤销**的设备：标签的用途是「挑设备去操作」，已经停用的不该算进来，
        // 否则界面上会显示「高考考场 20 台」而实际只有 18 台还活着。
        command.CommandText = """
            SELECT t.id, t.name, t.color, t.created_at,
                   (SELECT COUNT(1) FROM device_tags dt
                      JOIN devices d ON d.id = dt.device_id
                     WHERE dt.tag_id = t.id AND d.revoked = 0) AS device_count
              FROM tags t
             ORDER BY t.name COLLATE NOCASE;
            """;

        var result = new List<TagRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadTag(reader, hasCount: true));
        }

        return result;
    }

    /// <summary>按 ID 取标签（不存在返回 null）。</summary>
    public async Task<TagRow?> GetTagAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, color, created_at FROM tags WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTag(reader, hasCount: false) : null;
    }

    /// <summary>
    /// 一次取出「设备 ID → 标签」映射。
    /// <para>设备列表要给每台设备画标签徽标；逐台查询会变成 N+1，几百台设备就是几百次往返。</para>
    /// </summary>
    public async Task<Dictionary<string, List<TagRow>>> GetDeviceTagMapAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT dt.device_id, t.id, t.name, t.color, t.created_at
              FROM device_tags dt
              JOIN tags t ON t.id = dt.tag_id
             ORDER BY t.name COLLATE NOCASE;
            """;

        var map = new Dictionary<string, List<TagRow>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var deviceId = reader.GetString(0);
            var tag = new TagRow
            {
                Id = reader.GetString(1),
                Name = reader.GetString(2),
                Color = reader.GetString(3),
                CreatedAt = GetTimestampOrNow(reader, "created_at"),
            };

            if (!map.TryGetValue(deviceId, out var list))
            {
                list = [];
                map[deviceId] = list;
            }

            list.Add(tag);
        }

        return map;
    }

    /// <summary>新建标签。名称重复时给出可读的错误（而不是把 UNIQUE 约束报错甩给用户）。</summary>
    public async Task<TagRow> CreateTagAsync(string? name, string? color,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTagName(name);
        var safeColor = NormalizeTagColor(color);

        await using var connection = await OpenAsync(cancellationToken);
        if (await TagNameExistsAsync(connection, normalized, null, cancellationToken))
        {
            throw HubException.Validation($"已经有一个叫「{normalized}」的标签了，换个名字吧。");
        }

        var tag = new TagRow
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = normalized,
            Color = safeColor,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tags (id, name, color, created_at) VALUES ($id, $name, $color, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", tag.Id);
        command.Parameters.AddWithValue("$name", tag.Name);
        command.Parameters.AddWithValue("$color", tag.Color);
        command.Parameters.AddWithValue("$createdAt", Ts(tag.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return tag;
    }

    /// <summary>修改标签。不存在返回 false，重名抛校验错误。</summary>
    public async Task<bool> UpdateTagAsync(string id, string? name, string? color,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTagName(name);
        var safeColor = NormalizeTagColor(color);

        await using var connection = await OpenAsync(cancellationToken);
        if (await TagNameExistsAsync(connection, normalized, id, cancellationToken))
        {
            throw HubException.Validation($"已经有一个叫「{normalized}」的标签了，换个名字吧。");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tags SET name = $name, color = $color WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", normalized);
        command.Parameters.AddWithValue("$color", safeColor);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// 删除标签，同时清掉它的设备关联。
    /// <para>关联必须一起删：留着孤儿行会让「按标签选设备」选出已经不存在标签的设备集合。</para>
    /// </summary>
    public async Task<bool> DeleteTagAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var links = connection.CreateCommand())
        {
            links.Transaction = (SqliteTransaction)transaction;
            links.CommandText = "DELETE FROM device_tags WHERE tag_id = $id;";
            links.Parameters.AddWithValue("$id", id);
            await links.ExecuteNonQueryAsync(cancellationToken);
        }

        int removed;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM tags WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            removed = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return removed > 0;
    }

    /// <summary>
    /// 整体替换某台设备的标签集合。
    /// <para>用「先清后插」而不是增删对比：中间态不会被人看到（同一事务里），
    /// 而且前端传来的是最终勾选结果，语义上本来就不是「加一个 / 减一个」。</para>
    /// </summary>
    /// <returns>设备不存在时返回 false。</returns>
    public async Task<bool> SetDeviceTagsAsync(string deviceId, IReadOnlyList<string> tagIds,
        CancellationToken cancellationToken = default)
    {
        var wanted = tagIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (wanted.Count > MaxTagsPerDevice)
        {
            throw HubException.Validation($"一台设备最多挂 {MaxTagsPerDevice} 个标签。");
        }

        await using var connection = await OpenAsync(cancellationToken);

        // 设备存不存在由这里判定：不存在的设备不该悄悄留下几条关联行。
        if (await GetDeviceAsync(deviceId, cancellationToken) is null)
        {
            return false;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "DELETE FROM device_tags WHERE device_id = $id;";
            clear.Parameters.AddWithValue("$id", deviceId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        if (wanted.Count > 0)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;

            // 只挂真实存在的标签：前端可能持有已删除标签的旧 ID（另一个窗口刚删掉）。
            var names = new string[wanted.Count];
            for (var i = 0; i < wanted.Count; i++)
            {
                names[i] = $"$t{i}";
                insert.Parameters.AddWithValue($"$t{i}", wanted[i]);
            }

            insert.CommandText = $"""
                INSERT INTO device_tags (device_id, tag_id)
                SELECT $deviceId, id FROM tags WHERE id IN ({string.Join(", ", names)});
                """;
            insert.Parameters.AddWithValue("$deviceId", deviceId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>某标签下的设备（含已撤销的，交给调用方决定怎么用）。</summary>
    public async Task<List<DeviceRow>> GetDevicesByTagAsync(string tagId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // 用子查询而不是 JOIN：DeviceColumns 是不带表前缀的列名清单，JOIN 之后 id 会歧义。
        command.CommandText = $"""
            SELECT {DeviceColumns} FROM devices
             WHERE id IN (SELECT device_id FROM device_tags WHERE tag_id = $tag)
             ORDER BY name;
            """;
        command.Parameters.AddWithValue("$tag", tagId);

        var result = new List<DeviceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadDevice(reader));
        }

        return result;
    }

    // ── 辅助 ──

    private static TagRow ReadTag(SqliteDataReader reader, bool hasCount) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Color = reader.GetString(2),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
        DeviceCount = hasCount ? reader.GetInt32(4) : 0,
    };

    /// <summary>名称规范化：去首尾空白 + 校验非空与长度。</summary>
    private static string NormalizeTagName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw HubException.Validation("标签名不能为空。");
        }

        if (trimmed.Length > MaxTagNameLength)
        {
            throw HubException.Validation($"标签名最长 {MaxTagNameLength} 个字符。");
        }

        return trimmed;
    }

    /// <summary>
    /// 颜色规范化：只认 <c>#rgb</c> / <c>#rrggbb</c>，其它一律当「用默认色」。
    /// <para>不做成报错：颜色不影响功能，为它挡住保存不值得。但**必须白名单**——
    /// 这个值会被写进 style，放任任意字符串就是 CSS 注入。</para>
    /// </summary>
    private static string NormalizeTagColor(string? color)
    {
        var value = (color ?? string.Empty).Trim();
        return TagColorPattern.IsMatch(value) ? value : string.Empty;
    }

    private static async Task<bool> TagNameExistsAsync(SqliteConnection connection, string name,
        string? excludeId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1) FROM tags
             WHERE name = $name COLLATE NOCASE AND ($excludeId IS NULL OR id <> $excludeId);
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$excludeId", (object?)excludeId ?? DBNull.Value);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null and not DBNull && Convert.ToInt32(value) > 0;
    }
}

/// <summary>标签行（<see cref="HubStore.GetTagsAsync"/> 会填 <see cref="DeviceCount"/>）。</summary>
public sealed class TagRow
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>关联设备数（仅列表查询会填）。</summary>
    public int DeviceCount { get; set; }
}
