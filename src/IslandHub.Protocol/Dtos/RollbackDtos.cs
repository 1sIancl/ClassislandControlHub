namespace IslandHub.Protocol.Dtos;

/// <summary>
/// 一份「程序文件快照」（路线图 #89）。
///
/// <para>
/// 自动更新会把程序文件整体换掉，**不保留旧版本**——所以「更新完发现新版本有问题」时，
/// 除了重装没有退路。快照就是给这种时刻准备的：更新前先把当前程序文件打成一个 zip
/// （**不含数据目录**，数据本来就不动），需要时一键换回去。
/// </para>
/// </summary>
public sealed class RollbackSnapshotDto
{
    /// <summary>快照 ID（快照文件名去掉扩展名）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>快照对应的产品版本（例如 <c>1.4.5</c>）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>为什么拍这份快照：<c>pre-update</c> / <c>pre-rollback</c> / <c>manual</c>。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>创建时刻。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>快照大小（字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>快照里的文件数。</summary>
    public int FileCount { get; set; }
}

/// <summary>回滚结果。</summary>
public sealed class RollbackResultDto
{
    /// <summary>是否成功换回快照里的文件。</summary>
    public bool Ok { get; set; }

    /// <summary>用到的快照 ID。</summary>
    public string SnapshotId { get; set; } = string.Empty;

    /// <summary>成功替换的文件数。</summary>
    public int Replaced { get; set; }

    /// <summary>因文件被占用而跳过的文件数（Windows 上正在运行的 dll 就属于这种）。</summary>
    public int Skipped { get; set; }

    /// <summary>是否需要重启服务才能完成回滚。</summary>
    public bool RestartRequired { get; set; }

    /// <summary>给界面看的说明。</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>快照原因取值。</summary>
public static class RollbackReasons
{
    /// <summary>自动更新前自动拍的。</summary>
    public const string PreUpdate = "pre-update";

    /// <summary>回滚前自动拍的（让回滚本身也可回退）。</summary>
    public const string PreRollback = "pre-rollback";

    /// <summary>管理员手动拍的。</summary>
    public const string Manual = "manual";

    /// <summary>原因的中文说明。</summary>
    public static string Describe(string? reason) => reason switch
    {
        PreUpdate => "更新前自动快照",
        PreRollback => "回滚前自动快照",
        Manual => "手动快照",
        _ => reason ?? string.Empty,
    };
}
