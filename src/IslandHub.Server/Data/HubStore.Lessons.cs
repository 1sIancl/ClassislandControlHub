using IslandHub.Protocol.Dtos;

namespace IslandHub.Server.Data;

/// <summary>
/// 课表实时状态（「快捷换课」模块的数据部分）。
/// <para>内容来自教室端心跳：一台设备只保留**最新一条**——管理员关心的是「现在这间教室在上什么」，
/// 历史进度没有查询场景（要看历史有审计日志与指令历史）。</para>
/// </summary>
public sealed partial class HubStore
{
    /// <summary>写入 / 覆盖一台设备的课表实时状态。</summary>
    public async Task UpsertLessonStateAsync(string deviceId, LessonStateDto lesson,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_lesson_states (
                device_id, state, lesson_date, class_plan_name, current_subject, next_subject,
                next_start_at, next_end_at, left_seconds, is_adjusted, rotation_weeks, rotation_week, reported_at)
            VALUES ($device, $state, $date, $plan, $current, $next, $start, $end, $left, $adjusted,
                    $weeks, $week, $now)
            ON CONFLICT(device_id) DO UPDATE SET
                state           = excluded.state,
                lesson_date     = excluded.lesson_date,
                class_plan_name = excluded.class_plan_name,
                current_subject = excluded.current_subject,
                next_subject    = excluded.next_subject,
                next_start_at   = excluded.next_start_at,
                next_end_at     = excluded.next_end_at,
                left_seconds    = excluded.left_seconds,
                is_adjusted     = excluded.is_adjusted,
                rotation_weeks  = excluded.rotation_weeks,
                rotation_week   = excluded.rotation_week,
                reported_at     = excluded.reported_at;
            """;
        AddParameters(command,
            ("$device", deviceId),
            ("$state", string.IsNullOrWhiteSpace(lesson.State) ? LessonLiveStates.None : lesson.State),
            ("$date", lesson.Date ?? string.Empty),
            ("$plan", TextOrNull(lesson.ClassPlanName)),
            ("$current", TextOrNull(lesson.CurrentSubject)),
            ("$next", TextOrNull(lesson.NextSubject)),
            ("$start", TextOrNull(lesson.NextStartAt)),
            ("$end", TextOrNull(lesson.NextEndAt)),
            ("$left", Math.Max(0, lesson.LeftSeconds)),
            ("$adjusted", lesson.IsAdjusted ? 1 : 0),
            ("$weeks", lesson.RotationWeeks >= 2 ? lesson.RotationWeeks : 1),
            ("$week", Math.Max(0, lesson.RotationWeek)),
            ("$now", Ts(DateTimeOffset.UtcNow)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>存下教室端拉回来的课表目录（管理员点「读取课表」后由命令回报带回）。</summary>
    public async Task SaveLessonCatalogAsync(string deviceId, string catalogJson,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_lesson_states (device_id, reported_at, catalog, catalog_at)
            VALUES ($device, $now, $catalog, $now)
            ON CONFLICT(device_id) DO UPDATE SET
                catalog    = excluded.catalog,
                catalog_at = excluded.catalog_at;
            """;
        AddParameters(command,
            ("$device", deviceId),
            ("$catalog", catalogJson),
            ("$now", Ts(DateTimeOffset.UtcNow)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>读取所有设备的课表状态（键为设备 ID）。</summary>
    public async Task<Dictionary<string, LessonStateRow>> GetLessonStatesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, LessonStateRow>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, state, lesson_date, class_plan_name, current_subject, next_subject,
                   next_start_at, next_end_at, left_seconds, is_adjusted, reported_at,
                   catalog, catalog_at, rotation_weeks, rotation_week
              FROM device_lesson_states;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new LessonStateRow
            {
                DeviceId = reader.GetString(0),
                State = reader.GetString(1),
                Date = reader.GetString(2),
                ClassPlanName = reader.IsDBNull(3) ? null : reader.GetString(3),
                CurrentSubject = reader.IsDBNull(4) ? null : reader.GetString(4),
                NextSubject = reader.IsDBNull(5) ? null : reader.GetString(5),
                NextStartAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                NextEndAt = reader.IsDBNull(7) ? null : reader.GetString(7),
                LeftSeconds = reader.GetInt32(8),
                IsAdjusted = reader.GetInt32(9) != 0,
                ReportedAt = ParseTs(reader.GetString(10)),
                Catalog = reader.IsDBNull(11) ? null : reader.GetString(11),
                CatalogAt = reader.IsDBNull(12) ? null : ParseTs(reader.GetString(12)),
                RotationWeeks = reader.GetInt32(13),
                RotationWeek = reader.GetInt32(14),
            };
            result[row.DeviceId] = row;
        }

        return result;
    }

    /// <summary>读取一台设备的课表目录原始 JSON（为空表示还没拉取过）。</summary>
    public async Task<string?> GetLessonCatalogAsync(string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT catalog FROM device_lesson_states WHERE device_id = $device;";
        AddParameters(command, ("$device", deviceId));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    /// <summary>删除设备时一并清掉它的课表状态（避免留下孤儿行）。</summary>
    public async Task DeleteLessonStateAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM device_lesson_states WHERE device_id = $device;";
        AddParameters(command, ("$device", deviceId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

}

/// <summary>设备课表状态行（数据库视角，服务端内部使用）。</summary>
public sealed class LessonStateRow
{
    /// <summary>设备 ID。</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>状态（见 <see cref="LessonLiveStates"/>）。</summary>
    public string State { get; init; } = LessonLiveStates.None;

    /// <summary>教室端认定的日期。</summary>
    public string Date { get; init; } = string.Empty;

    /// <summary>当前课表名。</summary>
    public string? ClassPlanName { get; init; }

    /// <summary>当前科目。</summary>
    public string? CurrentSubject { get; init; }

    /// <summary>下节课科目。</summary>
    public string? NextSubject { get; init; }

    /// <summary>下节课开始时间。</summary>
    public string? NextStartAt { get; init; }

    /// <summary>下节课结束时间。</summary>
    public string? NextEndAt { get; init; }

    /// <summary>距上课（或距下课）剩余秒数。</summary>
    public int LeftSeconds { get; init; }

    /// <summary>当天是否处于换课 / 调休状态。</summary>
    public bool IsAdjusted { get; init; }

    /// <summary>轮换周期（周）；1 = 不轮换。</summary>
    public int RotationWeeks { get; init; } = 1;

    /// <summary>当前处于轮换周期的第几周（1 起）；0 = 不轮换 / 未知。</summary>
    public int RotationWeek { get; init; }

    /// <summary>最近一次上报时间。</summary>
    public DateTimeOffset ReportedAt { get; init; }

    /// <summary>课表目录 JSON。</summary>
    public string? Catalog { get; init; }

    /// <summary>课表目录拉取时间。</summary>
    public DateTimeOffset? CatalogAt { get; init; }
}
