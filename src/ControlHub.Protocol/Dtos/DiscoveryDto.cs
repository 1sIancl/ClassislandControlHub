namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 局域网发现——探测请求。
/// 客户端向广播地址的发现端口发送该报文，服务端应答 <see cref="DiscoveryAnnounce"/>。
/// </summary>
public sealed class DiscoveryProbe
{
    /// <summary>固定魔数，用于快速过滤无关 UDP 报文。</summary>
    public string Magic { get; set; } = HubProtocol.DiscoveryMagic;

    /// <summary>协议主版本。</summary>
    public int Version { get; set; } = HubProtocol.MajorVersion;

    /// <summary>随机数，应答会原样带回，便于客户端匹配请求。</summary>
    public string Nonce { get; set; } = string.Empty;
}

/// <summary>
/// 局域网发现——服务端应答。
/// </summary>
public sealed class DiscoveryAnnounce
{
    /// <summary>固定魔数。</summary>
    public string Magic { get; set; } = HubProtocol.DiscoveryMagic;

    /// <summary>协议主版本。</summary>
    public int Version { get; set; } = HubProtocol.MajorVersion;

    /// <summary>回显的随机数。</summary>
    public string Nonce { get; set; } = string.Empty;

    /// <summary>服务器名称，例如「XX 中学集控服务器」。</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>服务器 API 基地址，例如 <c>http://192.168.1.5:29800</c>。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API 根路径。</summary>
    public string ApiPrefix { get; set; } = HubProtocol.ApiPrefix;

    /// <summary>服务器是否要求注册码。为 <c>false</c> 时客户端可直接注册。</summary>
    public bool RequiresEnrollCode { get; set; } = true;

    /// <summary>当前配置版本号，便于客户端判断是否需要同步。</summary>
    public long Revision { get; set; }

    /// <summary>服务器生成时间（UTC）。</summary>
    public DateTimeOffset ServerTime { get; set; } = DateTimeOffset.UtcNow;
}
