using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 临时换课（跨天 / 跨周换课）的数据访问。
/// <para>
/// 覆盖不写回档案：每次构建设备下发内容时才把「当天生效」的覆盖合成进去，
/// 因此到期后设备下一次同步就会自动拿到原课表，不需要人工还原。
/// </para>
/// </summary>
public sealed partial class HubStore
{
    private const string TimetableOverrideColumns =
        "id, profile_id, class_plan_id, slot_index, subject_id, start_date, end_date, weekdays, reason, created_by, created_at";

    private static TimetableOverrideRow ReadTimetableOverride(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        ProfileId = GetString(reader, "profile_id"),
        ClassPlanId = GetString(reader, "class_plan_id"),
        SlotIndex = GetInt32(reader, "slot_index"),
        SubjectId = GetNullableString(reader, "subject_id"),
        StartDate = GetString(reader, "start_date"),
        EndDate = GetNullableString(reader, "end_date"),
        Weekdays = GetString(reader, "weekdays"),
        Reason = GetString(reader, "reason"),
        CreatedBy = GetString(reader, "created_by"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
    };

    /// <summary>某个档案的全部临时换课（新的排在前面）。</summary>
    public async Task<List<TimetableOverrideRow>> GetTimetableOverridesAsync(string profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {TimetableOverrideColumns} FROM timetable_overrides WHERE profile_id = $profileId "
            + "ORDER BY start_date DESC, created_at DESC;";
        command.Parameters.AddWithValue("$profileId", profileId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TimetableOverrideRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadTimetableOverride(reader));
        }

        return result;
    }

    /// <summary>
    /// 查询某档案在指定日期生效的覆盖：日期范围命中后，再按「星期几」过滤
    /// （<paramref name="weekday"/> 取值 <c>0=周日 .. 6=周六</c>，与协议保持一致）。
    /// </summary>
    public async Task<List<TimetableOverrideRow>> GetActiveTimetableOverridesAsync(string profileId, string today,
        int weekday, CancellationToken cancellationToken = default)
    {
        var all = await GetTimetableOverridesAsync(profileId, cancellationToken);
        return all.Where(row =>
        {
            if (string.CompareOrdinal(row.StartDate, today) > 0)
            {
                return false;   // 还没开始
            }

            if (!string.IsNullOrEmpty(row.EndDate) && string.CompareOrdinal(row.EndDate, today) < 0)
            {
                return false;   // 已经结束
            }

            return MatchesWeekday(row.Weekdays, weekday);
        }).ToList();
    }

    /// <summary>判断「1,3,5」这样的星期集合是否命中某一天；留空表示每天。</summary>
    public static bool MatchesWeekday(string? weekdays, int weekday)
    {
        if (string.IsNullOrWhiteSpace(weekdays))
        {
            return true;
        }

        foreach (var part in weekdays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var value) && value == weekday)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>是否还存在生效中或未来的覆盖（跨天时据此决定要不要唤醒设备重新同步）。</summary>
    public async Task<bool> HasActiveOrUpcomingTimetableOverridesAsync(string today,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1) FROM timetable_overrides
             WHERE end_date IS NULL OR end_date >= $today;
            """;
        command.Parameters.AddWithValue("$today", today);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null and not DBNull && Convert.ToInt32(value) > 0;
    }

    /// <summary>按 ID 查询覆盖。</summary>
    public async Task<TimetableOverrideRow?> GetTimetableOverrideAsync(string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TimetableOverrideColumns} FROM timetable_overrides WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTimetableOverride(reader) : null;
    }

    /// <summary>新建临时换课。</summary>
    public async Task CreateTimetableOverrideAsync(TimetableOverrideRow row,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO timetable_overrides
                (id, profile_id, class_plan_id, slot_index, subject_id, start_date, end_date,
                 weekdays, reason, created_by, created_at)
            VALUES
                ($id, $profileId, $classPlanId, $slotIndex, $subjectId, $startDate, $endDate,
                 $weekdays, $reason, $createdBy, $createdAt);
            """;
        AddParameters(command,
            ("$id", row.Id),
            ("$profileId", row.ProfileId),
            ("$classPlanId", row.ClassPlanId),
            ("$slotIndex", row.SlotIndex),
            ("$subjectId", TextOrNull(row.SubjectId)),
            ("$startDate", row.StartDate),
            ("$endDate", TextOrNull(row.EndDate)),
            ("$weekdays", row.Weekdays),
            ("$reason", row.Reason),
            ("$createdBy", row.CreatedBy),
            ("$createdAt", Ts(row.CreatedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>删除临时换课（撤销换课）。</summary>
    public async Task<bool> DeleteTimetableOverrideAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM timetable_overrides WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>清空某个档案的全部临时换课（删除档案时调用）。</summary>
    public async Task DeleteTimetableOverridesOfProfileAsync(string profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM timetable_overrides WHERE profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", profileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>临时换课的一行。</summary>
public sealed class TimetableOverrideRow
{
    /// <summary>覆盖 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>作用的目标档案 ID。</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>目标课表 ID；为空表示该档案下的所有课表。</summary>
    public string ClassPlanId { get; set; } = string.Empty;

    /// <summary>目标节次序号（与课表节点 <c>index</c> 一致，从 0 开始）。</summary>
    public int SlotIndex { get; set; }

    /// <summary>临时科目 ID；为空表示该节改为空堂（自习）。</summary>
    public string? SubjectId { get; set; }

    /// <summary>开始日期（<c>yyyy-MM-dd</c>）。</summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>结束日期（含当天）；为空表示持续到手动撤销。</summary>
    public string? EndDate { get; set; }

    /// <summary>限定星期几（如 <c>1,3,5</c>，0=周日）；为空表示每天。</summary>
    public string Weekdays { get; set; } = string.Empty;

    /// <summary>换课原因（例如「周三数学调至上午」）。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>操作人。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
