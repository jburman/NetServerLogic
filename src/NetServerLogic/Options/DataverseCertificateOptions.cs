using System.Security.Cryptography.X509Certificates;

namespace NetServerLogic.Options;

/// <summary>
/// Certificate-based authentication settings for connecting to live Dataverse.
/// Configure exactly one certificate source: <see cref="Thumbprint"/>, <see cref="Path"/>,
/// <see cref="Base64"/>, or <see cref="Instance"/>.
/// </summary>
public sealed class DataverseCertificateOptions
{
    /// <summary>
    /// Thumbprint of a certificate installed in the OS X.509 certificate store.
    /// Whitespace, colons, hyphens, and invisible formatting characters are ignored.
    /// </summary>
    public string? Thumbprint { get; set; }

    /// <summary>
    /// X.509 certificate store name when loading by <see cref="Thumbprint"/>.
    /// Defaults to <see cref="StoreName.My"/>.
    /// </summary>
    public StoreName StoreName { get; set; } = StoreName.My;

    /// <summary>
    /// X.509 certificate store location when loading by <see cref="Thumbprint"/>.
    /// Defaults to <see cref="StoreLocation.CurrentUser"/>.
    /// </summary>
    public StoreLocation StoreLocation { get; set; } = StoreLocation.CurrentUser;

    /// <summary>
    /// Whether certificate store lookup requires the certificate chain to be trusted and valid.
    /// Defaults to <see langword="false"/> so self-signed app-registration certificates work out of the box.
    /// </summary>
    public bool ValidOnly { get; set; }

    /// <summary>
    /// Path to a PKCS#12 (<c>.pfx</c>/<c>.p12</c>) or PEM (<c>.pem</c>/<c>.crt</c>) certificate file
    /// containing (or paired via <see cref="KeyPath"/> with) a private key.
    /// Relative paths are resolved against the host content root.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Optional path to a separate PEM private key file when <see cref="Path"/> points to a PEM certificate.
    /// Relative paths are resolved against the host content root.
    /// </summary>
    public string? KeyPath { get; set; }

    /// <summary>
    /// Optional base64-encoded PKCS#12 (<c>.pfx</c>/<c>.p12</c>) or combined PEM certificate bytes.
    /// Useful for User Secrets, environment variables, or container secrets.
    /// </summary>
    public string? Base64 { get; set; }

    /// <summary>
    /// Optional password for an encrypted PKCS#12 file/base64 payload or encrypted PEM private key.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Optional key storage flags used when importing PKCS#12 certificates.
    /// Defaults to <see cref="X509KeyStorageFlags.DefaultKeySet"/>.
    /// </summary>
    public X509KeyStorageFlags? KeyStorageFlags { get; set; }

    /// <summary>
    /// Optional pre-loaded <see cref="X509Certificate2"/> instance for programmatic configuration.
    /// The caller retains ownership and disposal responsibility for this instance.
    /// </summary>
    public X509Certificate2? Instance { get; set; }

    internal bool HasCertificateSource() =>
        !string.IsNullOrWhiteSpace(Thumbprint) ||
        !string.IsNullOrWhiteSpace(Path) ||
        !string.IsNullOrWhiteSpace(Base64) ||
        Instance is not null;
}
