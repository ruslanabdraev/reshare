using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Reshare.Core;

namespace Reshare.Host;

/// <summary>
/// Self-signed TLS certificate. Browsers only expose WebCodecs on secure origins, so the viewer
/// page must be served over HTTPS; the user accepts the certificate warning once per host.
/// </summary>
internal static class Certificates
{
    public static IReadOnlyList<IPAddress> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."))
            .Distinct()
            .ToList();

    public static X509Certificate2 GetOrCreate()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reshare");
        string path = Path.Combine(dir, "reshare-cert.pfx");
        var ips = LanAddresses().Append(IPAddress.Loopback).ToList();

        if (File.Exists(path))
        {
            try
            {
                var existing = new X509Certificate2(path, (string?)null, X509KeyStorageFlags.Exportable);
                var san = existing.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
                var covered = san?.EnumerateIPAddresses().ToHashSet() ?? [];
                if (existing.NotAfter > DateTime.Now.AddDays(7) && ips.All(covered.Contains))
                    return existing;
                Log.Info("Network addresses changed; generating a new certificate (the viewer will see the warning again)");
            }
            catch (CryptographicException) { }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=Reshare ({Environment.MachineName})", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddDnsName(Environment.MachineName);
        foreach (var ip in ips) sanBuilder.AddIpAddress(ip);
        req.CertificateExtensions.Add(sanBuilder.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));

        Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        // Re-load from PFX so the private key is usable by SChannel on Windows.
        return new X509Certificate2(path, (string?)null, X509KeyStorageFlags.Exportable);
    }
}
