using System.Globalization;
using ControlHub.Protocol.Dtos;
using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 报表聚合（#63 在线率 / #65 指令执行 / #67 操作热点）。
///
/// 设计取舍：
///   - **能复用就不建表**：#65 直接聚合 <c>device_commands</c>、#67 直接聚合 <c>audit_logs</c>，
///     不引入统计表，也就不存在「统计表与事实不一致」的问题。
///   - **只有在线率必须自己采样**：<c>devices.last_seen_at</c> 是瞬时值，历史不留痕，
///     所以每分钟把「当时在线的设备」记一分钟（<c>device_online_samples</c>）。
///   - **分母用采样轮次，不用 1440 分钟**：服务不是全天运行，重启的那段时间不该算成设备掉线。
///   - **日期一律按服务器本地日**：横轴是给人看的「10 月 5 日」；库里存的是 UTC，所以聚合时统一
///     用 <c>datetime(x, 'localtime')</c> 转换，避免出现「晚上 8 点后的操作被算到第二天」。
/// </summary>
public sealed partial class HubStore
{
    /// <summary>报表最多回溯的天数（再长也没人看，还会拖慢聚合）。</summary>
    private const int MaxReportDays = 90;

    /// <summary>把「最近 N 天」换算成日期范围（含今天）。</summary>
    private static ReportRangeDto BuildRange(int days)
    {
        var span = Math.Clamp(days, 1, MaxReportDays);
        var today = DateTimeOffset.Now.Date;
        var from = today.AddDays(-(span - 1));

        return new ReportRangeDto
        {
            From = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            To = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Days = span,
        };
    }

    // ────────────────────────────── #63 在线率 ──────────────────────────────

    /// <summary>
    /// 记录一轮在线采样：给当时在线的设备当日 +1 分钟，并把「采样轮次」+1。
    /// <para>在线判定刻意与 <c>SyncService.IsOnline</c> 保持一致（未撤销 + 最近心跳在超时窗口内）——
    /// 两处判定一旦不同，报表就会和仪表盘的在线数对不上，那种「数字打架」最难解释。</para>
    /// </summary>
    /// <returns>本轮判定为在线的设备数。</returns>
    public async Task<int> RecordOnlineSampleAsync(TimeSpan onlineTimeout,
        CancellationToken cancellationToken = default)
    {
        var aliveSince = DateTimeOffset.UtcNow - onlineTimeout;
        var date = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int online;
        await using (var add = connection.CreateCommand())
        {
            add.Transaction = (SqliteTransaction)transaction;
            add.CommandText = """
                INSERT INTO device_online_samples (device_id, date, online_minutes)
                SELECT id, $date, 1 FROM devices
                 WHERE revoked = 0 AND last_seen_at IS NOT NULL AND last_seen_at >= $aliveSince
                ON CONFLICT(device_id, date) DO UPDATE SET online_minutes = online_minutes + 1;
                """;
            add.Parameters.AddWithValue("$date", date);
            // last_seen_at 与 Ts() 用同一格式（ISO 8601），所以字符串比较等价于时间比较。
            add.Parameters.AddWithValue("$aliveSince", Ts(aliveSince));
            online = await add.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var meta = connection.CreateCommand())
        {
            meta.Transaction = (SqliteTransaction)transaction;
            meta.CommandText = """
                INSERT INTO online_sample_meta (date, sample_count) VALUES ($date, 1)
                ON CONFLICT(date) DO UPDATE SET sample_count = sample_count + 1;
                """;
            meta.Parameters.AddWithValue("$date", date);
            await meta.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return online;
    }

    /// <summary>设备在线率报表。</summary>
    public async Task<OnlineReportDto> GetOnlineReportAsync(int days,
        CancellationToken cancellationToken = default)
    {
        var range = BuildRange(days);
        var report = new OnlineReportDto { Range = range };

        var devices = await GetDevicesAsync(cancellationToken);
        var live = devices.Where(d => !d.Revoked).ToList();
        report.DeviceCount = live.Count;

        await using var connection = await OpenAsync(cancellationToken);

        // 每日采样轮次（分母的一部分）
        var samplesByDate = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT date, sample_count FROM online_sample_meta
                 WHERE date BETWEEN $from AND $to;
                """;
            cmd.Parameters.AddWithValue("$from", range.From);
            cmd.Parameters.AddWithValue("$to", range.To);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                samplesByDate[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        report.SampleCount = (int)samplesByDate.Values.Sum();

        // 每日在线分钟数
        var minutesByDate = new Dictionary<string, long>(StringComparer.Ordinal);
        var minutesByDevice = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT date, device_id, SUM(online_minutes) FROM device_online_samples
                 WHERE date BETWEEN $from AND $to
                 GROUP BY date, device_id;
                """;
            cmd.Parameters.AddWithValue("$from", range.From);
            cmd.Parameters.AddWithValue("$to", range.To);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var date = reader.GetString(0);
                var deviceId = reader.GetString(1);
                var minutes = reader.GetInt64(2);

                minutesByDate[date] = minutesByDate.GetValueOrDefault(date) + minutes;
                minutesByDevice[deviceId] = minutesByDevice.GetValueOrDefault(deviceId) + minutes;
            }
        }

