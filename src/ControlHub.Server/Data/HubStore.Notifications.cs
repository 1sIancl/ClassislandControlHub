using System.Globalization;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 通知回执的读写（#7）。
///
/// <para>「通知已送达」这件事分两层，不能混：</para>
/// <list type="number">
///   <item><c>device_commands.status</c> = <c>done</c> —— 插件**执行了**展示调用。
///       这是「我们这边尽力了」，不代表教室那边真的亮过。</item>
///   <item><c>notification_receipts</c> —— 教室端**回报**了展示开始 / 结束。
///       到达率只能用这一层算。</item>
/// </list>
///
/// <para>之所以要区分：指令 done 是同步返回的，插件调完 API 就报；而通知可能因为
/// 前台被考试软件占满、ClassIsland 被最小化到托盘、通知队列积压而根本没显示出来。
/// 用 done 当到达率会得到一个「永远是 100%」的假指标。</para>
/// </summary>
public sealed partial class HubStore
{
    /// <summary>合法的回执阶段。未知阶段一律拒绝——写进库的自由文本以后没法统计。</summary>
    private static readonly HashSet<string> ReceiptStages = new(StringComparer.Ordinal) { "shown", "closed" };

    /// <summary>
    /// 解析库里存的时间。
    /// <para>用 <see cref="DateTimeStyles.RoundtripKind"/>：写入时用的是 "O" 格式（<see cref="Ts"/>），
    /// 它会带上 <c>Z</c> 或偏移量；丢掉 Kind 会让 UTC 时间被当成本地时间，报表按日聚合时差一天。</para>
    /// </summary>
    private static DateTimeOffset ParseTs(string value) => DateTimeOffset.Parse(
        value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// 记录一条通知回执，返回是否是新记录（重复上报返回 <c>false</c>）。
    /// <para>用 <c>INSERT OR IGNORE</c> 而不是先查后插：网络重试是常态，
    /// 而「同一设备同一阶段报两次」不该把计数刷成 2。</para>
    /// </summary>
    /// <param name="deviceId">上报设备（必须是指令的目标设备，防止串号）。</param>
    /// <param name="commandId">通知对应的指令 ID。</param>
    /// <param name="stage"><c>shown</c> 或 <c>closed</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<bool> RecordNotificationReceiptAsync(string deviceId, string commandId, string stage,
        CancellationToken cancellationToken = default)
    {
        if (!ReceiptStages.Contains(stage))
        {
            throw new ArgumentException($"未知的回执阶段：{stage}", nameof(stage));
        }

        await using var connection = await OpenAsync(cancellationToken);

        // 先确认这条指令确实是发给这台设备的通知：否则任意已认证设备可以伪造回执，
        // 把别的设备/别的类型的指令刷出「已到达」。
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = """
                SELECT COUNT(1) FROM device_commands
                 WHERE id = $id AND device_id = $deviceId AND kind = $kind;
                """;
            AddParameters(check,
                ("$id", commandId),
                ("$deviceId", deviceId),
                ("$kind", RemoteCommandKinds.Notify));
            var exists = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (exists == 0)
            {
                return false;
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO notification_receipts (command_id, device_id, stage, at)
            VALUES ($id, $deviceId, $stage, $at);
            """;
        AddParameters(command,
            ("$id", commandId),
            ("$deviceId", deviceId),
            ("$stage", stage),
            ("$at", Ts(DateTimeOffset.UtcNow)));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// 单条通知的回执汇总：下发给谁、谁展示了、谁还没回。
    /// </summary>
    public async Task<NotificationReceiptSummaryDto?> GetNotificationReceiptAsync(string commandId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        NotificationReceiptSummaryDto? summary = null;
        var targets = new List<(string Id, string Name)>();

        await using (var command = connection.CreateCommand())
        {
            // 标题从 payload 里取（通知的标题存在 JSON 里），取不到就留空。
            command.CommandText = """
                SELECT c.issued_at, c.payload, c.device_id, d.name
                  FROM device_commands c
                  LEFT JOIN devices d ON d.id = c.device_id
                 WHERE c.id = $id AND c.kind = $kind;
                """;
            AddParameters(command, ("$id", commandId), ("$kind", RemoteCommandKinds.Notify));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var issuedAt = ParseTs(reader.GetString(0));
                var payload = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var deviceId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var deviceName = reader.IsDBNull(3) ? deviceId : reader.GetString(3);

                summary ??= new NotificationReceiptSummaryDto
                {
                    CommandId = commandId,
                    Title = ExtractNotifyTitle(payload),
                    IssuedAt = issuedAt,
                };
                targets.Add((deviceId, deviceName));
            }
        }

        if (summary is null)
        {
            return null;
        }

        summary.TargetCount = targets.Count;

        var shown = new HashSet<string>(StringComparer.Ordinal);
        var closed = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT device_id, stage FROM notification_receipts WHERE command_id = $id;
                """;
            AddParameters(command, ("$id", commandId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var deviceId = reader.GetString(0);
                var stage = reader.GetString(1);
                if (stage == "shown")
                {
                    shown.Add(deviceId);
                }
                else if (stage == "closed")
                {
                    closed.Add(deviceId);
                }
            }
        }

        summary.ShownCount = shown.Count;
        summary.ClosedCount = closed.Count;
        // 分母是目标数而不是「回报过的设备数」：否则没回报的那几台会被从分母里悄悄去掉，
        // 到达率永远看着很漂亮。
        summary.ArrivalRate = summary.TargetCount == 0
            ? 0
            : (double)summary.ShownCount / summary.TargetCount;
        summary.PendingDevices = targets
            .Where((t) => !shown.Contains(t.Id))
            .Select((t) => t.Name)
            .ToList();

        return summary;
    }

    /// <summary>
    /// 通知到达率总览（#66 的底座）：按日 + 逐条明细。
    /// </summary>
    public async Task<NotificationArrivalReportDto> GetNotificationArrivalReportAsync(int days,
        int limit = 50, CancellationToken cancellationToken = default)
    {
        var range = BuildRange(days);
        var report = new NotificationArrivalReportDto { Range = range };

        await using var connection = await OpenAsync(cancellationToken);

        // 逐条通知的骨架：范围内所有通知类指令，按时间倒序。
        var rows = new List<(string Id, string Title, DateTimeOffset At, List<(string Id, string Name)> Targets)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.id, c.payload, c.issued_at, c.device_id, d.name
                  FROM device_commands c
                  LEFT JOIN devices d ON d.id = c.device_id
                 WHERE c.kind = $kind
                   AND datetime(c.issued_at, 'localtime') >= $from
                 ORDER BY c.issued_at DESC;
                """;
            AddParameters(command,
                ("$kind", RemoteCommandKinds.Notify),
                ("$from", range.From));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                var title = ExtractNotifyTitle(reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
                var at = ParseTs(reader.GetString(2));
                var deviceId = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var deviceName = reader.IsDBNull(4) ? deviceId : reader.GetString(4);

                // 通知是**每台设备一条指令**，所以这里一行就是一条（设备，通知）对。
                rows.Add((id, title, at, new List<(string Id, string Name)> { (deviceId, deviceName) }));
            }
        }

        // 回执一次性读出来做映射，避免在循环里逐条查库。
        var receipts = new Dictionary<string, (HashSet<string> Shown, HashSet<string> Closed)>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT command_id, device_id, stage FROM notification_receipts
                 WHERE datetime(at, 'localtime') >= $from;
                """;
            AddParameters(command, ("$from", range.From));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var commandId = reader.GetString(0);
                var deviceId = reader.GetString(1);
                var stage = reader.GetString(2);
                if (!receipts.TryGetValue(commandId, out var entry))
                {
                    entry = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                    receipts[commandId] = entry;
                }

                (stage == "shown" ? entry.Shown : entry.Closed).Add(deviceId);
            }
        }

