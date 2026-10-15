using Microsoft.Data.Sqlite;

namespace IslandHub.Server.Data;

/// <summary>
/// 走班制教室的时段规则（`#25`）：第 N 节时，这台教室用哪个档案。
///
/// <para>存的是**绝对节次序号**（0 基，与课表节点序号一致），不是时间：
/// 作息时间表本身会变（夏令/冬令、临时调整），写死时间的话每次改作息都要重配一遍走班规则；
/// 存节次则跟着时间表走，作息改了规则仍然成立。</para>
/// </summary>
public partial class HubStore
{
    /// <summary>建表（幂等；由 <see cref="InitializeAsync"/> 调用）。</summary>
    internal async Task EnsureProfileSlotSchemaAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS device_profile_slots (
                device_id    TEXT NOT NULL,
                period_index INTEGER NOT NULL,
                profile_id   TEXT NOT NULL,
                PRIMARY KEY (device_id, period_index)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>取一台教室的时段规则（按节次升序）。</summary>
    public async Task<List<ProfileSlotRow>> GetDeviceProfileSlotsAsync(string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, period_index, profile_id
              FROM device_profile_slots WHERE device_id = $id ORDER BY period_index;
            """;
        command.Parameters.AddWithValue("$id", deviceId);
        return await ReadSlotsAsync(command, cancellationToken);
    }

    /// <summary>取所有教室的时段规则（按教室分组用）。</summary>
    public async Task<List<ProfileSlotRow>> GetAllProfileSlotsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, period_index, profile_id
              FROM device_profile_slots ORDER BY device_id, period_index;
            """;
        return await ReadSlotsAsync(command, cancellationToken);
    }

    /// <summary>整体替换一台教室的时段规则（传空列表即清空）。</summary>
    public async Task ReplaceDeviceProfileSlotsAsync(string deviceId,
        IReadOnlyList<ProfileSlotRow> slots, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "DELETE FROM device_profile_slots WHERE device_id = $id;";
            clear.Parameters.AddWithValue("$id", deviceId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var slot in slots)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO device_profile_slots (device_id, period_index, profile_id)
                VALUES ($device, $period, $profile);
                """;
            insert.Parameters.AddWithValue("$device", deviceId);
            insert.Parameters.AddWithValue("$period", slot.PeriodIndex);
            insert.Parameters.AddWithValue("$profile", slot.ProfileId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>教室被删除时，顺手清掉它的时段规则（否则规则会永远留在库里）。</summary>
    public async Task DeleteDeviceProfileSlotsAsync(string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM device_profile_slots WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<ProfileSlotRow>> ReadSlotsAsync(SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<ProfileSlotRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ProfileSlotRow
            {
                DeviceId = reader.GetString(0),
                PeriodIndex = reader.GetInt32(1),
                ProfileId = reader.GetString(2),
            });
        }

        return rows;
    }

    /// <summary>一条时段规则。</summary>
    public sealed class ProfileSlotRow
    {
        /// <summary>教室 ID。</summary>
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>节次序号（0 基）。</summary>
        public int PeriodIndex { get; set; }

        /// <summary>该节使用的档案 ID。</summary>
        public string ProfileId { get; set; } = string.Empty;
    }
}
