namespace IslandHub.Protocol.Dtos;

/// <summary>
/// 数据迁移包（路线图 #61）：把一台服务器上「人配出来的东西」整包搬到另一台。
///
/// <para>
/// 刻意做成**表级**而不是按实体逐个定义：实体字段以后还会加，逐个映射必然漏字段，
/// 而「漏掉的那个字段」在现场表现为「迁过去之后某项配置没了」，很难查。
/// 表级拷贝的代价是要显式列出**敏感列**（设备令牌、密码哈希、Webhook 密钥）并剥离它们。
/// </para>
///
/// <para>
/// 一律不导出的东西：审计日志、诊断 / 指令 / 客户端日志、在线率采样、会话、注册码与邀请码、
/// API 密钥、临时换课覆盖、提醒触发记录、Webhook 投递明细——这些是**运行时数据**或**凭据**，
/// 搬过去只会带来「对方服务器上出现了别人的登录记录」这类问题。
/// </para>
/// </summary>
public sealed class MigrationPackageDto
{
    /// <summary>包格式标识（导入时校验，避免把别的 JSON 当迁移包吃进去）。</summary>
    public string Format { get; set; } = MigrationFormat;

    /// <summary>包格式当前版本。</summary>
    public string FormatVersion { get; set; } = "1";

    /// <summary>导出方产品名。</summary>
    public string Product { get; set; } = string.Empty;

    /// <summary>导出方产品版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>导出方服务器名（便于确认「这是哪台机器导的」）。</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>导出时刻（UTC）。</summary>
    public DateTimeOffset ExportedAt { get; set; }

    /// <summary>各表的行数与是否含敏感列的说明，导入前一眼能看清包里有什么。</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>表名 → 表内容。</summary>
    public Dictionary<string, MigrationTableDto> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>格式标识常量。</summary>
    public const string MigrationFormat = "islandhub-migration";
}

/// <summary>迁移包里的单张表。</summary>
public sealed class MigrationTableDto
{
    /// <summary>列名（已剥离敏感列）。</summary>
    public List<string> Columns { get; set; } = [];

    /// <summary>
    /// 行数据：列名 → 文本值。
    /// <para>值统一按文本传：SQLite 是动态类型，写回时靠列亲和性自动转换，
    /// 而按类型逐个映射会在日期 / 布尔 / 可空列上没完没了地对不齐。</para>
    /// </summary>
    public List<Dictionary<string, string?>> Rows { get; set; } = [];
}

/// <summary>导入预览：先说清「会新增什么、覆盖什么、跳过什么」，再让人决定。</summary>
public sealed class MigrationPreviewDto
{
    /// <summary>一次性令牌：确认时按它取回刚才那份包（几 MB 的包不必再传一遍）。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>包的导出时刻与来源版本（提示「别用太老的包盖新服务器」）。</summary>
    public DateTimeOffset ExportedAt { get; set; }

    /// <summary>导出方版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>导出方服务器名。</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>逐表统计。</summary>
    public List<MigrationTablePreviewDto> Tables { get; set; } = [];

    /// <summary>整体提示（例如「导入前会自动备份」「设备与账号不会覆盖本机已有记录」）。</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>新增 / 覆盖 / 跳过 合计。</summary>
    public int TotalAdded => Tables.Sum(t => t.Added);

    /// <summary>覆盖合计。</summary>
    public int TotalUpdated => Tables.Sum(t => t.Updated);

    /// <summary>跳过合计。</summary>
    public int TotalSkipped => Tables.Sum(t => t.Skipped);
}

/// <summary>单表的导入预览。</summary>
public sealed class MigrationTablePreviewDto
{
    /// <summary>表名。</summary>
    public string Table { get; set; } = string.Empty;

    /// <summary>给人看的名字。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>本机没有、会新增的行数。</summary>
    public int Added { get; set; }

    /// <summary>本机已有、会被覆盖的行数。</summary>
    public int Updated { get; set; }

    /// <summary>被跳过的行数（缺主键、或按策略不动已有记录）。</summary>
    public int Skipped { get; set; }

    /// <summary>该表的特殊说明（策略 / 列差异）。</summary>
    public string? Note { get; set; }
}

/// <summary>导入结果。</summary>
public sealed class MigrationApplyResultDto
{
    /// <summary>导入前自动生成的备份 ID（出问题可以一键恢复）。</summary>
    public string BackupId { get; set; } = string.Empty;

    /// <summary>逐表落地情况。</summary>
    public List<MigrationTablePreviewDto> Tables { get; set; } = [];

    /// <summary>提示与说明。</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>写入的总行数。</summary>
    public int Written => Tables.Sum(t => t.Added + t.Updated);
}