        var daily = new Dictionary<string, (int Targets, int Shown)>(StringComparer.Ordinal);
        foreach (var row in rows.Take(limit))
        {
            // 没有回执的通知也要出现在列表里（到达率 0）——「没人回报」本身就是最重要的信息，
            // 把它过滤掉会让「通知发出去没人看」这件事从报表里消失。
            // 注意默认值要写**元素名**：三元表达式的类型推断会丢掉元组元素名，
            // 少了 Shown/Closed 下面就用不了。
            var entry = receipts.TryGetValue(row.Id, out var found)
                ? found
                : (Shown: new HashSet<string>(StringComparer.Ordinal),
                   Closed: new HashSet<string>(StringComparer.Ordinal));
            var summary = new NotificationReceiptSummaryDto
            {
                CommandId = row.Id,
                Title = row.Title,
                IssuedAt = row.At,
                TargetCount = row.Targets.Count,
                ShownCount = entry.Shown.Count,
                ClosedCount = entry.Closed.Count,
                ArrivalRate = row.Targets.Count == 0 ? 0 : (double)entry.Shown.Count / row.Targets.Count,
                PendingDevices = row.Targets.Where((t) => !entry.Shown.Contains(t.Id)).Select((t) => t.Name).ToList(),
            };
            report.Notifications.Add(summary);

            report.TotalTargets += summary.TargetCount;
            report.TotalShown += summary.ShownCount;

            // 按日聚合：日期取**本地日**（与其它报表同一口径）。
            var day = row.At.ToLocalTime().ToString("yyyy-MM-dd");
            var bucket = daily.TryGetValue(day, out var b) ? b : (0, 0);
            daily[day] = (bucket.Item1 + summary.TargetCount, bucket.Item2 + summary.ShownCount);
        }

        report.TotalNotifications = report.Notifications.Count;
        report.OverallRate = report.TotalTargets == 0 ? 0 : (double)report.TotalShown / report.TotalTargets;
        report.Daily = daily
            .OrderBy((kv) => kv.Key, StringComparer.Ordinal)
            .Select((kv) => new ReportBucketDto
            {
                Key = kv.Key,
                Label = kv.Key,
                Count = kv.Value.Item1,
                // Extra 存「已展示数」：按日的桶里 Count 是目标数、Extra 是到达数，
                // 前端拿两者直接画「到达 / 目标」，不必再请求一次。
                Extra = kv.Value.Item2,
                Rate = kv.Value.Item1 == 0 ? 0 : (double)kv.Value.Item2 / kv.Value.Item1,
            })
            .ToList();

        return report;
    }

    /// <summary>
    /// 从通知指令的 payload 里取标题。
    /// <para>取不到不抛异常：标题只是列表显示用，报表不该因为一条脏数据整页失败。</para>
    /// </summary>
    private static string ExtractNotifyTitle(string payload)
    {
        try
        {
            var dto = HubJson.Deserialize<NotifyRequestDto>(payload);
            if (!string.IsNullOrWhiteSpace(dto?.Title))
            {
                return dto.Title;
            }

            return dto?.Message ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
