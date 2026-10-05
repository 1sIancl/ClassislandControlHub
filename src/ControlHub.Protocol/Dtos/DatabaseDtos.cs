namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 数据库健康检查结果（#62）。
/// <para>SQLite 长期运行后会留下空闲页（删除数据不归还空间），WAL 文件也可能涨得比库还大。
/// 这里把「能不能回收、能回收多少」量化出来，管理员才好判断要不要整理。</para>
/// </summary>
public sealed class DatabaseHealthDto
{
    /// <summary>完整性检查是否通过（<c>PRAGMA integrity_check</c>）。</summary>
    public bool IntegrityOk { get; set; }

    /// <summary>完整性检查的原始结果（通过时是 <c>ok</c>）。</summary>
    public string IntegrityMessage { get; set; } = string.Empty;

    /// <summary>数据库主文件体积（字节）。</summary>
    public long DatabaseBytes { get; set; }

    /// <summary>WAL 文件体积（字节）。</summary>
    public long WalBytes { get; set; }

    /// <summary>共享内存文件体积（字节）。</summary>
    public long SharedMemoryBytes { get; set; }

    /// <summary>三者合计（字节）。</summary>
    public long TotalBytes { get; set; }

    /// <summary>页大小（字节）。</summary>
    public long PageSize { get; set; }

    /// <summary>总页数。</summary>
    public long PageCount { get; set; }

    /// <summary>空闲页数。</summary>
    public long FreePageCount { get; set; }

    /// <summary>空闲页占用的空间（= 空闲页数 × 页大小），即 VACUUM 大致能回收的上限。</summary>
    public long ReclaimableBytes { get; set; }

    /// <summary>检查时间。</summary>
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>VACUUM 整理结果。</summary>
public sealed class DatabaseVacuumResultDto
{
    /// <summary>整理前总体积（字节）。</summary>
    public long BeforeBytes { get; set; }

    /// <summary>整理后总体积（字节）。</summary>
    public long AfterBytes { get; set; }

    /// <summary>回收的字节数。</summary>
    public long ReclaimedBytes { get; set; }

    /// <summary>耗时（毫秒）。</summary>
    public long DurationMs { get; set; }
}
