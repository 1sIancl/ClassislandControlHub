using ControlHub.Protocol;
using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>设备相关的数据访问。</summary>
public sealed partial class HubStore
{
    private const string DeviceColumns = """
        id, token, name, machine_name, os_version, classisland_version, plugin_version,
        group_id, profile_id, state, applied_revision, push_epoch, applied_push_epoch,
        last_seen_at, last_sync_at, last_error, ip_address, revoked,
        current_class_plan_name, metrics, created_at
        """;

    private static DeviceRow ReadDevice(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        Token = GetString(reader, "token"),
        Name = GetString(reader, "name"),
        MachineName = GetString(reader, "machine_name"),
        OsVersion = GetString(reader, "os_version"),
        ClassIslandVersion = GetString(reader, "classisland_version"),
        PluginVersion = GetString(reader, "plugin_version"),
        GroupId = GetNullableString(reader, "group_id"),
        ProfileId = GetNullableString(reader, "profile_id"),
        State = GetString(reader, "state"),
        AppliedRevision = GetInt64(reader, "applied_revision"),
        PushEpoch = GetInt64(reader, "push_epoch"),
        AppliedPushEpoch = GetInt64(reader, "applied_push_epoch"),
        LastSeenAt = GetTimestamp(reader, "last_seen_at"),
        LastSyncAt = GetTimestamp(reader, "last_sync_at"),
        LastError = GetNullableString(reader, "last_error"),
        IpAddress = GetNullableString(reader, "ip_address"),
        Revoked = GetBool(reader, "revoked"),
        CurrentClassPlanName = GetNullableString(reader, "current_class_plan_name"),
        Metrics = GetString(reader, "metrics"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
    };

    /// <summary>按设备 ID 查询。</summary>
    public async Task<DeviceRow?> GetDeviceAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {DeviceColumns} FROM devices WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDevice(reader) : null;
    }

