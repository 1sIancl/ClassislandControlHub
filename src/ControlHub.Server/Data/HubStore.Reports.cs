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

    // ────────────────────────────── #64 配置同步 ──────────────────────────────

    /// <summary>
    /// 记录一次配置下发：写批次 + 目标设备快照（含**下发当时**给每台设备设定的推送世代号）。
    /// <para>快照必须在这里落：世代号在下发后马上就被递增了，事后再查拿到的是「下一批」的值，
    /// 于是永远算不出这一批到底有没有被跟上。世代号直接从 <c>devices</c> 现取即可——
    /// 它在 <c>BumpPushEpoch*</c> 之后正好是本批要求的那个值。</para>
    /// </summary>
    public async Task RecordSyncPushAsync(SyncPushRecord push, IReadOnlyList<string> targetDeviceIds,
        CancellationToken cancellationToken = default)
    {
        if (targetDeviceIds.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO sync_pushes
                       (id, revision, scope, target_count, created_at, created_by, message, force)
                VALUES ($id, $revision, $scope, $count, $at, $by, $message, $force);
                """;
            insert.Parameters.AddWithValue("$id", push.Id);
            insert.Parameters.AddWithValue("$revision", push.Revision);
            insert.Parameters.AddWithValue("$scope", push.Scope);
            insert.Parameters.AddWithValue("$count", targetDeviceIds.Count);
            insert.Parameters.AddWithValue("$at", Ts(push.CreatedAt));
            insert.Parameters.AddWithValue("$by", push.CreatedBy);
            insert.Parameters.AddWithValue("$message", push.Message);
            insert.Parameters.AddWithValue("$force", push.Force ? 1 : 0);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var targets = connection.CreateCommand())
        {
            targets.Transaction = (SqliteTransaction)transaction;

            // 参数化 IN：目标可能上百台，拼字面量既有注入风险也不好读。
            // 顺带一个好处：已删除的设备不会出现在快照里（IN 匹配不到），本来也不该统计。
            var names = new string[targetDeviceIds.Count];
            for (var i = 0; i < targetDeviceIds.Count; i++)
            {
                names[i] = $"$d{i}";
                targets.Parameters.AddWithValue($"$d{i}", targetDeviceIds[i]);
            }

            targets.CommandText = $"""
                INSERT INTO sync_push_targets (push_id, device_id, push_epoch)
                SELECT $id, id, push_epoch FROM devices WHERE id IN ({string.Join(", ", names)});
                """;
            targets.Parameters.AddWithValue("$id", push.Id);
            await targets.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>配置同步成功率报表：每批下发最终的覆盖情况。</summary>
    /// <param name="days">回溯天数。</param>
    /// <param name="onlineTimeout">在线判定窗口（与仪表盘 / 在线率共用同一口径）。</param>
    /// <param name="limit">最多返回多少批（新的在前）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SyncReportDto> GetSyncReportAsync(int days, TimeSpan onlineTimeout, int limit = 40,
        CancellationToken cancellationToken = default)
    {
        var range = BuildRange(days);
        var aliveSince = Ts(DateTimeOffset.UtcNow - onlineTimeout);
        var report = new SyncReportDto { Range = range };

        await using var connection = await OpenAsync(cancellationToken);

        report.Daily = await ReadBucketsAsync(connection, """
            SELECT substr(datetime(created_at, 'localtime'), 1, 10) AS d, COUNT(1), 0
              FROM sync_pushes
             WHERE substr(datetime(created_at, 'localtime'), 1, 10) BETWEEN $from AND $to
             GROUP BY d ORDER BY d;
            """, range, cancellationToken);

        // 一次拿到所有批次的覆盖情况。
        // 「跟上」= applied_push_epoch >= 本批给它的世代号（不是看 revision —— 定向推送不改 revision）。
        // 分母排除「下发后被停用 / 已删除」的设备：它们收不到是意料之中，不该算失败。
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT p.id, p.revision, p.scope, p.target_count, p.created_at, p.created_by,
                       p.message, p.force,
                       COUNT(t.device_id),
                       SUM(CASE WHEN d.id IS NULL OR d.revoked = 1 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN d.revoked = 0 AND d.applied_push_epoch >= t.push_epoch
                                THEN 1 ELSE 0 END),
                       SUM(CASE WHEN d.revoked = 0 AND d.applied_push_epoch < t.push_epoch
                                     AND d.last_seen_at IS NOT NULL AND d.last_seen_at >= $aliveSince
                                THEN 1 ELSE 0 END),
                       SUM(CASE WHEN d.revoked = 0 AND d.applied_push_epoch < t.push_epoch
                                     AND (d.last_seen_at IS NULL OR d.last_seen_at < $aliveSince)
                                THEN 1 ELSE 0 END),
                       AVG(CASE WHEN d.revoked = 0 AND d.applied_push_epoch >= t.push_epoch
                                THEN (julianday(d.last_sync_at) - julianday(p.created_at)) * 86400 END)
                  FROM sync_pushes p
                  LEFT JOIN sync_push_targets t ON t.push_id = p.id
                  LEFT JOIN devices d ON d.id = t.device_id
                 WHERE substr(datetime(p.created_at, 'localtime'), 1, 10) BETWEEN $from AND $to
                 GROUP BY p.id
                 ORDER BY p.created_at DESC
                 LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$from", range.From);
            cmd.Parameters.AddWithValue("$to", range.To);
            cmd.Parameters.AddWithValue("$aliveSince", aliveSince);
            cmd.Parameters.AddWithValue("$limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var synced = reader.GetInt32(10);
                var waiting = reader.GetInt32(11);
                var offline = reader.GetInt32(12);
                var denominator = synced + waiting + offline;

                report.Pushes.Add(new SyncPushReportDto
                {
                    Id = reader.GetString(0),
                    Revision = reader.GetInt64(1),
                    Scope = reader.GetString(2),
                    ScopeLabel = string.Empty, // 下面按批次数补
                    CreatedAt = GetTimestampOrNow(reader, "created_at"),
                    CreatedBy = reader.GetString(5),
                    Message = reader.GetString(6),
                    Force = reader.GetInt32(7) != 0,
                    TargetCount = reader.GetInt32(8),
                    Synced = synced,
                    Waiting = waiting,
                    Offline = offline,
                    Excluded = reader.GetInt32(9),
                    Countable = denominator,
                    SuccessRate = denominator > 0 ? (double)synced / denominator : 0,
                    AverageSeconds = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                });
            }
        }

        // 范围说明：把 scope 与目标数拼成一句人话（「全部设备（14）」）。
        foreach (var push in report.Pushes)
        {
            push.ScopeLabel = push.Scope switch
            {
                "all" => $"全部设备（{push.TargetCount}）",
                "group" => $"按分组（{push.TargetCount} 台）",
                "device" => $"指定设备（{push.TargetCount} 台）",
                "tag" => $"按标签（{push.TargetCount} 台）",
                _ => $"{push.Scope}（{push.TargetCount}）",
            };
        }

        report.PushCount = report.Pushes.Count;
        report.TotalTargets = report.Pushes.Sum(p => p.Synced + p.Waiting + p.Offline);
        report.TotalSynced = report.Pushes.Sum(p => p.Synced);
        report.OverallRate = report.TotalTargets > 0
            ? (double)report.TotalSynced / report.TotalTargets
            : 0;
        report.FullySyncedPushes = report.Pushes.Count(p => p.Waiting + p.Offline == 0);

        // 反复没跟上的设备：出问题时最该先看这几台。
        await using (var stuck = connection.CreateCommand())
        {
            stuck.CommandText = """
                SELECT t.device_id, COALESCE(d.name, '（已删除）'), COUNT(1) AS missed
                  FROM sync_push_targets t
                  JOIN sync_pushes p ON p.id = t.push_id
                  LEFT JOIN devices d ON d.id = t.device_id
                 WHERE substr(datetime(p.created_at, 'localtime'), 1, 10) BETWEEN $from AND $to
                       AND d.revoked = 0 AND d.applied_push_epoch < t.push_epoch
                 GROUP BY t.device_id
                 ORDER BY missed DESC
                 LIMIT 15;
                """;
            stuck.Parameters.AddWithValue("$from", range.From);
            stuck.Parameters.AddWithValue("$to", range.To);

            await using var reader = await stuck.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var missed = reader.GetInt32(2);
                report.StuckDevices.Add(new ReportBucketDto
                {
                    Key = reader.GetString(0),
                    Label = reader.GetString(1),
                    Count = missed,
                });
            }
        }

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

/// <summary>一次配置下发的元信息（用于落库，见 <see cref="HubStore.RecordSyncPushAsync"/>）。</summary>
/// <param name="Id">批次 ID。</param>
/// <param name="Revision">下发时的全局配置版本号。</param>
/// <param name="Scope">下发范围（all / group / device）。</param>
/// <param name="CreatedAt">下发时间。</param>
/// <param name="CreatedBy">发起人。</param>
/// <param name="Message">随下发的提示消息。</param>
/// <param name="Force">是否强制覆盖本地内容。</param>
public sealed record SyncPushRecord(
    string Id,
    long Revision,
    string Scope,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string Message,
    bool Force);
