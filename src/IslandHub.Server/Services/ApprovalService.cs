using IslandHub.Protocol;
using IslandHub.Protocol.Dtos;
using IslandHub.Server.Data;
using IslandHub.Server.Http;

namespace IslandHub.Server.Services;

/// <summary>一种「需要审批的操作」的执行者。</summary>
public interface IApprovalExecutor
{
    /// <summary>操作类型（见 <see cref="ApprovalKinds"/>）。</summary>
    string Kind { get; }

    /// <summary>把参数说成一句话，给审批人看（「从备份 xxx 恢复数据库」）。</summary>
    string Describe(string payloadJson);

    /// <summary>
    /// 真正执行。返回「是否成功 + 一句话结果」；**不要抛异常代替失败**——
    /// 失败要落成 <see cref="ApprovalStatuses.Failed"/> 记录，否则审批人以为已经生效了。
    /// </summary>
    Task<(bool Ok, string Result)> ExecuteAsync(string payloadJson, string approver, CancellationToken cancellationToken);
}

/// <summary>
/// 操作审批（路线图 #35）：危险操作由非超管发起时转成「待审批」，由超级管理员确认后执行。
///
/// <para>
/// 三条设计口径：
/// <list type="number">
/// <item><b>只拦非超管</b>：超管本来就是最终责任人，要求「自己审批自己」只会逼人绕过流程。</item>
/// <item><b>发起与执行分离</b>：申请落库（含参数），审批时按参数重放执行——不依赖发起人的会话还在。</item>
/// <item><b>执行失败也留记录</b>：状态落 <c>failed</c> 并写明原因，比「悄悄什么都没发生」诚实得多。</item>
/// </list>
/// </para>
/// </summary>
public sealed class ApprovalService(
    HubStore store,
    IEnumerable<IApprovalExecutor> executors,
    ILogger<ApprovalService> logger)
{
    private readonly Dictionary<string, IApprovalExecutor> _executors =
        executors.ToDictionary(e => e.Kind, StringComparer.Ordinal);

    /// <summary>这次操作是否需要走审批（非超级管理员需要）。</summary>
    public static bool NeedsApproval(SessionRow session) =>
        !string.Equals(session.Role, PermissionKeys.AdministratorRole, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否是超级管理员。</summary>
    public static bool IsSuperAdmin(SessionRow session) => !NeedsApproval(session);

    /// <summary>提交一条审批申请。</summary>
    public async Task<ApprovalRequestDto> SubmitAsync(string kind, object payload, string requester,
        CancellationToken cancellationToken)
    {
        if (!_executors.TryGetValue(kind, out var executor))
        {
            throw HubException.Validation($"不支持审批的操作类型：{kind}。");
        }

        var json = HubJson.Serialize(payload);
        var row = new ApprovalRow
        {
            Id = HubChecksum.NewId(),
            Kind = kind,
            Title = executor.Describe(json),
            Payload = json,
            Status = ApprovalStatuses.Pending,
            RequestedBy = requester,
            RequestedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateApprovalAsync(row, cancellationToken);
        logger.LogInformation("{Requester} 提交了「{Title}」的审批申请（{Id}）。", requester, row.Title, row.Id);
        return ToDto(row);
    }

    /// <summary>通过并执行。</summary>
    public async Task<ApprovalRequestDto> ApproveAsync(string id, string approver, string? comment, string? ip,
        CancellationToken cancellationToken)
    {
        var row = await RequirePendingAsync(id, cancellationToken);
        if (!_executors.TryGetValue(row.Kind, out var executor))
        {
            throw HubException.Validation($"这条申请的操作类型已不受支持：{row.Kind}，请驳回后重新发起。");
        }

        // ⚠️ **先落定决定、再执行**：像「恢复备份」这类操作会**整库覆盖**——
        //    执行完再改状态时，这条申请所在的行已经被备份里的旧库替换掉了，更新 0 行命中，
        //    于是出现「明明恢复了却报『刚被别人处理过了』」（实测踩到，才改成这个顺序）。
        //    条件更新（只改 pending）保证两个人同时点「通过」时只有一个人能进入执行。
        if (!await store.DecideApprovalAsync(id, ApprovalStatuses.Approved, approver, comment, "执行中…",
                cancellationToken))
        {
            throw HubException.Validation("这条申请刚被别人处理过了，请刷新后再看。");
        }

        row.Status = ApprovalStatuses.Approved;
        row.DecidedBy = approver;
        row.DecidedAt = DateTimeOffset.UtcNow;
        row.Comment = comment;

        bool ok;
        string result;
        try
        {
            (ok, result) = await executor.ExecuteAsync(row.Payload, approver, cancellationToken);
        }
        catch (Exception ex)
        {
            // 执行器本该自己把失败转成返回值；这里兜一层，免得异常把「申请卡在执行中」——
            // 卡住的申请让人以为「点了没反应」，比明确失败更糟。
            logger.LogError(ex, "审批执行失败：{Id}（{Kind}）。", id, row.Kind);
            (ok, result) = (false, $"执行时抛异常：{ex.Message}");
        }

        var status = ok ? ApprovalStatuses.Approved : ApprovalStatuses.Failed;
        row.Status = status;
        row.Result = result;

        // 回写结果：尽力而为。整库被替换时这一行可能已经不见了，那是这类操作的**预期副作用**
        // （旧库被换回来，审批记录自然也不在了），不该报错——结果本身会进日志与审计。
        if (!await store.UpdateApprovalResultAsync(id, status, result, cancellationToken))
        {
            logger.LogInformation("审批 {Id} 的执行结果未能回写（多半是该操作整库覆盖了记录所在的行）。", id);
        }

        await store.AddAuditAsync(approver, "approval.approve", id,
            $"通过「{row.Title}」：{result}", ip, cancellationToken);
        return ToDto(row);
    }

    /// <summary>驳回（不执行）。</summary>
    public async Task<ApprovalRequestDto> RejectAsync(string id, string approver, string? comment, string? ip,
        CancellationToken cancellationToken)
    {
        var row = await RequirePendingAsync(id, cancellationToken);
        row.Status = ApprovalStatuses.Rejected;
        row.DecidedBy = approver;
        row.DecidedAt = DateTimeOffset.UtcNow;
        row.Comment = comment;
        row.Result = "已驳回，未执行。";

        if (!await store.DecideApprovalAsync(id, ApprovalStatuses.Rejected, approver, comment, row.Result,
                cancellationToken))
        {
            throw HubException.Validation("这条申请刚被别人处理过了，请刷新后再看。");
        }

        await store.AddAuditAsync(approver, "approval.reject", id,
            $"驳回「{row.Title}」{(string.IsNullOrWhiteSpace(comment) ? "。" : $"：{comment}")}",
            ip, cancellationToken);
        return ToDto(row);
    }

    /// <summary>列出审批（超管看全部，其他人只看自己发起的）。</summary>
    public async Task<List<ApprovalRequestDto>> ListAsync(SessionRow session, string? status,
        CancellationToken cancellationToken)
    {
        var requestedBy = IsSuperAdmin(session) ? null : session.Username;
        var rows = await store.GetApprovalsAsync(status, requestedBy, cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    private async Task<ApprovalRow> RequirePendingAsync(string id, CancellationToken cancellationToken)
    {
        var row = await store.GetApprovalAsync(id, cancellationToken)
                  ?? throw HubException.NotFound("审批申请不存在。");

        if (!string.Equals(row.Status, ApprovalStatuses.Pending, StringComparison.Ordinal))
        {
            throw HubException.Validation($"这条申请已经是「{ApprovalStatuses.Describe(row.Status)}」，不能再处理。");
        }

        return row;
    }

    private static ApprovalRequestDto ToDto(ApprovalRow row) => new()
    {
        Id = row.Id,
        Kind = row.Kind,
        Title = row.Title,
        Payload = row.Payload,
        Status = row.Status,
        RequestedBy = row.RequestedBy,
        RequestedAt = row.RequestedAt,
        DecidedBy = row.DecidedBy,
        DecidedAt = row.DecidedAt,
        Comment = row.Comment,
        Result = row.Result,
    };
}

/// <summary>「从备份恢复数据库」的审批参数。</summary>
public sealed class BackupRestoreApprovalPayload
{
    /// <summary>备份 ID。</summary>
    public string BackupId { get; set; } = string.Empty;
}

/// <summary>「回滚程序版本」的审批参数。</summary>
public sealed class RollbackApprovalPayload
{
    /// <summary>程序文件快照 ID。</summary>
    public string SnapshotId { get; set; } = string.Empty;
}

/// <summary>
/// 「把程序文件回滚到某个快照」的执行者（#89）。
/// <para>
/// 和「恢复备份」同一档危险程度：它会**换掉整个程序目录**。区别是数据目录不动，
/// 所以它属于「版本退回去」，不是「数据退回去」。
/// </para>
/// </summary>
public sealed class UpdateRollbackExecutor(RollbackService rollback, HubStore store) : IApprovalExecutor
{
    /// <inheritdoc />
    public string Kind => ApprovalKinds.UpdateRollback;

    /// <inheritdoc />
    public string Describe(string payloadJson)
    {
        var payload = HubJson.DeserializeOrDefault(payloadJson, new RollbackApprovalPayload());
        return $"把程序文件回滚到快照「{payload.SnapshotId}」（换掉整个程序目录，数据目录不动，需重启生效）";
    }

    /// <inheritdoc />
    public async Task<(bool Ok, string Result)> ExecuteAsync(string payloadJson, string approver,
        CancellationToken cancellationToken)
    {
        var payload = HubJson.DeserializeOrDefault(payloadJson, new RollbackApprovalPayload());
        if (string.IsNullOrWhiteSpace(payload.SnapshotId))
        {
            return (false, "申请里没有快照 ID。");
        }

        var result = rollback.Restore(payload.SnapshotId);
        await store.AddAuditAsync(approver, "update.rollback", payload.SnapshotId,
            $"经审批{result.Message}", "approval", cancellationToken);

        return (result.Ok, result.Message);
    }
}

/// <summary>
/// 「从备份恢复数据库」的执行者。
/// <para>
/// 这是接入审批流的第一个操作，也是**最该有审批**的那个：它会整库覆盖，
/// 拿错备份等于把学校一学期的配置换掉。
/// </para>
/// </summary>
public sealed class BackupRestoreExecutor(BackupService backups, HubStore store) : IApprovalExecutor
{
    /// <inheritdoc />
    public string Kind => ApprovalKinds.BackupRestore;

    /// <inheritdoc />
    public string Describe(string payloadJson)
    {
        var payload = HubJson.DeserializeOrDefault(payloadJson, new BackupRestoreApprovalPayload());
        return $"从备份「{payload.BackupId}」恢复数据库（整库覆盖，需重启服务生效）";
    }

    /// <inheritdoc />
    public async Task<(bool Ok, string Result)> ExecuteAsync(string payloadJson, string approver,
        CancellationToken cancellationToken)
    {
        var payload = HubJson.DeserializeOrDefault(payloadJson, new BackupRestoreApprovalPayload());
        if (string.IsNullOrWhiteSpace(payload.BackupId))
        {
            return (false, "申请里没有备份 ID。");
        }

        var ok = backups.RestoreBackup(payload.BackupId);
        await store.AddAuditAsync(approver, "backup.restore", payload.BackupId,
            $"经审批从备份恢复数据库（{(ok ? "成功" : "失败")}），需重启服务生效。",
            "approval", cancellationToken);

        return (ok, ok ? "已恢复备份，重启服务后生效。" : "恢复失败（备份文件可能已损坏或被删除）。");
    }
}
