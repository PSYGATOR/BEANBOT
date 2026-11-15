using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Asn1.X509;

namespace BeanBot.Transport;

public interface ICertificateVerifyer
{
    bool IsValid(X509CertificateStructure[] certs);
}

public sealed class CertificatePinMismatchException : Exception
{
    public CertificatePinMismatchException(
        string region,
        string expectedSha256,
        string presentedSha256,
        string presentedSubject)
        : base($"DTLS certificate pin mismatch for region '{region}': expected SHA-256 {expectedSha256}, got {presentedSha256}, subject='{presentedSubject}'. Re-pin with --pin-accept-new if intentional.")
    {
        Region = region;
        ExpectedSha256 = expectedSha256;
        PresentedSha256 = presentedSha256;
        PresentedSubject = presentedSubject;
    }

    public string Region { get; }
    public string ExpectedSha256 { get; }
    public string PresentedSha256 { get; }
    public string PresentedSubject { get; }
}

/// <summary>
/// Trust-on-first-use (TOFU) certificate pinning store (5.2, F1).
/// Persists certs/&lt;region&gt;.der and certs/&lt;region&gt;.sha256.
/// </summary>
public sealed class TofuCertificatePinStore
{
    private readonly string _certsDirectory;
    private readonly ILogger<TofuCertificatePinStore>? _logger;

    public TofuCertificatePinStore(string certsDirectory = "certs", ILogger<TofuCertificatePinStore>? logger = null)
    {
        _certsDirectory = ResolveSafeCertsPath(certsDirectory);
        _logger = logger;
    }

    private static string ResolveSafeCertsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return path;
        }

        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var cwd = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(winDir) && cwd.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(exeDir) && Directory.Exists(exeDir))
            {
                return Path.Combine(exeDir, path);
            }
        }

        return path;
    }

    public (string DerPath, string Sha256Path) GetPinPaths(string region)
    {
        var safeRegion = SanitizeRegion(region);
        return (
            Path.Combine(_certsDirectory, $"{safeRegion}.der"),
            Path.Combine(_certsDirectory, $"{safeRegion}.sha256"));
    }

    public void VerifyOrPin(string region, byte[] presentedDer, string presentedSubject, bool acceptNewPin)
    {
        ArgumentNullException.ThrowIfNull(presentedDer);
        if (presentedDer.Length == 0)
        {
            throw new InvalidOperationException($"Empty server certificate presented for region '{region}'.");
        }

        Directory.CreateDirectory(_certsDirectory);
        var (derPath, shaPath) = GetPinPaths(region);
        var presentedHash = ComputeSha256Hex(presentedDer);

        if (!File.Exists(derPath) || acceptNewPin)
        {
            File.WriteAllBytes(derPath, presentedDer);
            File.WriteAllText(shaPath, presentedHash + Environment.NewLine);
            _logger?.LogInformation(
                "Pinned DTLS certificate for region {Region} (acceptNew={AcceptNew}): SHA-256={Sha256}, Subject={Subject}",
                region, acceptNewPin, presentedHash, presentedSubject);
            return;
        }

        var expectedDer = File.ReadAllBytes(derPath);
        var expectedHash = ComputeSha256Hex(expectedDer);
        if (!CryptographicOperations.FixedTimeEquals(expectedDer, presentedDer))
        {
            _logger?.LogError(
                "DTLS certificate pin MISMATCH for region {Region}! Expected SHA-256={ExpectedSha256}, Presented SHA-256={PresentedSha256}, Subject={Subject}",
                region, expectedHash, presentedHash, presentedSubject);
            throw new CertificatePinMismatchException(region, expectedHash, presentedHash, presentedSubject);
        }
    }

    public static string ComputeSha256Hex(ReadOnlySpan<byte> der)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(der, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SanitizeRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            throw new ArgumentException("Region cannot be empty.", nameof(region));
        }

        var chars = region.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray();
        return new string(chars);
    }
}

internal sealed class TofuCertificateVerifier : ICertificateVerifyer
{
    private readonly string _region;
    private readonly TofuCertificatePinStore _pinStore;
    private readonly bool _acceptNewPin;

    public TofuCertificateVerifier(string region, TofuCertificatePinStore pinStore, bool acceptNewPin)
    {
        _region = region;
        _pinStore = pinStore;
        _acceptNewPin = acceptNewPin;
    }

    public bool IsValid(X509CertificateStructure[] certs)
    {
        if (certs.Length == 0)
        {
            return false;
        }

        var leaf = certs[0];
        var der = leaf.GetEncoded();
        var subject = leaf.Subject?.ToString() ?? "<unknown>";
        _pinStore.VerifyOrPin(_region, der, subject, _acceptNewPin);
        return true;
    }
}
