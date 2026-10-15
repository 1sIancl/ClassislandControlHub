using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using Microsoft.Data.Sqlite;

namespace IslandHub.Server.Data;

/// <summary>
/// 邀请码（自助注册）与定时提醒的数据访问。
/// <para>提醒按 <c>user_id</c> 隔离：每个账号只能读写自己的提醒。</para>
/// </summary>
public sealed partial class HubStore
{
    // ────────────────────────────── 邀请码 ──────────────────────────────

    private const string RegisterCodeColumns =
        "code, code_hash, note, permissions, max_uses, used_count, expires_at, created_at";

    /// <summary>读取一行邀请码：<c>code</c> 列是密文，这里解密为可展示的明文（失败则置空并标记不可用）。</summary>
    private RegisterCodeRow ReadRegisterCode(SqliteDataReader reader)
    {
        var code = ReadSecret(GetString(reader, "code"), out var available);
        return new RegisterCodeRow
        {
            Code = code,
            CodeHash = GetString(reader, "code_hash"),
            CodeUnavailable = !available,
            Note = GetString(reader, "note"),
            Permissions = PermissionKeys.Normalize(
                HubJson.DeserializeOrDefault<List<string>>(GetString(reader, "permissions"), [])),
            MaxUses = GetInt32(reader, "max_uses"),
            UsedCount = GetInt32(reader, "used_count"),
            ExpiresAt = GetTimestamp(reader, "expires_at"),
            CreatedAt = GetTimestampOrNow(reader, "created_at"),
        };
    }

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

    /// <summary>按码查询邀请码（支持明文或 <c>ref:</c> 指纹引用；明文先规范化，再按 HMAC 指纹查找）。</summary>
    public async Task<RegisterCodeRow?> GetRegisterCodeAsync(string code,
        CancellationToken cancellationToken = default)
    {
        var (lookup, hash, plain) = ResolveSecretLookup(code);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {RegisterCodeColumns} FROM register_codes WHERE {lookup} LIMIT 1;";
        AddParameters(command, ("$hash", hash), ("$plain", plain));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRegisterCode(reader) : null;
    }

