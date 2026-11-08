namespace Quartermaster.Relay;

/// <summary>
/// Relay settings, bound from the <c>Relay</c> section of appsettings.json or from environment
/// variables such as <c>Relay__Port</c>, <c>Relay__CertificatePath</c>, <c>Relay__DataDir</c>.
/// </summary>
public sealed class RelayOptions
{
    public int Port { get; set; } = Quartermaster.Net.Protocol.DefaultPort;

    /// <summary>Interface to listen on: <c>any</c> (default, all interfaces), <c>loopback</c> (127.0.0.1 only) or an IP address.</summary>
    public string BindAddress { get; set; } = "any";

    /// <summary>A .pfx/.p12 file, or a PEM certificate (e.g. Let's Encrypt <c>fullchain.pem</c>). Empty = self-signed.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Password of the .pfx file (or of an encrypted PEM key).</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>PEM private key file (e.g. Let's Encrypt <c>privkey.pem</c>); only for PEM certificates.</summary>
    public string? CertificateKeyPath { get; set; }

    /// <summary>Kestrel's limit on concurrent connections (all addresses together).</summary>
    public int MaxConnections { get; set; } = 2000;

    /// <summary>Messages per second a room's host connection may send (guests: 50/s, burst 200).</summary>
    public double HostMessagesPerSecond { get; set; } = Limits.HostMessagesPerSecond;

    /// <summary>Burst size of a room's host connection.</summary>
    public double HostBurst { get; set; } = Limits.HostBurst;

    /// <summary>Folder for the generated certificate and the turn mailbox (<c>{DataDir}/mailbox</c>).</summary>
    public string DataDir { get; set; } = "/data";

    /// <summary>Most bytes the turn mailbox keeps, all boxes together (default 1 GiB). Full = new uploads refused.</summary>
    public long MailboxMaxBytes { get; set; } = 1L << 30;

    /// <summary>Most turns the mailbox keeps, all boxes together. Full = new uploads refused.</summary>
    public int MailboxMaxItems { get; set; } = 10_000;

    /// <summary>Days a turn stays in the mailbox before it is deleted.</summary>
    public int MailboxExpiryDays { get; set; } = Quartermaster.Net.MailboxLimits.ExpiryDays;
}
