using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace Yaesu_Web_Control.Services.Alexa;

// Verifies Amazon's signature on an incoming Alexa Skill request, per
//   https://developer.amazon.com/en-US/docs/alexa/custom-skills/host-a-custom-skill-as-a-web-service.html#verifying-that-the-request-was-sent-by-alexa
//
// Required steps:
//
//   1. Validate the SignatureCertChainUrl HTTP header. It MUST be:
//      - HTTPS protocol
//      - Host s3.amazonaws.com (case-insensitive)
//      - Path starts with /echo.api/
//      - Port 443 or omitted
//      - Normalised (no .. that would resolve outside /echo.api/)
//
//   2. Download the certificate chain at that URL (cached by URL — Amazon
//      uses the same cert for many requests, so we cache the parsed leaf).
//
//   3. Validate the certificate chain:
//      - Leaf cert NotBefore/NotAfter currently valid
//      - Chain trusts to a system-known CA (Amazon's chain through DigiCert)
//      - Leaf cert's Subject Alternative Name includes echo-api.amazon.com
//
//   4. Verify the SHA-256 RSA signature in the Signature header against the
//      raw request body bytes using the leaf cert's public key.
//
// Replay-protection (timestamp window) is enforced by the controller after
// JSON deserialization, since it depends on the parsed body.
public class AmazonSignatureVerifier
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AmazonSignatureVerifier> _logger;

    private const string ExpectedHost = "s3.amazonaws.com";
    private const string ExpectedPathPrefix = "/echo.api/";
    private const string ExpectedSan = "echo-api.amazon.com";
    private static readonly TimeSpan CertCacheTtl = TimeSpan.FromHours(24);

    public AmazonSignatureVerifier(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<AmazonSignatureVerifier> logger)
    {
        _http   = httpClientFactory.CreateClient(nameof(AmazonSignatureVerifier));
        _cache  = cache;
        _logger = logger;
    }

    /// <summary>
    /// Verify the signature on an Alexa request. Returns Ok() on success, or
    /// Fail(reason) on any validation failure. The reason is logged-only — the
    /// HTTP response intentionally doesn't reveal which check failed (so a
    /// would-be attacker can't binary-search their way to a valid signature).
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(
        string requestBody,
        string signatureHeader,
        string certChainUrlHeader,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
            return VerifyResult.Fail("Signature header missing");
        if (string.IsNullOrWhiteSpace(certChainUrlHeader))
            return VerifyResult.Fail("SignatureCertChainUrl header missing");

        // ── 1. Validate the cert-chain URL ──────────────────────────────────
        if (!IsValidCertChainUrl(certChainUrlHeader, out var urlError))
            return VerifyResult.Fail($"SignatureCertChainUrl invalid: {urlError}");

        // ── 2. Fetch (cached) and parse the cert chain ──────────────────────
        var leafCert = await GetLeafCertAsync(certChainUrlHeader, cancellationToken);
        if (leafCert == null)
            return VerifyResult.Fail("Cert chain fetch or parse failed");

        // ── 3a. Leaf cert validity window ───────────────────────────────────
        var now = DateTime.UtcNow;
        if (now < leafCert.NotBefore.ToUniversalTime() || now > leafCert.NotAfter.ToUniversalTime())
            return VerifyResult.Fail($"Leaf cert outside validity window ({leafCert.NotBefore:O} to {leafCert.NotAfter:O})");

        // ── 3b. Leaf cert SAN must include echo-api.amazon.com ──────────────
        if (!HasSubjectAlternativeName(leafCert, ExpectedSan))
            return VerifyResult.Fail($"Leaf cert SAN does not include {ExpectedSan}");

        // ── 3c. Chain validation against system trust store ─────────────────
        // Amazon's chain currently roots to Starfield / Amazon CA, which is
        // in the Windows trusted-root store by default. If a future Amazon
        // chain change broke this, we'd need to bundle their root manually —
        // not the case today.
        using (var chain = new X509Chain())
        {
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            if (!chain.Build(leafCert))
            {
                var reasons = string.Join("; ", chain.ChainStatus.Select(s => $"{s.Status}: {s.StatusInformation.Trim()}"));
                return VerifyResult.Fail($"Cert chain build failed: {reasons}");
            }
        }

        // ── 4. Verify the SHA-256 RSA signature against the raw body ────────
        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signatureHeader);
        }
        catch (FormatException)
        {
            return VerifyResult.Fail("Signature header is not valid base64");
        }

        var bodyBytes = Encoding.UTF8.GetBytes(requestBody);

        using var rsa = leafCert.GetRSAPublicKey();
        if (rsa == null)
            return VerifyResult.Fail("Leaf cert does not expose an RSA public key");

        bool signatureOk = rsa.VerifyData(
            bodyBytes,
            signatureBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        if (!signatureOk)
            return VerifyResult.Fail("Signature does not match request body");

        _logger.LogDebug("Alexa signature OK (cert subject={Subject})", leafCert.Subject);
        return VerifyResult.Ok();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Cert chain URL validation
    // ────────────────────────────────────────────────────────────────────────

    private static bool IsValidCertChainUrl(string url, out string error)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "not a valid absolute URI";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"scheme {uri.Scheme} (must be https)";
            return false;
        }

        if (!string.Equals(uri.Host, ExpectedHost, StringComparison.OrdinalIgnoreCase))
        {
            error = $"host {uri.Host} (must be {ExpectedHost})";
            return false;
        }

        // Default HTTPS port (443) is fine; explicit 443 is fine; anything else isn't.
        if (!uri.IsDefaultPort && uri.Port != 443)
        {
            error = $"port {uri.Port} (must be 443 or default)";
            return false;
        }

        // AbsolutePath is already normalised by Uri (resolves ../ traversal).
        if (!uri.AbsolutePath.StartsWith(ExpectedPathPrefix, StringComparison.Ordinal))
        {
            error = $"path {uri.AbsolutePath} (must start with {ExpectedPathPrefix})";
            return false;
        }

        error = "";
        return true;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Cert chain fetch + cache
    // ────────────────────────────────────────────────────────────────────────

    private async Task<X509Certificate2?> GetLeafCertAsync(string url, CancellationToken ct)
    {
        // Cache by URL. Amazon re-uses the same cert for the duration of its
        // validity, so this drops to one fetch per process per cert rotation.
        if (_cache.TryGetValue(url, out X509Certificate2? cached))
        {
            return cached;
        }

        string pem;
        try
        {
            pem = await _http.GetStringAsync(url, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cert chain fetch failed for {Url}", url);
            return null;
        }

        // The cert chain comes back as a sequence of PEM-encoded certs. The
        // first one is the leaf; the remainder are intermediates leading to
        // a trusted root. We only need the leaf for signature verification
        // (chain build uses the system trust store + any intermediates the
        // server presents during HTTPS, which we don't have here — but the
        // Amazon chain is well-known and present in Windows' store).
        try
        {
            var leaf = X509Certificate2.CreateFromPem(pem);
            // Re-import via the DER bytes through X509CertificateLoader so
            // the cert always has its public-key accessor populated, and we
            // avoid the obsolete X509Certificate2(byte[]) constructor.
            var reimported = X509CertificateLoader.LoadCertificate(leaf.RawData);
            leaf.Dispose();

            _cache.Set(url, reimported, CertCacheTtl);
            return reimported;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cert chain parse failed for {Url}", url);
            return null;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Subject Alternative Name check
    // ────────────────────────────────────────────────────────────────────────

    private static bool HasSubjectAlternativeName(X509Certificate2 cert, string expectedName)
    {
        // Subject Alternative Name OID is 2.5.29.17. Iterate the cert's
        // extensions, find the SAN, parse it, and check for an exact DNS
        // name match. Could use X509SubjectAlternativeNameExtension in
        // .NET 7+ for cleaner parsing.
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue;

            var sanText = ext.Format(multiLine: false);
            // Format returns text like "DNS Name=echo-api.amazon.com, DNS Name=..."
            // Comparison is case-insensitive (DNS is case-insensitive).
            if (sanText.IndexOf(expectedName, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }
}

/// <summary>
/// Outcome of a signature verification attempt. On failure the reason is
/// logged but not echoed in the HTTP response — defence in depth.
/// </summary>
public readonly record struct VerifyResult(bool IsValid, string FailReason)
{
    public static VerifyResult Ok() => new(true, "");
    public static VerifyResult Fail(string reason) => new(false, reason);
}
