namespace ControlHub.Protocol.Dtos;

/// <summary>账号信息（管理端账号列表）。</summary>
public sealed class AccountDto
{
    /// <summary>账号 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>登录用户名。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>显示名称。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>角色：<c>admin</c>（超级管理员，全部权限）/ <c>custom</c>（按权限勾选）。</summary>
    public string Role { get; set; } = PermissionKeys.CustomRole;

    /// <summary>已授予的权限键集合。超级管理员此项为空但实际拥有全部权限。</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>是否仍处于「首次登录需改密」状态。</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>新建或修改账号。</summary>
public sealed class AccountUpsertRequest
{
    /// <summary>登录用户名。新建时必填；修改时忽略（用户名不可改）。</summary>
    public string? Username { get; set; }

    /// <summary>显示名称。</summary>
    public string? DisplayName { get; set; }

    /// <summary>角色：<c>admin</c> 或 <c>custom</c>。仅超级管理员可设为 <c>admin</c>。</summary>
    public string? Role { get; set; }

    /// <summary>权限键集合，仅在 <see cref="Role"/> 为 <c>custom</c> 时生效。</summary>
    public List<string>? Permissions { get; set; }

    /// <summary>新建时的初始密码；留空表示由系统生成并返回。</summary>
    public string? Password { get; set; }
}

/// <summary>新建账号的结果。</summary>
public sealed class AccountCreatedDto
{
    /// <summary>新建的账号。</summary>
    public AccountDto Account { get; set; } = new();

    /// <summary>系统生成的初始密码（仅当请求未指定密码时返回，且只返回这一次）。</summary>
    public string? GeneratedPassword { get; set; }
}

/// <summary>权限目录项：一个模块的「查看 / 修改」两个权限键。</summary>
public sealed class PermissionModuleDto
{
    /// <summary>模块键，例如 <c>devices</c>。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>模块名称，例如「设备与分组」。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>一句话说明这个模块管什么。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>查看权限键；为空表示该模块没有独立的查看权限。</summary>
    public string? ReadKey { get; set; }

    /// <summary>修改权限键；为空表示该模块只读。</summary>
    public string? WriteKey { get; set; }
}

/// <summary>角色模板：把常见岗位打包成「一键套用」的权限组合。</summary>
public sealed class PermissionPresetDto
{
    /// <summary>模板键，例如 <c>viewer</c>。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>模板名称，例如「只读观察员」。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>一句话说明这个角色能做什么。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>套用后勾选的权限键集合。</summary>
    public List<string> Permissions { get; set; } = [];
}

/// <summary>邀请码：凭码可自助注册为一个拥有预设权限的账号。</summary>
public sealed class RegisterCodeDto
{
    /// <summary>邀请码本体。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// 该邀请码是否可用。<c>false</c> 表示密文解密失败（密钥文件被更换 / 丢失）——此时 <see cref="Code"/>
    /// 为空，界面应显示「不可用」并提示重新生成（#36）。
    /// </summary>
    public bool Available { get; set; } = true;

    /// <summary>
    /// 指纹引用（<c>ref:xxxxxxxx</c>）：明文不可用时，管理端仍可用它查询 / 删除该行（#36）。
    /// 不含邀请码的任何原始字符，可安全展示。
    /// </summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>备注，便于管理端识别用途。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>注册后获得的权限键集合。</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>可注册次数；0 表示不限。</summary>
    public int MaxUses { get; set; }

    /// <summary>已使用次数。</summary>
    public int UsedCount { get; set; }

    /// <summary>剩余可用次数；-1 表示不限。</summary>
    public int RemainingUses { get; set; }

    /// <summary>过期时间；为空表示长期有效。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>新建邀请码。</summary>
public sealed class RegisterCodeUpsertRequest
{
    /// <summary>备注。</summary>
    public string? Note { get; set; }

    /// <summary>注册后获得的权限键集合；留空表示用默认权限集。</summary>
    public List<string>? Permissions { get; set; }

    /// <summary>可注册次数；0 表示不限。</summary>
    public int MaxUses { get; set; } = 1;

    /// <summary>有效小时数；0 表示长期有效。</summary>
    public int ValidHours { get; set; } = 72;
}

/// <summary>自助注册请求（凭邀请码，无需登录）。</summary>
public sealed class RegisterRequest
{
    /// <summary>管理员生成的邀请码。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>期望的登录用户名。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>显示名称，留空时用用户名。</summary>
    public string? DisplayName { get; set; }

    /// <summary>登录密码。</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>自助注册的可公开状态（登录页据此决定是否显示「注册」入口）。</summary>
public sealed class RegistrationInfoDto
{
    /// <summary>是否允许凭邀请码自助注册。</summary>
    public bool Enabled { get; set; }

    /// <summary>是否允许提交自助注册申请（提交后需管理员审批）。</summary>
    public bool ApprovalEnabled { get; set; }

    /// <summary>邀请码前缀是否可校验（用于前端提示）。</summary>
    public string Hint { get; set; } = string.Empty;
}

/// <summary>自助注册申请（管理端审批列表）。</summary>
public sealed class RegisterRequestDto
{
    /// <summary>申请 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>申请使用的登录用户名。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>显示名称。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>申请人填写的说明（供管理员判断身份）。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>状态：<c>pending</c> / <c>approved</c> / <c>rejected</c>。</summary>
    public string Status { get; set; } = "pending";

    /// <summary>拒绝理由。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>审批人用户名。</summary>
    public string ReviewedBy { get; set; } = string.Empty;

    /// <summary>审批时间。</summary>
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>提交时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>提交自助注册申请（无需登录）。</summary>
public sealed class RegisterRequestSubmit
{
    /// <summary>期望的登录用户名。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>显示名称，留空时用用户名。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>自行设置的密码，批准后直接生效。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>说明（例如「高二年级语文老师」），便于管理员核实。</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>批准注册申请：指定该账号获得的权限。</summary>
public sealed class RegisterRequestApprove
{
    /// <summary>批准后授予的权限键集合；留空表示用默认权限集。</summary>
    public List<string>? Permissions { get; set; }
}

/// <summary>拒绝注册申请。</summary>
public sealed class RegisterRequestReject
{
    /// <summary>拒绝理由（会记录在申请上，便于以后追溯）。</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// 界面偏好（#40）：跨设备保持一致的那部分自定义设置。
/// <para>只收「布局类」偏好（模块顺序与显隐、表格列）。主题 / 密度 / 字体这类**设备相关**的仍留在浏览器本地：
/// 同一账号在办公室电脑和教室大屏上的要求往往不同，同步过去反而是打扰。</para>
/// </summary>
public sealed class UiPreferencesDto
{
    /// <summary>区域（scope）→ 布局项数组；数组顺序即展示顺序。</summary>
    public Dictionary<string, List<UiLayoutItemDto>> Layouts { get; set; } = [];
}

/// <summary>布局中的一项（模块 / 列）。</summary>
public sealed class UiLayoutItemDto
{
    /// <summary>模块 / 列的唯一键，与前端 `prefs.js` 中该 scope 的 key 对应。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>是否显示。</summary>
    public bool Enabled { get; set; } = true;
}
