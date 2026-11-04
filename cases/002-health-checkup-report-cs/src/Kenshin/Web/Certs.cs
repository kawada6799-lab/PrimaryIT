using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kenshin.Web;

/// <summary>専用 Wi-Fi 側の HTTPS 用の自己署名証明書。タブレットには data\ca.crt を一度入れる（マイクを使うため HTTPS が要る）。</summary>
public static class Certs
{
    public static X509Certificate2 Ensure(string dataDir, string lanAddress)
    {
        Directory.CreateDirectory(dataDir);
        var pfx = Path.Combine(dataDir, "cert.pfx");
        var cer = Path.Combine(dataDir, "ca.crt");
        if (File.Exists(pfx))
        {
            try
            {
                var existing = new X509Certificate2(pfx, "", X509KeyStorageFlags.Exportable);
                if (existing.NotAfter > DateTime.Now.AddDays(30) && existing.Subject.Contains(lanAddress)) return existing;
            }
            catch { /* 作り直す */ }
        }
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=kenshin {lanAddress}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("kenshin.local");
        if (IPAddress.TryParse(lanAddress, out var ip)) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx, ""));
        File.WriteAllBytes(cer, cert.Export(X509ContentType.Cert));
        return new X509Certificate2(pfx, "", X509KeyStorageFlags.Exportable);
    }
}
