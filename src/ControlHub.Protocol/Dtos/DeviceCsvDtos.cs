namespace ControlHub.Protocol.Dtos;

/// <summary>设备 CSV 导入请求。</summary>
public sealed class DeviceImportRequest
{
    /// <summary>CSV 文本（含表头，列名见导入模板）。</summary>
    public string Csv { get; set; } = string.Empty;

    /// <summary>
    /// 只出报告不落库。先用它检查一遍「哪几行会更新、哪几行会预注册、哪几行报错」，
    /// 确认无误后再正式导入。
    /// </summary>
    public bool DryRun { get; set; }
}

/// <summary>设备 CSV 导入结果。</summary>
public sealed class DeviceImportResultDto
{
    /// <summary>是否为预演（未落库）。</summary>
    public bool DryRun { get; set; }

    /// <summary>预注册的设备数（匹配不到已有设备，等教室端上线认领）。</summary>
    public int Created { get; set; }

    /// <summary>更新的设备数。</summary>
    public int Updated { get; set; }

    /// <summary>出错的行数。</summary>
    public int Failed { get; set; }

    /// <summary>逐行结果。</summary>
    public List<DeviceImportRowResultDto> Rows { get; set; } = [];
}

/// <summary>导入时某一行的处理结果。</summary>
public sealed class DeviceImportRowResultDto
{
    /// <summary>行号（从 1 开始，与表格里看到的一致）。</summary>
    public int Line { get; set; }

    /// <summary>动作：更新 / 预注册 / 错误（预演时为「将更新」「将预注册」）。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>设备名称（便于对照表格）。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>设备 ID（新建的预注册记录会给出生成的 ID）。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>说明：改了哪些字段，或出错原因。</summary>
    public string Message { get; set; } = string.Empty;
}
