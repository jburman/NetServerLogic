using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NetServerLogic.Options;

namespace NetServerLogic.Connectors;

internal readonly record struct LoadedCertificate(X509Certificate2 Certificate, bool OwnsCertificate);

/// <summary>
/// Loads X.509 certificates for live Dataverse certificate-based authentication from an in-memory instance,
/// OS certificate store, file (<c>.pfx</c>/<c>.p12</c>/<c>.pem</c>), or base64-encoded payload.
/// </summary>
internal static class DataverseCertificateLoader
{
    private const string PemHeaderPrefix = "-----BEGIN ";

    public static LoadedCertificate Load(DataverseCertificateOptions options, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configuredSources = new List<string>(4);
        if (options.Instance is not null)
        {
            configuredSources.Add(nameof(DataverseCertificateOptions.Instance));
        }

        if (!string.IsNullOrWhiteSpace(options.Thumbprint))
        {
            configuredSources.Add(nameof(DataverseCertificateOptions.Thumbprint));
        }

        if (!string.IsNullOrWhiteSpace(options.Path))
        {
            configuredSources.Add(nameof(DataverseCertificateOptions.Path));
        }

        if (!string.IsNullOrWhiteSpace(options.Base64))
        {
            configuredSources.Add(nameof(DataverseCertificateOptions.Base64));
        }

        if (configuredSources.Count == 0)
        {
            throw new InvalidOperationException(
                "Dataverse certificate authentication requires one certificate source: " +
                "Thumbprint, Path, Base64, or Instance.");
        }

        if (configuredSources.Count > 1)
        {
            throw new InvalidOperationException(
                $"Configure only one Dataverse certificate source, not multiple ({string.Join(", ", configuredSources)}).");
        }

        if (!string.IsNullOrWhiteSpace(options.KeyPath) && string.IsNullOrWhiteSpace(options.Path))
        {
            throw new InvalidOperationException(
                "PowerPagesServerLogic:Dataverse:Certificate:KeyPath can only be used together with Path.");
        }

        if (options.Instance is not null)
        {
            EnsurePrivateKey(options.Instance, ownsCertificate: false, "the provided X509Certificate2 instance");
            return new LoadedCertificate(options.Instance, OwnsCertificate: false);
        }

        if (!string.IsNullOrWhiteSpace(options.Thumbprint))
        {
            var storeCert = LoadFromStore(
                options.Thumbprint,
                options.StoreName,
                options.StoreLocation,
                options.ValidOnly);
            return new LoadedCertificate(storeCert, OwnsCertificate: true);
        }

        if (!string.IsNullOrWhiteSpace(options.Path))
        {
            var fileCert = LoadFromFile(
                options.Path,
                options.KeyPath,
                options.Password,
                options.KeyStorageFlags,
                contentRootPath);
            return new LoadedCertificate(fileCert, OwnsCertificate: true);
        }

        var base64Cert = LoadFromBase64(options.Base64!, options.Password, options.KeyStorageFlags);
        return new LoadedCertificate(base64Cert, OwnsCertificate: true);
    }

    internal static string NormalizeThumbprint(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            throw new InvalidOperationException("Dataverse certificate thumbprint must not be empty.");
        }

