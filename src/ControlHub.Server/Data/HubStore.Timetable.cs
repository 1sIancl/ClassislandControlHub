using System.Globalization;
using System.Text.Json;
using ControlHub.Protocol;
using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 课表模板（#9）：存取「天 × 节次」的排课网格，供其它档案套用。
/// </summary>
public sealed partial class HubStore
{
    /// <summary>模板名长度上限。</summary>
    public const int MaxTemplateNameLength = 32;

    private const int MaxGridPeriods = 32;

    private static readonly JsonSerializerOptions GridJsonOptions = new()
    {
        // 网格键是 "0".."6" 这样的字符串，别让序列化器按属性名规则去改它。
        DictionaryKeyPolicy = null,
    };

    /// <summary>全部课表模板（新的在前）。</summary>
    public async Task<List<TimetableTemplateRow>> GetTimetableTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, grid_json, created_at, created_by
              FROM timetable_templates ORDER BY created_at DESC;
            """;

        var result = new List<TimetableTemplateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadTemplate(reader));
        }

        return result;
    }

    /// <summary>新建课表模板。重名给出可读错误。</summary>
    public async Task<TimetableTemplateRow> CreateTimetableTemplateAsync(string? name,
        Dictionary<string, List<string?>>? grid, int periodCount, string createdBy,
        CancellationToken cancellationToken = default)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            throw HubException.Validation("模板名称不能为空。");
        }

        if (normalized.Length > MaxTemplateNameLength)
        {
            throw HubException.Validation($"模板名称最长 {MaxTemplateNameLength} 个字符。");
        }

        var safeGrid = NormalizeGrid(grid);
        if (safeGrid.Count == 0)
        {
            throw HubException.Validation("这套课表还没有排任何课，没什么可存成模板的。");
        }

        await using var connection = await OpenAsync(cancellationToken);

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(1) FROM timetable_templates WHERE name = $name COLLATE NOCASE;";
            check.Parameters.AddWithValue("$name", normalized);
            var exists = await check.ExecuteScalarAsync(cancellationToken);
            if (exists is not null and not DBNull && Convert.ToInt32(exists) > 0)
            {
                throw HubException.Validation($"已经有一个叫「{normalized}」的模板了，换个名字吧。");
            }
        }

        var row = new TimetableTemplateRow
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = normalized,
            Grid = safeGrid,
            PeriodCount = Math.Clamp(periodCount, 0, MaxGridPeriods),
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO timetable_templates (id, name, grid_json, created_at, created_by)
            VALUES ($id, $name, $grid, $createdAt, $by);
            """;
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$name", row.Name);
        command.Parameters.AddWithValue("$grid", JsonSerializer.Serialize(row.Grid, GridJsonOptions));
        command.Parameters.AddWithValue("$createdAt", Ts(row.CreatedAt));
        command.Parameters.AddWithValue("$by", row.CreatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return row;
    }

    /// <summary>删除课表模板。</summary>
    public async Task<bool> DeleteTimetableTemplateAsync(string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM timetable_templates WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    // ── 辅助 ──

    private static TimetableTemplateRow ReadTemplate(SqliteDataReader reader)
    {
        var row = new TimetableTemplateRow
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            CreatedAt = GetTimestampOrNow(reader, "created_at"),
            CreatedBy = reader.GetString(4),
        };

        try
        {
            row.Grid = JsonSerializer.Deserialize<Dictionary<string, List<string?>>>(
                reader.GetString(2), GridJsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // 模板内容坏了不该让整个列表打不开：留个空网格，用户仍能删掉它重建。
            row.Grid = [];
        }

        row.PeriodCount = row.Grid.Count == 0 ? 0 : row.Grid.Values.Max(v => v.Count);
        return row;
    }

    /// <summary>
    /// 规范化网格：只保留 0–6 天、每天最多 32 节，科目名去空白并截断。
    /// <para>脏键（非 0–6）**静默丢弃**而不是报错：它不影响其余部分被正常套用，
    /// 而为一个坏键让整次保存失败，用户体验更差。</para>
    /// </summary>
    private static Dictionary<string, List<string?>> NormalizeGrid(
        Dictionary<string, List<string?>>? grid)
    {
        var result = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        if (grid is null)
        {
            return result;
        }

        foreach (var (key, value) in grid)
        {
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day)
                || day is < 0 or > 6)
            {
                continue;
            }

            var cells = new List<string?>();
            foreach (var cell in (value ?? []).Take(MaxGridPeriods))
            {
                var text = (cell ?? string.Empty).Trim();
                cells.Add(text.Length == 0 ? null : text[..Math.Min(text.Length, 32)]);
            }

            result[day.ToString(CultureInfo.InvariantCulture)] = cells;
        }

        return result;
    }
}

/// <summary>课表模板行。</summary>
public sealed class TimetableTemplateRow
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Dictionary<string, List<string?>> Grid { get; set; } = [];

    public int PeriodCount { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
