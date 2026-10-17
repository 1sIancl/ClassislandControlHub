namespace IslandHub.Protocol.Dtos;

/// <summary>
/// 一次「操作审批」（路线图 #35）。
///
/// <para>
/// 为什么要有：有些操作**一按下去就覆盖整站数据**（恢复备份、删设备、批量下发），
/// 这类操作不该由一个人既发起又确认。做法是把「发起」与「执行」拆开：普通账号点下去只是**提交一条申请**，
/// 由超级管理员在「操作审批」页确认后才真正执行。
/// </para>
///
/// <para>
/// 只对**非超级管理员**生效：超管本来就是最终责任人，再要求自己审批自己只会逼人绕过它。
/// </para>
/// </summary>
public sealed class ApprovalRequestDto
{
    /// <summary>申请 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>操作类型（见 <see cref="ApprovalKinds"/>）。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>一句话说明「要做什么」（由操作方生成，用于审批人快速判断）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>原始参数（JSON，执行时按类型还原）。</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>状态，见 <see cref="ApprovalStatuses"/>。</summary>
    public string Status { get; set; } = ApprovalStatuses.Pending;

    /// <summary>发起人账号。</summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>发起时刻（UTC）。</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>审批人账号（未处理时为空）。</summary>
    public string? DecidedBy { get; set; }

    /// <summary>处理时刻（UTC）。</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>审批意见。</summary>
    public string? Comment { get; set; }

    /// <summary>执行结果摘要（通过并执行后填入；执行失败也写在这里）。</summary>
    public string? Result { get; set; }
}

/// <summary>审批列表：带上「当前账号能否审批」。</summary>
/// <para>
/// 由服务端算而不是前端猜：超管的权限是「展开成全选」的，前端拿不到角色时没法可靠判断，
/// 猜错了就是「按钮能点但一点就 403」。
/// </para>
public sealed class ApprovalListDto
{
    /// <summary>当前账号是否可以审批（只有超级管理员可以）。</summary>
    public bool CanDecide { get; set; }

    /// <summary>审批记录（新的在前）。</summary>
    public List<ApprovalRequestDto> Items { get; set; } = [];
}

/// <summary>审批（通过 / 驳回）的请求体。</summary>
public sealed class ApprovalDecisionRequest
{
    /// <summary>审批意见（可选）。驳回时建议写清原因。</summary>
    public string? Comment { get; set; }
}

/// <summary>「恢复备份」这类操作的返回：是已经执行了，还是转成了待审批。</summary>
public sealed class DangerousActionResultDto
{
    /// <summary>是否已经执行（<c>false</c> 表示已提交审批、等超级管理员确认）。</summary>
    public bool Executed { get; set; }

    /// <summary>提交审批时返回的申请 ID。</summary>
    public string? ApprovalId { get; set; }

    /// <summary>结果状态：<c>done</c> / <c>failed</c> / <c>pending</c>。</summary>
    public string Status { get; set; } = "done";

    /// <summary>给界面看的说明。</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>审批状态取值。</summary>
public static class ApprovalStatuses
{
    /// <summary>等待超级管理员处理。</summary>
    public const string Pending = "pending";

    /// <summary>已通过并执行成功。</summary>
    public const string Approved = "approved";

    /// <summary>已驳回（不执行）。</summary>
    public const string Rejected = "rejected";

    /// <summary>已通过但执行失败（保留这条记录，别让人以为已经生效）。</summary>
    public const string Failed = "failed";

    /// <summary>发起人自己撤回。</summary>
    public const string Cancelled = "cancelled";

    /// <summary>状态的中文说明。</summary>
    public static string Describe(string? status) => status switch
    {
        Approved => "已通过",
        Rejected => "已驳回",
        Failed => "执行失败",
        Cancelled => "已撤回",
        _ => "待审批",
    };
}

/// <summary>需要审批的操作类型。</summary>
public static class ApprovalKinds
{
    /// <summary>从备份恢复数据库（整库覆盖，最危险的一个）。</summary>
    public const string BackupRestore = "backup.restore";

    /// <summary>类型的中文说明。</summary>
    public static string Describe(string? kind) => kind switch
    {
        BackupRestore => "恢复备份",
        _ => kind ?? string.Empty,
    };
}