        var builder = new StringBuilder(thumbprint.Length);
        foreach (var ch in thumbprint)
        {
            if (char.IsWhiteSpace(ch) || ch is ':' or '-')
            {
                continue;
            }

            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.Format or UnicodeCategory.Control)
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(ch))
            {
                throw new InvalidOperationException(
                    "Dataverse certificate thumbprint contains invalid characters; expected hexadecimal digits.");
            }

            builder.Append(char.ToUpperInvariant(ch));
        }

        if (builder.Length == 0 || builder.Length % 2 != 0)
        {
            throw new InvalidOperationException(
                "Dataverse certificate thumbprint must contain an even number of hexadecimal digits.");
        }

        return builder.ToString();
    }

    private static X509Certificate2 LoadFromStore(
        string thumbprint,
        StoreName storeName,
        StoreLocation storeLocation,
        bool validOnly)
    {
        var normalized = NormalizeThumbprint(thumbprint);

        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly);

        var allCertificates = store.Certificates;
        X509Certificate2Collection? matches = null;
        X509Certificate2? selected = null;

        try
        {
            matches = allCertificates.Find(X509FindType.FindByThumbprint, normalized, validOnly);
            if (matches.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No certificate with thumbprint '{normalized}' was found in certificate store " +
                    $"'{storeLocation}\\{storeName}' (ValidOnly={validOnly}).");
            }

            selected = matches.FirstOrDefault(c => c.HasPrivateKey) ?? matches[0];
            EnsurePrivateKey(
                selected,
                ownsCertificate: false,
                $"certificate '{normalized}' in store '{storeLocation}\\{storeName}'");

            return selected;
        }
        catch
        {
            selected?.Dispose();
            throw;
        }
        finally
        {
            if (matches is not null)
            {
                foreach (var cert in matches)
                {
                    if (!ReferenceEquals(cert, selected))
                    {
                        cert.Dispose();
                    }
                }
            }

            foreach (var cert in allCertificates)
            {
                if (!ReferenceEquals(cert, selected))
                {
                    cert.Dispose();
                }
            }
        }
    }

    private static X509Certificate2 LoadFromFile(
        string path,
        string? keyPath,
        string? password,
        X509KeyStorageFlags? keyStorageFlags,
        string contentRootPath)
    {
        var resolvedPath = ResolvePath(path, contentRootPath);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException(
                $"Dataverse certificate file was not found at '{resolvedPath}'.",
                resolvedPath);
        }

        string? resolvedKeyPath = null;
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            resolvedKeyPath = ResolvePath(keyPath, contentRootPath);
            if (!File.Exists(resolvedKeyPath))
            {
                throw new FileNotFoundException(
                    $"Dataverse certificate private key file was not found at '{resolvedKeyPath}'.",
                    resolvedKeyPath);
            }
        }

        X509Certificate2 cert;
        if (resolvedKeyPath is not null || IsPemFile(resolvedPath))
        {
            if (resolvedKeyPath is null && !HasPemPrivateKey(File.ReadAllText(resolvedPath)))
            {
                throw new InvalidOperationException(
                    $"Dataverse certificate authentication requires a private key, but certificate file '{resolvedPath}' " +
                    "does not contain a PEM private key block. Provide a combined PEM file or set " +
                    "PowerPagesServerLogic:Dataverse:Certificate:KeyPath.");
            }

            cert = string.IsNullOrEmpty(password)
                ? X509Certificate2.CreateFromPemFile(resolvedPath, resolvedKeyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(resolvedPath, password, resolvedKeyPath);

            cert = ApplyKeyStorageFlagsIfRequested(cert, keyStorageFlags);
        }
        else
        {
            cert = X509CertificateLoader.LoadPkcs12FromFile(
                resolvedPath,
                password,
                keyStorageFlags ?? X509KeyStorageFlags.DefaultKeySet);
        }

        EnsurePrivateKey(cert, ownsCertificate: true, $"certificate file '{resolvedPath}'");
        return cert;
    }

    private static X509Certificate2 LoadFromBase64(
        string base64,
        string? password,
        X509KeyStorageFlags? keyStorageFlags)
    {
        byte[] rawBytes;
        try
        {
            rawBytes = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "PowerPagesServerLogic:Dataverse:Certificate:Base64 is not a valid base64 string.",
                ex);
        }

        if (rawBytes.Length == 0)
        {
            throw new InvalidOperationException(
                "PowerPagesServerLogic:Dataverse:Certificate:Base64 decoded to an empty byte array.");
        }

        X509Certificate2 cert;
        if (StartsWithPemHeader(rawBytes))
        {
            var pemText = Encoding.UTF8.GetString(rawBytes);
            if (!HasPemPrivateKey(pemText))
            {
                throw new InvalidOperationException(
                    "Dataverse certificate authentication requires a private key, but the base64-encoded PEM " +
                    "does not contain a private key block.");
            }

            cert = string.IsNullOrEmpty(password)
                ? X509Certificate2.CreateFromPem(pemText, pemText)
                : X509Certificate2.CreateFromEncryptedPem(pemText, pemText, password);

            cert = ApplyKeyStorageFlagsIfRequested(cert, keyStorageFlags);
        }
        else
        {
            cert = X509CertificateLoader.LoadPkcs12(
                rawBytes,
                password,
                keyStorageFlags ?? X509KeyStorageFlags.DefaultKeySet);
        }

        EnsurePrivateKey(cert, ownsCertificate: true, "base64-encoded certificate");
        return cert;
    }

    private static X509Certificate2 ApplyKeyStorageFlagsIfRequested(
        X509Certificate2 cert,
        X509KeyStorageFlags? keyStorageFlags)
    {
        if (!keyStorageFlags.HasValue || !cert.HasPrivateKey)
        {
            return cert;
        }

        using (cert)
        {
            var pkcs12Bytes = cert.Export(X509ContentType.Pkcs12);
            return X509CertificateLoader.LoadPkcs12(pkcs12Bytes, password: null, keyStorageFlags.Value);
        }
    }

    private static void EnsurePrivateKey(X509Certificate2 cert, bool ownsCertificate, string sourceDescription)
    {
        if (cert.HasPrivateKey)
        {
            return;
        }

        if (ownsCertificate)
        {
            cert.Dispose();
        }

        throw new InvalidOperationException(
            $"Dataverse certificate authentication requires a private key, but {sourceDescription} does not contain an accessible private key.");
    }

    private static string ResolvePath(string path, string contentRootPath)
    {
        var trimmed = path.Trim();
        return Path.GetFullPath(
            Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(contentRootPath, trimmed));
    }

    private static bool IsPemFile(string resolvedPath)
    {
        var extension = Path.GetExtension(resolvedPath);
        if (extension.Equals(".pem", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".crt", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cer", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var headerBuffer = new byte[64];
        using var stream = File.OpenRead(resolvedPath);
        var bytesRead = stream.Read(headerBuffer, 0, headerBuffer.Length);
        return bytesRead > 0 && StartsWithPemHeader(headerBuffer.AsSpan(0, bytesRead));
    }

    private static bool StartsWithPemHeader(ReadOnlySpan<byte> bytes)
    {
        var trimmed = bytes;
        // Skip optional UTF-8 BOM
        if (trimmed.Length >= 3 && trimmed[0] == 0xEF && trimmed[1] == 0xBB && trimmed[2] == 0xBF)
        {
            trimmed = trimmed[3..];
        }

        // Skip leading ASCII whitespace
        var index = 0;
        while (index < trimmed.Length && (trimmed[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
        {
            index++;
        }

        trimmed = trimmed[index..];
        return trimmed.StartsWith(Encoding.ASCII.GetBytes(PemHeaderPrefix));
    }

    private static bool HasPemPrivateKey(ReadOnlySpan<char> pemText)
    {
        var remaining = pemText;
        while (PemEncoding.TryFind(remaining, out var fields))
        {
            var label = remaining[fields.Label];
            if (label.Equals("PRIVATE KEY", StringComparison.Ordinal) ||
                label.Equals("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal) ||
                label.Equals("RSA PRIVATE KEY", StringComparison.Ordinal) ||
                label.Equals("EC PRIVATE KEY", StringComparison.Ordinal))
            {
                return true;
            }

            remaining = remaining[fields.Location.End..];
        }

        return false;
    }
}
