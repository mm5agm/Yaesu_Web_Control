using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>
    /// Generates and locates the optional self-signed HTTPS certificate in AppData.
    /// </summary>
    public static class HttpsCertificateService
    {
        public const string CertFileName = "https.pfx";
        public const string PfxPassword = "ywc-https"; // local-only; enables cross-platform Kestrel load

        public static string AppDataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control");

        public static string CertificatePath => Path.Combine(AppDataDirectory, CertFileName);

        public static bool CertificateExists => File.Exists(CertificatePath);

        /// <summary>
        /// Create a self-signed cert with localhost + optional SAN hostnames/IPs.
        /// Overwrites any existing certificate.
        /// </summary>
        public static void Generate(IEnumerable<string> sanHosts, int validityYears = 2)
        {
            Directory.CreateDirectory(AppDataDirectory);

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=Yaesu Web Control",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    true));
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // serverAuth
                    false));

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);

            foreach (var raw in sanHosts)
            {
                var host = raw.Trim();
                if (string.IsNullOrEmpty(host)) continue;
                if (System.Net.IPAddress.TryParse(host, out var ip))
                    san.AddIpAddress(ip);
                else
                    san.AddDnsName(host);
            }

            request.CertificateExtensions.Add(san.Build());

            using var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(validityYears));

            var pfxBytes = cert.Export(X509ContentType.Pfx, PfxPassword);
            File.WriteAllBytes(CertificatePath, pfxBytes);
        }

        public static X509Certificate2? Load()
        {
            if (!CertificateExists) return null;
            return X509CertificateLoader.LoadPkcs12FromFile(CertificatePath, PfxPassword);
        }

        public static string DescribeCertificate()
        {
            if (!CertificateExists) return "No certificate generated yet.";
            try
            {
                using var cert = Load();
                if (cert == null) return "Certificate file unreadable.";
                var sb = new StringBuilder();
                sb.Append($"Subject: {cert.Subject}; ");
                sb.Append($"NotAfter: {cert.NotAfter:u}; ");
                sb.Append($"Thumbprint: {cert.Thumbprint}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Certificate error: {ex.Message}";
            }
        }
    }
}
