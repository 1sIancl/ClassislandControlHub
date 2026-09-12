using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ControlHub.Server.Services;

/// <summary>
/// HTTPS 证书提供者。
/// <para>
/// 学校内网往往没有正式证书。这里优先使用配置文件中指定的证书；
/// 未指定时生成一张自签名证书并持久化到数据目录，使内网也能以 HTTPS 提供服务。
/// </para>
/// <para>
/// 注意：自签名证书不会被客户端自动信任。请在客户端插件中开启
/// 「允许不受信任的证书」，或在生产环境用正式证书 / 反向代理终止 TLS。
/// </para>
/// </summary>
public static class CertificateProvider
{
    /// <summary>
    /// 加载或创建证书。
    /// </summary>
    /// <param name="certificatePath">证书文件路径（<c>.pfx</c>）。为空时使用自动生成的证书。</param>
    /// <param name="password">证书口令。</param>
    /// <param name="dataDirectory">自动生成证书时的存放目录。</param>
    /// <param name="serverName">证书主题名。</param>
    /// <param name="logger">日志器。</param>
    /// <returns>可用的证书；失败时返回 <c>null</c>。</returns>
    public static X509Certificate2? LoadOrCreate(string? certificatePath, string? password,
        string dataDirectory, string serverName, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(certificatePath) && File.Exists(certificatePath))
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(certificatePath,
                    string.IsNullOrEmpty(password) ? null : password);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "加载证书 {Path} 失败，将尝试使用自动生成的证书。", certificatePath);
            }
        }

        var autoPath = Path.Combine(dataDirectory, "controlhub-selfsigned.pfx");
        const string autoPassword = "controlhub";

        if (File.Exists(autoPath))
        {
            try
            {
                var cached = X509CertificateLoader.LoadPkcs12FromFile(autoPath, autoPassword);
                if (cached.NotAfter > DateTime.Now.AddDays(7))
                {
                    return cached;
                }

                logger.LogInformation("已缓存的自签名证书即将过期，将重新生成。");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "读取已缓存的自签名证书失败，将重新生成。");
            }
        }

        try
        {
            var certificate = CreateSelfSigned(serverName);
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllBytes(autoPath, certificate.Export(X509ContentType.Pfx, autoPassword));

            logger.LogWarning(
                "已生成自签名证书用于 HTTPS（有效至 {Expiry:yyyy-MM-dd}）。" +
                "客户端需信任该证书或开启「允许不受信任的证书」，否则请改用 HTTP 或反向代理。",
                certificate.NotAfter);

            return certificate;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "生成自签名证书失败，HTTPS 将不可用。");
            return null;
        }
    }

    private static X509Certificate2 CreateSelfSigned(string serverName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={serverName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(serverName);
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
        {
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                sanBuilder.AddIpAddress(address);
            }
        }

        request.CertificateExtensions.Add(sanBuilder.Build());

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        // 重新导入以确保私钥可用且可持久化导出。
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx, "controlhub"),
            "controlhub");
    }
}