    /// <summary>按设备令牌查询。</summary>
    public async Task<DeviceRow?> GetDeviceByTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {DeviceColumns} FROM devices WHERE token = $token LIMIT 1;";
        command.Parameters.AddWithValue("$token", token);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDevice(reader) : null;
    }

    /// <summary>查询全部设备。</summary>
    public async Task<List<DeviceRow>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {DeviceColumns} FROM devices ORDER BY name, created_at;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DeviceRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadDevice(reader));
        }

        return result;
    }

    /// <summary>查询分组内的设备。</summary>
    public async Task<List<DeviceRow>> GetDevicesByGroupAsync(string groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {DeviceColumns} FROM devices WHERE group_id = $groupId ORDER BY name;";
        command.Parameters.AddWithValue("$groupId", groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DeviceRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadDevice(reader));
        }

        return result;
    }

    /// <summary>
    /// 注册（或重新注册）设备。以 <paramref name="deviceId"/> 为唯一键，
    /// 同一台设备重复注册时保留原有记录并刷新令牌与上报信息。
    /// </summary>
    public async Task<DeviceRow> UpsertDeviceAsync(string deviceId, string token, string name,
        string machineName, string osVersion, string classIslandVersion, string pluginVersion,
        string? ipAddress, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO devices (id, token, name, machine_name, os_version, classisland_version,
                                 plugin_version, state, applied_revision, ip_address, created_at)
            VALUES ($id, $token, $name, $machine, $os, $ci, $plugin, 'idle', 0, $ip, $now)
            ON CONFLICT(id) DO UPDATE SET
                token               = excluded.token,
                name                = excluded.name,
                machine_name        = excluded.machine_name,
                os_version          = excluded.os_version,
                classisland_version = excluded.classisland_version,
                plugin_version      = excluded.plugin_version,
                ip_address          = excluded.ip_address,
                revoked             = 0,
                state               = 'idle';
            """;
        AddParameters(command,
            ("$id", deviceId),
            ("$token", token),
            ("$name", name),
            ("$machine", machineName),
            ("$os", osVersion),
            ("$ci", classIslandVersion),
            ("$plugin", pluginVersion),
            ("$ip", TextOrNull(ipAddress)),
            ("$now", Ts(DateTimeOffset.UtcNow)));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetDeviceAsync(deviceId, cancellationToken)
               ?? throw new HubException(HubErrorCodes.Internal, "设备注册后读取失败。", 500);
    }

    /// <summary>更新心跳信息。</summary>
    public async Task UpdateHeartbeatAsync(string deviceId, string state, long appliedRevision,
        long appliedPushEpoch, string? currentClassPlanName, string? lastError, string metrics,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE devices SET
                state                   = $state,
                applied_revision        = $revision,
                applied_push_epoch      = $pushEpoch,
                last_seen_at            = $now,
                current_class_plan_name = $plan,
                last_error              = $error,
                metrics                 = $metrics
            WHERE id = $id;
            """;
        AddParameters(command,
            ("$state", state),
            ("$revision", appliedRevision),
            ("$pushEpoch", appliedPushEpoch),
            ("$now", Ts(DateTimeOffset.UtcNow)),
            ("$plan", TextOrNull(currentClassPlanName)),
            ("$error", TextOrNull(lastError)),
            ("$metrics", metrics),
            ("$id", deviceId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>记录一次成功的配置应用。</summary>
    public async Task UpdateAppliedAsync(string deviceId, long revision, long pushEpoch,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE devices SET
                applied_revision   = $revision,
                applied_push_epoch = $pushEpoch,
                last_sync_at       = $now,
                last_seen_at       = $now
            WHERE id = $id;
            """;
        AddParameters(command,
            ("$revision", revision),
            ("$pushEpoch", pushEpoch),
            ("$now", Ts(DateTimeOffset.UtcNow)),
            ("$id", deviceId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>读取设备的推送世代号。</summary>
    public async Task<long> GetPushEpochAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT push_epoch FROM devices WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", deviceId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    /// <summary>递增指定设备的推送世代号，返回受影响设备数。用于定向推送。</summary>
    public async Task<int> BumpPushEpochAsync(IEnumerable<string> deviceIds,
        CancellationToken cancellationToken = default)
    {
        var ids = deviceIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE devices SET push_epoch = push_epoch + 1 WHERE id = $id;";
        var parameter = command.Parameters.Add("$id", SqliteType.Text);

        var affected = 0;
        foreach (var id in ids)
        {
            parameter.Value = id;
            affected += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return affected;
    }

    /// <summary>递增全部设备的推送世代号，返回受影响设备数。</summary>
    public async Task<int> BumpPushEpochAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET push_epoch = push_epoch + 1 WHERE revoked = 0;";
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>重命名设备。</summary>
    public async Task RenameDeviceAsync(string deviceId, string name,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET name = $name WHERE id = $id;";
        AddParameters(command, ("$name", name), ("$id", deviceId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>调整设备所属分组。</summary>
    public async Task SetDeviceGroupAsync(string deviceId, string? groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET group_id = $groupId WHERE id = $id;";
        AddParameters(command, ("$groupId", TextOrNull(groupId)), ("$id", deviceId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>为设备单独指定配置档案（优先级高于分组默认档案）。</summary>
    public async Task SetDeviceProfileAsync(string deviceId, string? profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET profile_id = $profileId WHERE id = $id;";
        AddParameters(command, ("$profileId", TextOrNull(profileId)), ("$id", deviceId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>设置设备的吊销状态。</summary>
    public async Task SetDeviceRevokedAsync(string deviceId, bool revoked,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET revoked = $revoked WHERE id = $id;";
        AddParameters(command, ("$revoked", Bool(revoked)), ("$id", deviceId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>删除设备记录。</summary>
    public async Task DeleteDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM devices WHERE id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>清空指定设备的上报日志。</summary>
    public async Task ClearDeviceLogsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM client_logs WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>写入客户端日志。</summary>
    public async Task InsertClientLogsAsync(string deviceId, IEnumerable<(DateTimeOffset Ts, string Level, string Message)> entries,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO client_logs (device_id, ts, level, message)
            VALUES ($deviceId, $ts, $level, $message);
            """;
        var pDevice = command.Parameters.Add("$deviceId", SqliteType.Text);
        var pTs = command.Parameters.Add("$ts", SqliteType.Text);
        var pLevel = command.Parameters.Add("$level", SqliteType.Text);
        var pMessage = command.Parameters.Add("$message", SqliteType.Text);
        pDevice.Value = deviceId;

        foreach (var (ts, level, message) in entries)
        {
            pTs.Value = Ts(ts);
            pLevel.Value = level;
            pMessage.Value = message;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>查询某个设备的最近日志。</summary>
    public async Task<List<ClientLogRow>> GetClientLogsAsync(string deviceId, int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, device_id, ts, level, message FROM client_logs
            WHERE device_id = $id ORDER BY ts DESC LIMIT $limit;
            """;
        AddParameters(command, ("$id", deviceId), ("$limit", limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ClientLogRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ClientLogRow
            {
                Id = GetInt64(reader, "id"),
                DeviceId = GetString(reader, "device_id"),
                Timestamp = GetTimestampOrNow(reader, "ts"),
                Level = GetString(reader, "level"),
                Message = GetString(reader, "message"),
            });
        }

        return result;
    }
}