    /// <summary>创建邀请码（<c>code</c> 以密文落库，并写入 HMAC 指纹；传入的 <paramref name="row"/> 保持明文）。</summary>
    public async Task CreateRegisterCodeAsync(RegisterCodeRow row, CancellationToken cancellationToken = default)
    {
        var plain = NormalizeEnrollCode(row.Code);
        var hash = _secrets.ComputeLookupHash(plain);
        row.Code = plain;
        // 回填指纹：调用方（端点）用它生成 DTO 的指纹引用，创建响应里不能是空引用。
        row.CodeHash = hash;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO register_codes (code, code_hash, note, permissions, max_uses, used_count, expires_at, created_at)
            VALUES ($code, $hash, $note, $permissions, $maxUses, $usedCount, $expiresAt, $createdAt);
            """;
        AddParameters(command,
            ("$code", ProtectSecret(plain)),
            ("$hash", hash),
            ("$note", row.Note),
            ("$permissions", HubJson.Serialize(PermissionKeys.Normalize(row.Permissions))),
            ("$maxUses", row.MaxUses),
            ("$usedCount", row.UsedCount),
            ("$expiresAt", row.ExpiresAt is null ? null : Ts(row.ExpiresAt.Value)),
            ("$createdAt", Ts(row.CreatedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>删除邀请码（按指纹查找，兼容未迁移的明文老行；也接受 <c>ref:</c> 引用）。</summary>
    public async Task DeleteRegisterCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var (lookup, hash, plain) = ResolveSecretLookup(code);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM register_codes WHERE {lookup};";
        AddParameters(command, ("$hash", hash), ("$plain", plain));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 校验并消费一次邀请码。成功时返回该码（已用次数 +1），失败时返回原因。
    /// <para>校验与计数在同一事务内完成，避免并发注册把次数用超。</para>
    /// </summary>
    public async Task<(RegisterCodeRow? Code, string? Error)> ConsumeRegisterCodeAsync(string code,
        CancellationToken cancellationToken = default)
    {
        var (lookup, hash, plain) = ResolveSecretLookup(code);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        RegisterCodeRow? row = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText =
                $"SELECT {RegisterCodeColumns} FROM register_codes WHERE {lookup} LIMIT 1;";
            AddParameters(select, ("$hash", hash), ("$plain", plain));
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

        if (row.CodeUnavailable)
        {
            // 指纹匹配但密文解密失败：按不可用处理，绝不静默放行（#36）。
            return (null, "邀请码不可用：服务端加密密钥已更换，请让管理员重新生成。");
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
            update.CommandText =
                $"UPDATE register_codes SET used_count = used_count + 1 WHERE {lookup};";
            AddParameters(update, ("$hash", hash), ("$plain", plain));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        row.UsedCount++;
        return (row, null);
    }

    // ────────────────────────────── 定时提醒 ──────────────────────────────

    private const string ReminderColumns =
        "id, user_id, title, content, channel, action, trigger_kind, target_type, target_id, speak, enabled, "
        + "repeat_kind, repeat_interval, repeat_weekdays, repeat_day_of_month, fire_time, start_date, end_date, "
        + "next_fire_at, last_fired_at, fire_count, created_at, updated_at";

    private static ReminderRow ReadReminder(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        UserId = GetString(reader, "user_id"),
        Title = GetString(reader, "title"),
        Content = GetString(reader, "content"),
        Channel = GetString(reader, "channel"),
        Action = NormalizeAction(GetString(reader, "action")),
        TriggerKind = NormalizeTriggerKind(GetString(reader, "trigger_kind")),
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
            VALUES ($id, $userId, $title, $content, $channel, $action, $triggerKind, $targetType, $targetId, $speak,
                    $enabled, $repeatKind, $repeatInterval, $repeatWeekdays, $repeatDayOfMonth, $fireTime,
                    $startDate, $endDate, $nextFireAt, $lastFiredAt, $fireCount, $createdAt, $updatedAt);
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
                title = $title, content = $content, channel = $channel, action = $action,
                trigger_kind = $triggerKind,
                target_type = $targetType,
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

    /// <summary>
    /// 「领取」这一次触发：把 <c>next_fire_at</c> 推进到下一次（<paramref name="nextFireAt"/> 为空表示不再重复），
    /// 返回 <c>true</c> 表示这次归本次调用执行。
    /// <para><b>为什么要用 CAS 而不是先执行后推进</b>：原来的顺序是「先下发指令、再推进时间」，
    /// 万一推进那步失败（或同一份库被两个实例各跑一个调度器），<c>next_fire_at</c> 不变，
    /// 15 秒后同一分钟会**再下发一次**——通知重复一次还能忍，**关机重复下发就会把教室机反复关掉**。
    /// 这里用 <c>next_fire_at IS $expected</c>（SQLite 的 IS 是 null 安全的等值比较）做原子领取：
    /// 抢到的人才执行，抢不到的人直接跳过。</para>
    /// </summary>
    public async Task<bool> TryClaimReminderFireAsync(string reminderId, DateTimeOffset? expectedNextFireAt,
        DateTimeOffset? nextFireAt, DateTimeOffset firedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminders
            SET last_fired_at = $firedAt, next_fire_at = $nextFireAt, fire_count = fire_count + 1,
                enabled = $enabled, updated_at = $firedAt
            WHERE id = $id AND next_fire_at IS $expected;
            """;
        AddParameters(command,
            ("$firedAt", Ts(firedAt)),
            ("$nextFireAt", nextFireAt is null ? null : Ts(nextFireAt.Value)),
            ("$enabled", Bool(nextFireAt is not null)),
            ("$id", reminderId),
            ("$expected", expectedNextFireAt is null ? null : Ts(expectedNextFireAt.Value)));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// 把库里的触发方式收敛到合法取值：**保留具体的那个事件**，不认识的取值一律按「按时间」。
    /// <para>⚠️ 这里必须逐个保留取值，不能写成「是事件就归一到某一个事件」——我第一版就是这么写的，
    /// 结果 `sync.completed` 存进去读出来变成了 `device_online`：提醒照建、界面照显示，
    /// 但永远不会被它期待的事件唤醒（典型的静默失效）。</para>
    /// </summary>
    private static string NormalizeTriggerKind(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is not null && ReminderTriggers.All.Contains(normalized)
            ? normalized
            : ReminderTriggers.Time;
    }

    /// <summary>取某类**事件触发**的提醒（跨账号；供心跳里的「设备上线」用）。</summary>
    public async Task<List<ReminderRow>> GetEventRemindersAsync(string triggerKind,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ReminderColumns} FROM reminders
            WHERE enabled = 1 AND trigger_kind = $kind
            ORDER BY created_at;
            """;
        command.Parameters.AddWithValue("$kind", triggerKind);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ReminderRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadReminder(reader));
        }

        return result;
    }

    /// <summary>
    /// 「领取」一次**事件触发**（#12）：与 <see cref="TryClaimReminderFireAsync"/> 的区别是
    /// **不碰排程**——不推进 <c>next_fire_at</c>、也不改 <c>enabled</c>（那是时间触发的语义），
    /// 只落 <c>last_fired_at</c> / <c>fire_count</c>，并用冷却挡住「设备反复上下线」把提醒刷爆。
    /// <para>冷却判定与写入在同一条 SQL 里（<c>last_fired_at IS NULL OR &lt;= $cutoff</c>），
    /// 所以并发的两次上线只有一次能领到——与定时触发用 CAS 领取是同一个理由：
    /// 「定时关机」重复执行是关机事故，「上线提醒」重复执行是刷屏事故。</para>
    /// </summary>
    public async Task<bool> TryClaimEventFireAsync(string reminderId, DateTimeOffset firedAt,
        DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminders
            SET last_fired_at = $firedAt, fire_count = fire_count + 1, updated_at = $firedAt
            WHERE id = $id AND enabled = 1 AND (last_fired_at IS NULL OR last_fired_at <= $cutoff);
            """;
        AddParameters(command,
            ("$firedAt", Ts(firedAt)),
            ("$cutoff", Ts(cutoff)),
            ("$id", reminderId));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>写入一条触发记录。</summary>
    public async Task AddReminderFireAsync(ReminderFireRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reminder_fires (id, reminder_id, user_id, action, fired_at, affected, skipped, detail)
            VALUES ($id, $reminderId, $userId, $action, $firedAt, $affected, $skipped, $detail);
            """;
        AddParameters(command,
            ("$id", row.Id),
            ("$reminderId", row.ReminderId),
            ("$userId", row.UserId),
            ("$action", NormalizeAction(row.Action)),
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
                Action = NormalizeAction(GetString(reader, "action")),
                FiredAt = GetTimestampOrNow(reader, "fired_at"),
                Affected = GetInt32(reader, "affected"),
                Skipped = GetInt32(reader, "skipped"),
                Detail = GetString(reader, "detail"),
            });
        }

        return result;
    }

    private const string FireColumns =
        "id, reminder_id, user_id, action, fired_at, affected, skipped, detail";

    /// <summary>
    /// 动作取值的兜底：空串（老库刚补列的行）与认不出的值一律当「发通知」。
    /// <para>数据库里出现过坏值时，宁可退回默认动作，也不要让排程器拿到 null 后走空分支。</para>
    /// </summary>
    private static string NormalizeAction(string? action) =>
        ReminderActions.All.Contains(action ?? string.Empty) ? action! : ReminderActions.Notify;

    private static void AddReminderParameters(SqliteCommand command, ReminderRow row) =>
        AddParameters(command,
            ("$id", row.Id),
            ("$userId", row.UserId),
            ("$title", row.Title),
            ("$content", row.Content),
            ("$channel", row.Channel),
            ("$action", NormalizeAction(row.Action)),
            ("$triggerKind", NormalizeTriggerKind(row.TriggerKind)),
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
    /// <summary>邀请码明文：写入时加密落库，读取时由密文解密而来（#36）。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 指纹（确定性，用于精确查找）。</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>读取时解密失败（密钥文件被更换 / 丢失）→ 该行不可用，界面应提示重新生成。</summary>
    public bool CodeUnavailable { get; set; }

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

    /// <summary>到点执行的动作，见 <see cref="ReminderActions"/>。</summary>
    public string Action { get; set; } = ReminderActions.Notify;

    /// <summary>
    /// 触发方式，见 <see cref="ReminderTriggers"/>（#12）。
    /// <para>事件触发（device_online）时 <see cref="NextFireAt"/> 恒为空——这不是「停用」，
    /// 而是「不听排程」。所以任何「next_fire_at 为空即停用」的判断都要先看这一列。</para>
    /// </summary>
    public string TriggerKind { get; set; } = ReminderTriggers.Time;

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

    /// <summary>这次执行的动作，见 <see cref="ReminderActions"/>。</summary>
    public string Action { get; set; } = ReminderActions.Notify;

    public DateTimeOffset FiredAt { get; set; }
    public int Affected { get; set; }
    public int Skipped { get; set; }
    public string Detail { get; set; } = string.Empty;
}
