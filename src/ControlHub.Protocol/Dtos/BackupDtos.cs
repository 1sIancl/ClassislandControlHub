namespace ControlHub.Protocol.Dtos;

/// <summary>
/// 创建备份时可选的内容（#59）。
/// <para>默认只含**数据库 + 配置文件**。加密密钥**不在**默认范围内，因为它是凭据：
/// 谁拿到含密钥的备份，谁就拿到了全部注册码 / 邀请码 / Webhook 加签密钥。</para>
/// </summary>
public sealed class BackupOptionsDto
{
    /// <summary>包含数据目录下的配置文件（<c>*.json</c>）。默认包含。</summary>
    public bool IncludeConfigFiles { get; set; } = true;

    /// <summary>
    /// 包含加密密钥 <c>secrets.key</c>（默认**不含**）。
    /// <para>勾上之后这份备份可以「直接接管」服务器（连同全部凭据），所以**不要外发、不要放公共云盘**；
    /// 反过来，换机器恢复时没有它就会出现「注册码全部不可用、Webhook 报密钥不可解密」。</para>
    /// </summary>
    public bool IncludeSecretsKey { get; set; }

    /// <summary>包含本机免登录令牌 <c>local-shell.token</c>（默认不含）。</summary>
    public bool IncludeLocalShellToken { get; set; }
}
