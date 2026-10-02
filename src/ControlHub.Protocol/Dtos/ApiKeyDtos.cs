namespace ControlHub.Protocol.Dtos;

/// <summary>一把 API 密钥（**不含明文**：明文只在创建响应里出现一次）。</summary>
public sealed class ApiKeyDto
{
    /// <summary>密钥 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>名字，例如「机房监控脚本」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>明文前若干字符，用于辨认是哪一把（不足以还原密钥）。</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>授予的权限集合。</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>创建者账号。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>过期时间（为空表示长期有效）。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>最近使用时间（为空表示从未使用）。</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>是否已撤销。</summary>
    public bool Revoked { get; set; }

    /// <summary>备注（用途说明，便于交接）。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>是否仍然可用（未撤销且未过期）。</summary>
    public bool Active => !Revoked && (!ExpiresAt.HasValue || ExpiresAt.Value > DateTimeOffset.UtcNow);
}

/// <summary>创建 API 密钥的请求。</summary>
public sealed class ApiKeyCreateRequest
{
    /// <summary>名字（必填）：写清是谁在用，将来好判断该不该撤。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>授予的权限集合；建议只给只读。留空表示不授予任何权限（等于只能用探针类接口）。</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>有效天数；<c>0</c> 表示长期有效（默认建议填一个期限）。</summary>
    public int ExpiresInDays { get; set; }

    /// <summary>备注。</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>创建结果：明文只在这里出现一次。</summary>
public sealed class ApiKeyCreateResultDto
{
    /// <summary>密钥信息（不含明文）。</summary>
    public ApiKeyDto Key { get; set; } = new();

    /// <summary>密钥明文。<b>只返回这一次</b>，服务端只存哈希，之后无法再取回。</summary>
    public string Secret { get; set; } = string.Empty;
}
