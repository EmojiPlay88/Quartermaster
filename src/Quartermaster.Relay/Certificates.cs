using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Quartermaster.Relay;

/// <param name="Fingerprint">SHA-256 of the certificate, upper-case hex (what clients pin).</param>
/// <param name="Created">True when a self-signed certificate was generated on this start.</param>
public sealed record RelayCertificate(X509Certificate2 Certificate, string Fingerprint, bool Created);

public static class Certificates
{
    public const string FileName = "relay-certificate.pfx";

    /// <summary>
    /// The configured certificate (.pfx/.p12, or PEM with an optional key file), else the self-signed ECDSA
    /// certificate in <see cref="RelayOptions.DataDir"/>, generated on the first run and reused afterwards.
    /// </summary>
    public static RelayCertificate LoadOrCreate(RelayOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            var cert = LoadConfigured(options.CertificatePath, options.CertificateKeyPath, options.CertificatePassword);
            if (!cert.HasPrivateKey)
                throw new InvalidOperationException($"The certificate {options.CertificatePath} has no private key; set Relay:CertificateKeyPath to the key file.");
            return new RelayCertificate(cert, Fingerprint(cert), false);
        }

        var file = Path.Combine(options.DataDir, FileName);
        bool created = false;
        if (!File.Exists(file))
        {
            try
            {
                Directory.CreateDirectory(options.DataDir);
                var tmp = file + ".tmp";
                File.WriteAllBytes(tmp, CreateSelfSigned());
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(tmp, file);
            }
            catch (UnauthorizedAccessException e)
            {
                throw new InvalidOperationException(
                    $"Cannot write the relay certificate to {options.DataDir}. Give the relay's user write access to that folder " +
                    "(Docker: the container runs as user id 1654, e.g. `sudo chown 1654 data`).", e);
            }
            created = true;
        }
        var loaded = X509CertificateLoader.LoadPkcs12FromFile(file, null);
        return new RelayCertificate(loaded, Fingerprint(loaded), created);
    }

    public static string Fingerprint(X509Certificate2 cert) => cert.GetCertHashString(HashAlgorithmName.SHA256);

    private static X509Certificate2 LoadConfigured(string path, string? keyPath, string? password)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Relay certificate not found: {path}", path);
        if (string.IsNullOrWhiteSpace(keyPath)) keyPath = null;
        if (string.IsNullOrEmpty(password)) password = null;
        if (path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".crt", StringComparison.OrdinalIgnoreCase))
        {
            using var pem = string.IsNullOrEmpty(password)
                ? X509Certificate2.CreateFromPemFile(path, keyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(path, password, keyPath);
            // Re-import so the key is usable by the TLS stack on every platform (PEM keys are ephemeral on Windows).
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }

    private static byte[] CreateSelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Quartermaster Relay", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("quartermaster-relay");
        req.CertificateExtensions.Add(san.Build());
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        return cert.Export(X509ContentType.Pkcs12);
    }
}
