namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 标签（#10）。
/// <para>与「楼栋 / 楼层」分组**正交**：分组回答「这台设备在哪」（只属于一处），
/// 标签回答「它是什么」（可以同时是「高考考场」和「待维修」）。两者不互相替代——
/// 想靠分组凑出「考试用的那批」只会把层级结构搞乱。</para>
/// </summary>
public sealed class TagDto
{
    /// <summary>标签 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>标签名（唯一，不区分 ASCII 大小写）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>颜色（十六进制，如 <c>#e5484d</c>；留空表示用默认色）。</summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>关联的设备数（只计未撤销的设备）。</summary>
    public int DeviceCount { get; set; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>挂在设备上的标签引用（只带展示需要的字段，别把整个标签表塞进设备列表）。</summary>
public sealed class TagRefDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = string.Empty;
}

/// <summary>新建 / 修改标签的请求。</summary>
public sealed class TagRequest
{
    /// <summary>标签名（必填，首尾空白会被忽略）。</summary>
    public string? Name { get; set; }

    /// <summary>颜色（可选）。</summary>
    public string? Color { get; set; }
}

/// <summary>
/// 设置某台设备的标签集合。
/// <para>刻意做成**整体替换**而不是「加一个 / 删一个」：界面是勾选式的，
/// 连续调两次增删接口时中间态一旦失败就会留下对不上的结果，
/// 而「提交最终集合」永远只有一个确定状态。</para>
/// </summary>
public sealed class DeviceTagsRequest
{
    public List<string> TagIds { get; set; } = [];
}