        // 整体：分母 = 采样轮次 × 设备数。设备数为 0 时不能除。
        var totalMinutes = minutesByDevice.Values.Sum();
        var totalCapacity = (long)report.SampleCount * Math.Max(1, report.DeviceCount);
        report.OverallRate = totalCapacity > 0
            ? Math.Min(1d, (double)totalMinutes / totalCapacity)
            : 0;

        foreach (var date in samplesByDate.Keys.Concat(minutesByDate.Keys).Distinct().OrderBy(d => d, StringComparer.Ordinal))
        {
            var sampleCount = samplesByDate.GetValueOrDefault(date);
            var capacity = sampleCount * Math.Max(1, report.DeviceCount);
            report.Daily.Add(new ReportBucketDto
            {
                Key = date,
                Label = date,
                Count = (int)minutesByDate.GetValueOrDefault(date),
                Extra = (int)capacity,
                Rate = capacity > 0 ? Math.Min(1d, (double)minutesByDate.GetValueOrDefault(date) / capacity) : 0,
            });
        }

        // 每台设备：**在线率低的排前面**——报表要盯的正是这些。
        var rows = new List<ReportBucketDto>();
        foreach (var device in live)
        {
            var minutes = minutesByDevice.GetValueOrDefault(device.Id);
            var capacity = Math.Max(1, report.SampleCount);
            rows.Add(new ReportBucketDto
            {
                Key = device.Id,
                Label = device.Name,
                Count = (int)minutes,
                Extra = capacity,
                Rate = report.SampleCount > 0 ? Math.Min(1d, (double)minutes / capacity) : 0,
            });
        }

        report.ByDevice = rows
            .OrderBy(r => r.Rate)
            .ThenBy(r => r.Label, StringComparer.Ordinal)
            .Take(50)
            .ToList();

        // 按分组（楼栋 / 楼层）：用设备**直接所属**的分组，不猜层级——猜错了比不分组更误导。
        var groups = await GetGroupsAsync(cancellationToken);
        var groupNames = groups.ToDictionary(g => g.Id, g => g.Name, StringComparer.Ordinal);
        report.ByGroup = live
            .GroupBy(d => string.IsNullOrWhiteSpace(d.GroupId) ? string.Empty : d.GroupId, StringComparer.Ordinal)
            .Select(g =>
            {
                var minutes = g.Sum(d => minutesByDevice.GetValueOrDefault(d.Id));
                var capacity = (long)Math.Max(1, report.SampleCount) * g.Count();
                return new ReportBucketDto
                {
                    Key = g.Key,
                    Label = groupNames.TryGetValue(g.Key, out var name) ? name : "未分组",
                    Count = (int)minutes,
                    Extra = (int)capacity,
                    Rate = capacity > 0 ? Math.Min(1d, (double)minutes / capacity) : 0,
                };
            })
            .OrderBy(r => r.Rate)
            .ToList();

        await using (var last = connection.CreateCommand())
        {
            last.CommandText = "SELECT MAX(date) FROM online_sample_meta;";
            var value = await last.ExecuteScalarAsync(cancellationToken);
            report.LastSampledDate = value as string;
        }

