using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 邀请码（自助注册）与定时提醒的数据访问。
/// <para>提醒按 <c>user_id</c> 隔离：每个账号只能读写自己的提醒。</para>
/// </summary>
public sealed partial class HubStore
{
    // ────────────────────────────── 邀请码 ──────────────────────────────

    private const string RegisterCodeColumns =
        "code, note, permissions, max_uses, used_count, expires_at, created_at";

    private static RegisterCodeRow ReadRegisterCode(SqliteDataReader reader) => new()
    {
        Code = GetString(reader, "code"),
        Note = GetString(reader, "note"),
        Permissions = PermissionKeys.Normalize(
            HubJson.DeserializeOrDefault<List<string>>(GetString(reader, "permissions"), [])),
        MaxUses = GetInt32(reader, "max_uses"),
        UsedCount = GetInt32(reader, "used_count"),
        ExpiresAt = GetTimestamp(reader, "expires_at"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
    };

    /// <summary>查询全部邀请码（新创建的排在前面）。</summary>
    public async Task<List<RegisterCodeRow>> GetRegisterCodesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RegisterCodeColumns} FROM register_codes ORDER BY created_at DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RegisterCodeRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadRegisterCode(reader));
        }

        return result;
    }

    /// <summary>按码查询邀请码。</summary>
    public async Task<RegisterCodeRow?> GetRegisterCodeAsync(string code,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RegisterCodeColumns} FROM register_codes WHERE code = $code LIMIT 1;";
        command.Parameters.AddWithValue("$code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRegisterCode(reader) : null;
    }

    /// <summary>创建邀请码。</summary>
    public async Task CreateRegisterCodeAsync(RegisterCodeRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO register_codes (code, note, permissions, max_uses, used_count, expires_at, created_at)
            VALUES ($code, $note, $permissions, $maxUses, $usedCount, $expiresAt, $createdAt);
            """;
        AddParameters(command,
            ("$code", row.Code),
            ("$note", row.Note),
            ("$permissions", HubJson.Serialize(PermissionKeys.Normalize(row.Permissions))),
            ("$maxUses", row.MaxUses),
            ("$usedCount", row.UsedCount),
            ("$expiresAt", row.ExpiresAt is null ? null : Ts(row.ExpiresAt.Value)),
            ("$createdAt", Ts(row.CreatedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>删除邀请码。</summary>
    public async Task DeleteRegisterCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM register_codes WHERE code = $code;";
        command.Parameters.AddWithValue("$code", code);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 校验并消费一次邀请码。成功时返回该码（已用次数 +1），失败时返回原因。
    /// <para>校验与计数在同一事务内完成，避免并发注册把次数用超。</para>
    /// </summary>
    public async Task<(RegisterCodeRow? Code, string? Error)> ConsumeRegisterCodeAsync(string code,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        RegisterCodeRow? row = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText = $"SELECT {RegisterCodeColumns} FROM register_codes WHERE code = $code LIMIT 1;";
            select.Parameters.AddWithValue("$code", code);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                row = ReadRegisterCode(reader);
            }
        }

        if (row is null)
        {
            return (null, "邀请码不存在。");
        }

        if (row.ExpiresAt.HasValue && row.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            return (null, "邀请码已过期。");
        }

        if (row.MaxUses > 0 && row.UsedCount >= row.MaxUses)
        {
            return (null, "邀请码使用次数已用尽。");
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE register_codes SET used_count = used_count + 1 WHERE code = $code;";
            update.Parameters.AddWithValue("$code", code);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        row.UsedCount++;
        return (row, null);
    }

    // ────────────────────────────── 定时提醒 ──────────────────────────────

    private const string ReminderColumns =
        "id, user_id, title, content, channel, target_type, target_id, speak, enabled, repeat_kind, "
        + "repeat_interval, repeat_weekdays, repeat_day_of_month, fire_time, start_date, end_date, "
        + "next_fire_at, last_fired_at, fire_count, created_at, updated_at";

    private static ReminderRow ReadReminder(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        UserId = GetString(reader, "user_id"),
        Title = GetString(reader, "title"),
        Content = GetString(reader, "content"),
        Channel = GetString(reader, "channel"),
        TargetType = GetString(reader, "target_type"),
        TargetId = GetString(reader, "target_id"),
        Speak = GetBool(reader, "speak"),
        Enabled = GetBool(reader, "enabled"),
        RepeatKind = GetString(reader, "repeat_kind"),
        RepeatInterval = GetInt32(reader, "repeat_interval"),
        RepeatWeekdays = GetString(reader, "repeat_weekdays"),
        RepeatDayOfMonth = GetInt32(reader, "repeat_day_of_month"),
        FireTime = GetString(reader, "fire_time"),
        StartDate = GetString(reader, "start_date"),
        EndDate = GetNullableString(reader, "end_date"),
        NextFireAt = GetTimestamp(reader, "next_fire_at"),
        LastFiredAt = GetTimestamp(reader, "last_fired_at"),
        FireCount = GetInt32(reader, "fire_count"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
        UpdatedAt = GetTimestampOrNow(reader, "updated_at"),
    };

    /// <summary>查询某个账号的全部提醒（按下次触发时间排序）。</summary>
    public async Task<List<ReminderRow>> GetRemindersAsync(string userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ReminderColumns} FROM reminders WHERE user_id = $userId
            ORDER BY enabled DESC, next_fire_at IS NULL, next_fire_at, created_at;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ReminderRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadReminder(reader));
        }

        return result;
    }

    /// <summary>按 ID 查询提醒。</summary>
    public async Task<ReminderRow?> GetReminderAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ReminderColumns} FROM reminders WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadReminder(reader) : null;
    }

    /// <summary>创建提醒。</summary>
    public async Task CreateReminderAsync(ReminderRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO reminders ({ReminderColumns})
            VALUES ($id, $userId, $title, $content, $channel, $targetType, $targetId, $speak, $enabled,
                    $repeatKind, $repeatInterval, $repeatWeekdays, $repeatDayOfMonth, $fireTime, $startDate,
                    $endDate, $nextFireAt, $lastFiredAt, $fireCount, $createdAt, $updatedAt);
            """;
        AddReminderParameters(command, row);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>更新提醒（整体覆盖可编辑字段）。</summary>
    public async Task UpdateReminderAsync(ReminderRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminders SET
                title = $title, content = $content, channel = $channel, target_type = $targetType,
                target_id = $targetId, speak = $speak, enabled = $enabled, repeat_kind = $repeatKind,
                repeat_interval = $repeatInterval, repeat_weekdays = $repeatWeekdays,
                repeat_day_of_month = $repeatDayOfMonth, fire_time = $fireTime, start_date = $startDate,
                end_date = $endDate, next_fire_at = $nextFireAt, last_fired_at = $lastFiredAt,
                fire_count = $fireCount, updated_at = $updatedAt
            WHERE id = $id;
            """;
        AddReminderParameters(command, row);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>删除提醒及其触发记录。</summary>
    public async Task DeleteReminderAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var sql in new[]
                 {
                     "DELETE FROM reminder_fires WHERE reminder_id = $id;",
                     "DELETE FROM reminders WHERE id = $id;",
                 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>取出到点需要触发的提醒（跨账号，供调度器使用）。</summary>
    public async Task<List<ReminderRow>> GetDueRemindersAsync(DateTimeOffset nowUtc, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ReminderColumns} FROM reminders
            WHERE enabled = 1 AND next_fire_at IS NOT NULL AND next_fire_at <= $now
            ORDER BY next_fire_at LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", Ts(nowUtc));
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ReminderRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadReminder(reader));
        }

        return result;
    }

    /// <summary>记录一次触发并推进到下次触发时间（<paramref name="nextFireAt"/> 为空表示不再重复）。</summary>
    public async Task RecordReminderFiredAsync(string reminderId, DateTimeOffset firedAt,
        DateTimeOffset? nextFireAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminders
            SET last_fired_at = $firedAt, next_fire_at = $nextFireAt, fire_count = fire_count + 1,
                enabled = $enabled, updated_at = $firedAt
            WHERE id = $id;
            """;
        AddParameters(command,
            ("$firedAt", Ts(firedAt)),
            ("$nextFireAt", nextFireAt is null ? null : Ts(nextFireAt.Value)),
            ("$enabled", Bool(nextFireAt is not null)),
            ("$id", reminderId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>写入一条触发记录。</summary>
    public async Task AddReminderFireAsync(ReminderFireRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reminder_fires (id, reminder_id, user_id, fired_at, affected, skipped, detail)
            VALUES ($id, $reminderId, $userId, $firedAt, $affected, $skipped, $detail);
            """;
        AddParameters(command,
            ("$id", row.Id),
            ("$reminderId", row.ReminderId),
            ("$userId", row.UserId),
            ("$firedAt", Ts(row.FiredAt)),
            ("$affected", row.Affected),
            ("$skipped", row.Skipped),
            ("$detail", row.Detail));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>查询某个账号的触发历史（倒序）。</summary>
    public async Task<List<ReminderFireRow>> GetReminderFiresAsync(string userId, string? reminderId = null,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = reminderId is null
            ? $"SELECT {FireColumns} FROM reminder_fires WHERE user_id = $userId ORDER BY fired_at DESC LIMIT $limit;"
            : $"SELECT {FireColumns} FROM reminder_fires WHERE user_id = $userId AND reminder_id = $reminderId ORDER BY fired_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$userId", userId);
        if (reminderId is not null)
        {
            command.Parameters.AddWithValue("$reminderId", reminderId);
        }

        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ReminderFireRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ReminderFireRow
            {
                Id = GetString(reader, "id"),
                ReminderId = GetString(reader, "reminder_id"),
                UserId = GetString(reader, "user_id"),
                FiredAt = GetTimestampOrNow(reader, "fired_at"),
                Affected = GetInt32(reader, "affected"),
                Skipped = GetInt32(reader, "skipped"),
                Detail = GetString(reader, "detail"),
            });
        }

        return result;
    }

    private const string FireColumns =
        "id, reminder_id, user_id, fired_at, affected, skipped, detail";

    private static void AddReminderParameters(SqliteCommand command, ReminderRow row) =>
        AddParameters(command,
            ("$id", row.Id),
            ("$userId", row.UserId),
            ("$title", row.Title),
            ("$content", row.Content),
            ("$channel", row.Channel),
            ("$targetType", row.TargetType),
            ("$targetId", row.TargetId),
            ("$speak", Bool(row.Speak)),
            ("$enabled", Bool(row.Enabled)),
            ("$repeatKind", row.RepeatKind),
            ("$repeatInterval", row.RepeatInterval),
            ("$repeatWeekdays", row.RepeatWeekdays),
            ("$repeatDayOfMonth", row.RepeatDayOfMonth),
            ("$fireTime", row.FireTime),
            ("$startDate", row.StartDate),
            ("$endDate", TextOrNull(row.EndDate)),
            ("$nextFireAt", row.NextFireAt is null ? null : Ts(row.NextFireAt.Value)),
            ("$lastFiredAt", row.LastFiredAt is null ? null : Ts(row.LastFiredAt.Value)),
            ("$fireCount", row.FireCount),
            ("$createdAt", Ts(row.CreatedAt)),
            ("$updatedAt", Ts(row.UpdatedAt)));
}

/// <summary>邀请码记录。</summary>
public sealed class RegisterCodeRow
{
    public string Code { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    /// <summary>注册后授予的权限键集合。</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>可注册次数；0 表示不限。</summary>
    public int MaxUses { get; set; } = 1;

    /// <summary>已注册次数。</summary>
    public int UsedCount { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>定时提醒记录。</summary>
public sealed class ReminderRow
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Channel { get; set; } = ReminderChannels.Screen;
    public string TargetType { get; set; } = ReminderTargets.All;
    public string TargetId { get; set; } = string.Empty;
    public bool Speak { get; set; }
    public bool Enabled { get; set; } = true;
    public string RepeatKind { get; set; } = ReminderRepeats.Once;
    public int RepeatInterval { get; set; } = 1;

    /// <summary>每周重复的星期几，存成 <c>1,3,5</c> 这样的文本。</summary>
    public string RepeatWeekdays { get; set; } = string.Empty;

    public int RepeatDayOfMonth { get; set; }
    public string FireTime { get; set; } = "08:00";
    public string StartDate { get; set; } = string.Empty;
    public string? EndDate { get; set; }
    public DateTimeOffset? NextFireAt { get; set; }
    public DateTimeOffset? LastFiredAt { get; set; }
    public int FireCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>把 <see cref="RepeatWeekdays"/> 解析为整数集合。</summary>
    public List<int> WeekdayList() => RepeatWeekdays
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(v => int.TryParse(v, out var n) ? n : 0)
        .Where(n => n is >= 1 and <= 7)
        .Distinct()
        .OrderBy(n => n)
        .ToList();
}

/// <summary>提醒触发记录。</summary>
public sealed class ReminderFireRow
{
    public string Id { get; set; } = string.Empty;
    public string ReminderId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset FiredAt { get; set; }
    public int Affected { get; set; }
    public int Skipped { get; set; }
    public string Detail { get; set; } = string.Empty;
}
