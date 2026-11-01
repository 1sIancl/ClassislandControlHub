using Microsoft.Data.Sqlite;

namespace IslandHub.Server.Data;

/// <summary>
/// 全局时间表库的数据访问（#136）。
///
/// <para>
/// 时间表原来是档案内容的一部分（<c>content.timeLayouts</c>），于是「同一份作息给三个年级用」
/// 只能在三个档案里各存一份，改一次要改三处、还容易改漏。现在提升为独立实体：
/// 档案用 <c>content.timeLayoutIds</c> 引用，课表用 <c>timeLayoutId</c> 指向它。
/// </para>
///
/// <para>
/// 为了不打断现有代码（下发、导入、导出、前端都读 <c>content.timeLayouts</c>），
/// 读取档案时会把引用的时间表**投影**回那个字段；写入时再把它们**提取**进库。
/// 也就是说库里是唯一来源，档案里的那份只是投影，不会出现两份真相。
/// </para>
/// </summary>
public sealed partial class HubStore
{
    private const string TimeLayoutColumns = "id, name, items, created_at, updated_at";

    private static TimeLayoutRow ReadTimeLayout(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        Name = GetString(reader, "name"),
        Items = GetString(reader, "items"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
        UpdatedAt = GetTimestampOrNow(reader, "updated_at"),
    };

    /// <summary>查询全部时间表（按名称排序）。</summary>
    public async Task<List<TimeLayoutRow>> GetTimeLayoutsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TimeLayoutColumns} FROM time_layouts ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var result = new List<TimeLayoutRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadTimeLayout(reader));
        }

        return result;
    }

    /// <summary>按 ID 查询单个时间表。</summary>
    public async Task<TimeLayoutRow?> GetTimeLayoutAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TimeLayoutColumns} FROM time_layouts WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTimeLayout(reader) : null;
    }

    /// <summary>新增或覆盖一张时间表（按 ID upsert）。</summary>
    public async Task UpsertTimeLayoutAsync(
        string id, string name, string itemsJson, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO time_layouts (id, name, items, created_at, updated_at)
            VALUES ($id, $name, $items, $now, $now)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                items = excluded.items,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$items", itemsJson);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>删除一张时间表；返回是否真的删掉了。</summary>
    public async Task<bool> DeleteTimeLayoutAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM time_layouts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// 批量写入时间表（迁移与保存档案时用）。
    /// <para>逐条 upsert 而不是先删后插：删除会让同时进行的下发读到空档期。</para>
    /// </summary>
    public async Task UpsertTimeLayoutsAsync(
        IEnumerable<TimeLayoutRow> layouts, CancellationToken cancellationToken = default)
    {
        foreach (var layout in layouts)
        {
            await UpsertTimeLayoutAsync(layout.Id, layout.Name, layout.Items, cancellationToken);
        }
    }

    /// <summary>
    /// 只更新档案内容（不递增版本号）。
    /// <para>
    /// 迁移专用：把内嵌时间表换成引用属于内部整理，内容对教室端是等价的，
    /// 递增 revision 会让所有设备显示「有更新」，白白触发一轮下发。
    /// </para>
    /// </summary>
    public async Task UpdateProfileContentAsync(
        string id, string content, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE profiles SET content = $content, updated_at = $now WHERE id = $id;";
        AddParameters(command,
            ("$content", content),
            ("$now", Ts(DateTimeOffset.UtcNow)),
            ("$id", id));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