        return report;
    }

    // ────────────────────────────── #65 指令执行 ──────────────────────────────

    /// <summary>远程指令执行报表：成功率、类型分布、失败去向。</summary>
    public async Task<CommandReportDto> GetCommandReportAsync(int days,
        CancellationToken cancellationToken = default)
    {
        var range = BuildRange(days);
        var report = new CommandReportDto { Range = range };

        await using var connection = await OpenAsync(cancellationToken);

        // 状态分布（一次查完，避免多轮往返）
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT status, COUNT(1) FROM device_commands
                 WHERE substr(datetime(issued_at, 'localtime'), 1, 10) BETWEEN $from AND $to
                 GROUP BY status;
                """;
            cmd.Parameters.AddWithValue("$from", range.From);
            cmd.Parameters.AddWithValue("$to", range.To);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var count = reader.GetInt32(1);
                report.Total += count;
                switch (reader.GetString(0))
                {
                    case "done": report.Done = count; break;
                    case "failed": report.Failed = count; break;
                    case "expired": report.Expired = count; break;
                    // pending / dispatched 都算「还没有结果」。
                    default: report.Unfinished += count; break;
                }
            }
        }

        var settled = report.Done + report.Failed + report.Expired;
        report.SuccessRate = settled > 0 ? (double)report.Done / settled : 0;

        report.Daily = await ReadBucketsAsync(connection, """
            SELECT substr(datetime(issued_at, 'localtime'), 1, 10) AS d,
                   COUNT(1),
                   SUM(CASE WHEN status = 'failed' OR status = 'expired' THEN 1 ELSE 0 END)
              FROM device_commands
             WHERE substr(datetime(issued_at, 'localtime'), 1, 10) BETWEEN $from AND $to
             GROUP BY d ORDER BY d;
            """, range, cancellationToken);

        report.ByKind = await ReadBucketsAsync(connection, """
            SELECT kind, COUNT(1),
                   SUM(CASE WHEN status = 'failed' OR status = 'expired' THEN 1 ELSE 0 END)
              FROM device_commands
             WHERE substr(datetime(issued_at, 'localtime'), 1, 10) BETWEEN $from AND $to
             GROUP BY kind ORDER BY COUNT(1) DESC LIMIT 20;
            """, range, cancellationToken);

        // 失败最多的设备（要盯的就是反复失败的那几台）
        var byDevice = await ReadBucketsAsync(connection, """
            SELECT c.device_id, COALESCE(d.name, '(已删除设备)'), COUNT(1) AS failed
              FROM device_commands c LEFT JOIN devices d ON d.id = c.device_id
             WHERE substr(datetime(c.issued_at, 'localtime'), 1, 10) BETWEEN $from AND $to
                   AND c.status IN ('failed', 'expired')
             GROUP BY c.device_id ORDER BY failed DESC LIMIT 20;
            """, range, cancellationToken);

        // 把「该设备范围内总共发了多少条」补进 Extra，便于看出失败占比。
        foreach (var row in byDevice)
        {
            row.Extra = await ScalarIntAsync(connection, """
                SELECT COUNT(1) FROM device_commands
                 WHERE device_id = $id
                   AND substr(datetime(issued_at, 'localtime'), 1, 10) BETWEEN $from AND $to;
                """, [("$id", row.Key), ("$from", range.From), ("$to", range.To)], cancellationToken);
        }

        report.ByDevice = byDevice;

        report.Failures = await ReadBucketsAsync(connection, """
            SELECT CASE WHEN TRIM(COALESCE(output, '')) = '' THEN '(设备未回传原因)' ELSE substr(output, 1, 80) END,
                   COUNT(1), 0
              FROM device_commands
             WHERE substr(datetime(issued_at, 'localtime'), 1, 10) BETWEEN $from AND $to
                   AND status = 'failed'
             GROUP BY 1 ORDER BY COUNT(1) DESC LIMIT 15;
            """, range, cancellationToken);

        return report;
    }

    // ────────────────────────────── #67 操作热点 ──────────────────────────────

    /// <summary>操作热点分析：按日、按类别、按账号看审计里的操作量。</summary>
    public async Task<OperationReportDto> GetOperationReportAsync(int days,
        CancellationToken cancellationToken = default)
    {
        var range = BuildRange(days);
        var report = new OperationReportDto { Range = range };

        await using var connection = await OpenAsync(cancellationToken);

        report.Total = await ScalarIntAsync(connection, """
            SELECT COUNT(1) FROM audit_logs
             WHERE substr(datetime(ts, 'localtime'), 1, 10) BETWEEN $from AND $to;
            """, [("$from", range.From), ("$to", range.To)], cancellationToken);

        report.ActorCount = await ScalarIntAsync(connection, """
            SELECT COUNT(DISTINCT actor) FROM audit_logs
             WHERE substr(datetime(ts, 'localtime'), 1, 10) BETWEEN $from AND $to
                   AND actor <> '';
            """, [("$from", range.From), ("$to", range.To)], cancellationToken);

        report.Daily = await ReadBucketsAsync(connection, """
            SELECT substr(datetime(ts, 'localtime'), 1, 10) AS d, COUNT(1), 0
              FROM audit_logs
             WHERE substr(datetime(ts, 'localtime'), 1, 10) BETWEEN $from AND $to
             GROUP BY d ORDER BY d;
            """, range, cancellationToken);

        // 动作码的第一段即类别（device.command → device；admin.login → admin）
        report.ByCategory = await ReadBucketsAsync(connection, """
            SELECT substr(action, 1, CASE WHEN instr(action, '.') > 0
                                          THEN instr(action, '.') - 1 ELSE length(action) END),
                   COUNT(1), 0
              FROM audit_logs
             WHERE substr(datetime(ts, 'localtime'), 1, 10) BETWEEN $from AND $to
             GROUP BY 1 ORDER BY COUNT(1) DESC LIMIT 20;
            """, range, cancellationToken);

        report.ByActor = await ReadBucketsAsync(connection, """
            SELECT actor, COUNT(1), 0 FROM audit_logs
             WHERE substr(datetime(ts, 'localtime'), 1, 10) BETWEEN $from AND $to
             GROUP BY actor ORDER BY COUNT(1) DESC LIMIT 20;
            """, range, cancellationToken);

        report.TopActions = await ReadBucketsAsync(connection, """
            SELECT action, COUNT(1), 0 FROM audit_logs
             WHERE substr(datetime(ts, 'localtime'), 1, 10) BETWEEN $from AND $to
             GROUP BY action ORDER BY COUNT(1) DESC LIMIT 20;
            """, range, cancellationToken);

        report.TopTargets = await ReadBucketsAsync(connection, """
            SELECT target, COUNT(1), 0 FROM audit_logs
             WHERE substr(datetime(ts, 'localtime'), 1, 10) BETWEEN $from AND $to
                   AND target <> ''
             GROUP BY target ORDER BY COUNT(1) DESC LIMIT 20;
            """, range, cancellationToken);

        return report;
    }

    // ────────────────────────────── 小工具 ──────────────────────────────

    /// <summary>
    /// 跑「键 + 计数 + 补充计数」形态的聚合 SQL（三张报表都在用）。
    /// <para>约定：SELECT 的第 3 列是补充计数，没有就写 <c>0</c>。</para>
    /// </summary>
    private static async Task<List<ReportBucketDto>> ReadBucketsAsync(SqliteConnection connection,
        string sql, ReportRangeDto range, CancellationToken cancellationToken)
    {
        var list = new List<ReportBucketDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$from", range.From);
        command.Parameters.AddWithValue("$to", range.To);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var count = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var extra = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);

            list.Add(new ReportBucketDto
            {
                Key = key,
                // 空键要有可读的名字，否则界面上会出现一行空白。
                Label = string.IsNullOrWhiteSpace(key) ? "（未记录）" : key,
                Count = count,
                Extra = extra,
                Rate = extra > 0 ? Math.Min(1d, (double)count / extra) : 0,
            });
        }

        return list;
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql,
        (string Name, string Value)[] parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var value2 = await command.ExecuteScalarAsync(cancellationToken);
        return value2 is null or DBNull ? 0 : Convert.ToInt32(value2);
    }
}
