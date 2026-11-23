using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetServerLogic.Abstractions;
using NetServerLogic.Connectors;
using NetServerLogic.Hosting;
using NetServerLogic.Options;

namespace NetServerLogic.Tests.Connectors;

public sealed class LiveDataverseSdkProviderTests : IDisposable
{
    private readonly string _tempDir;

    public LiveDataverseSdkProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"NetServerLogic_CertTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void NormalizeThumbprint_StripsSeparatorsAndInvisibleCharacters()
    {
        var raw = "\u200Eab:cd-ef 01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef 01\u200F";
        var normalized = DataverseCertificateLoader.NormalizeThumbprint(raw);
        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF01", normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ZZZZ")]
    [InlineData("ABC")]
    public void NormalizeThumbprint_RejectsInvalidValues(string invalid)
    {
        Assert.Throws<InvalidOperationException>(() => DataverseCertificateLoader.NormalizeThumbprint(invalid));
    }

    [Fact]
    public void CertificateLoader_Instance_WithPrivateKey_ReturnsUnownedCertificate()
    {
        using var cert = CreateSelfSignedCertificate();
        var loaded = DataverseCertificateLoader.Load(
            new DataverseCertificateOptions { Instance = cert },
            _tempDir);

        Assert.Same(cert, loaded.Certificate);
        Assert.False(loaded.OwnsCertificate);
        Assert.True(loaded.Certificate.HasPrivateKey);
    }

    [Fact]
    public void CertificateLoader_Instance_WithoutPrivateKey_ThrowsWithoutDisposingCallerCert()
    {
        using var certWithKey = CreateSelfSignedCertificate();
        using var publicOnly = X509CertificateLoader.LoadCertificate(certWithKey.RawData);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DataverseCertificateLoader.Load(
                new DataverseCertificateOptions { Instance = publicOnly },
                _tempDir));

        Assert.Contains("private key", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Caller's certificate should still be usable (not disposed).
        Assert.Equal(certWithKey.Thumbprint, publicOnly.Thumbprint);
    }

    [Fact]
    public void CertificateLoader_PfxFile_UnencryptedAndEncrypted_ResolvesRelativePath()
    {
        using var sourceCert = CreateSelfSignedCertificate();

        var unencryptedFile = Path.Combine(_tempDir, "unencrypted.pfx");
        File.WriteAllBytes(unencryptedFile, sourceCert.Export(X509ContentType.Pkcs12));

        var encryptedFile = Path.Combine(_tempDir, "encrypted.pfx");
        File.WriteAllBytes(encryptedFile, sourceCert.Export(X509ContentType.Pkcs12, "p@ssword!"));

        var loadedUnencrypted = DataverseCertificateLoader.Load(
            new DataverseCertificateOptions { Path = "unencrypted.pfx" },
            _tempDir);
        using (loadedUnencrypted.Certificate)
        {
            Assert.True(loadedUnencrypted.OwnsCertificate);
            Assert.True(loadedUnencrypted.Certificate.HasPrivateKey);
            Assert.Equal(sourceCert.Thumbprint, loadedUnencrypted.Certificate.Thumbprint);
        }

        var loadedEncrypted = DataverseCertificateLoader.Load(
            new DataverseCertificateOptions { Path = "encrypted.pfx", Password = "p@ssword!" },
            _tempDir);
        using (loadedEncrypted.Certificate)
        {
            Assert.True(loadedEncrypted.OwnsCertificate);
            Assert.True(loadedEncrypted.Certificate.HasPrivateKey);
            Assert.Equal(sourceCert.Thumbprint, loadedEncrypted.Certificate.Thumbprint);
        }

        Assert.ThrowsAny<CryptographicException>(() =>
            DataverseCertificateLoader.Load(
                new DataverseCertificateOptions { Path = "encrypted.pfx", Password = "wrong-password" },
                _tempDir));
    }

    [Fact]
    public void CertificateLoader_PemFiles_CombinedAndSeparateAndEncrypted_LoadAndSign()
    {
        using var sourceCert = CreateSelfSignedCertificate();
        using var rsa = sourceCert.GetRSAPrivateKey()!;

        var certPem = sourceCert.ExportCertificatePem();
        var keyPem = rsa.ExportPkcs8PrivateKeyPem();
        var encryptedKeyPem = rsa.ExportEncryptedPkcs8PrivateKeyPem(
            "pem-secret",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, iterationCount: 10_000));

        // 1. Combined unencrypted PEM
        File.WriteAllText(Path.Combine(_tempDir, "combined.pem"), $"{certPem}\n{keyPem}");
        var combinedLoaded = DataverseCertificateLoader.Load(
            new DataverseCertificateOptions { Path = "combined.pem" },
            _tempDir);
        using (combinedLoaded.Certificate)
        {
            Assert.True(combinedLoaded.OwnsCertificate);
            Assert.True(combinedLoaded.Certificate.HasPrivateKey);
            Assert.Equal(sourceCert.Thumbprint, combinedLoaded.Certificate.Thumbprint);
            AssertCanSignWithRsa(combinedLoaded.Certificate);
        }

        // 2. Combined encrypted PEM
        File.WriteAllText(Path.Combine(_tempDir, "combined-enc.pem"), $"{certPem}\n{encryptedKeyPem}");
        var combinedEncLoaded = DataverseCertificateLoader.Load(
            new DataverseCertificateOptions { Path = "combined-enc.pem", Password = "pem-secret" },
            _tempDir);
        using (combinedEncLoaded.Certificate)
        {
            Assert.True(combinedEncLoaded.OwnsCertificate);
            Assert.True(combinedEncLoaded.Certificate.HasPrivateKey);
            AssertCanSignWithRsa(combinedEncLoaded.Certificate);
        }

        // 3. Separate cert + unencrypted key PEM
        File.WriteAllText(Path.Combine(_tempDir, "cert.crt"), certPem);
        File.WriteAllText(Path.Combine(_tempDir, "private.key"), keyPem);
        var splitLoaded = DataverseCertificateLoader.Load(
            new DataverseCertificateOptions { Path = "cert.crt", KeyPath = "private.key" },
            _tempDir);
        using (splitLoaded.Certificate)
        {
            Assert.True(splitLoaded.OwnsCertificate);
            Assert.True(splitLoaded.Certificate.HasPrivateKey);
            AssertCanSignWithRsa(splitLoaded.Certificate);
        }

        // 4. Separate cert + encrypted key PEM
        File.WriteAllText(Path.Combine(_tempDir, "private-enc.key"), encryptedKeyPem);
        var splitEncLoaded = DataverseCertificateLoader.Load(
            new DataverseCertificateOptions
            {
                Path = "cert.crt",
                KeyPath = "private-enc.key",
                Password = "pem-secret"
            },
            _tempDir);
        using (splitEncLoaded.Certificate)
        {
            Assert.True(splitEncLoaded.OwnsCertificate);
            Assert.True(splitEncLoaded.Certificate.HasPrivateKey);
            AssertCanSignWithRsa(splitEncLoaded.Certificate);
        }

        // 5. Public-only PEM without key fails cleanly
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DataverseCertificateLoader.Load(
                new DataverseCertificateOptions { Path = "cert.crt" },
                _tempDir));
        Assert.Contains("private key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CertificateLoader_MissingFileOrKeyFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            DataverseCertificateLoader.Load(
                new DataverseCertificateOptions { Path = "does-not-exist.pfx" },
                _tempDir));

        var certPath = Path.Combine(_tempDir, "existing.pem");
        File.WriteAllText(certPath, "placeholder");

        Assert.Throws<FileNotFoundException>(() =>
            DataverseCertificateLoader.Load(
                new DataverseCertificateOptions { Path = "existing.pem", KeyPath = "missing.key" },
                _tempDir));
    }

    [Fact]
    public void CertificateLoader_Base64_PfxAndCombinedPemAndInvalidInput()
    {
        using var sourceCert = CreateSelfSignedCertificate();
        using var rsa = sourceCert.GetRSAPrivateKey()!;

        // 1. Base64 password-protected PFX
        var pfxBase64 = Convert.ToBase64String(sourceCert.Export(X509ContentType.Pkcs12, "b64-secret"));
        var loadedPfx = DataverseCertificateLoader.Load(
            new DataverseCertificateOptions { Base64 = pfxBase64, Password = "b64-secret" },
            _tempDir);
        using (loadedPfx.Certificate)
        {
            Assert.True(loadedPfx.OwnsCertificate);
            Assert.True(loadedPfx.Certificate.HasPrivateKey);
            Assert.Equal(sourceCert.Thumbprint, loadedPfx.Certificate.Thumbprint);
        }

        // 2. Base64 combined PEM
        var combinedPem = $"{sourceCert.ExportCertificatePem()}\n{rsa.ExportPkcs8PrivateKeyPem()}";
        var pemBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(combinedPem));
        var loadedPem = DataverseCertificateLoader.Load(
            new DataverseCertificateOptions { Base64 = pemBase64 },
            _tempDir);
        using (loadedPem.Certificate)
        {
            Assert.True(loadedPem.OwnsCertificate);
            Assert.True(loadedPem.Certificate.HasPrivateKey);
            Assert.Equal(sourceCert.Thumbprint, loadedPem.Certificate.Thumbprint);
        }

        // 3. Invalid Base64
        Assert.Throws<InvalidOperationException>(() =>
            DataverseCertificateLoader.Load(
                new DataverseCertificateOptions { Base64 = "not-valid-base64!!!" },
                _tempDir));

        // 4. Base64 public-only PEM
        var publicPemBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sourceCert.ExportCertificatePem()));
        var publicPemEx = Assert.Throws<InvalidOperationException>(() =>
            DataverseCertificateLoader.Load(
                new DataverseCertificateOptions { Base64 = publicPemBase64 },
                _tempDir));
        Assert.Contains("private key", publicPemEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CertificateLoader_StoreLookup_MissingThumbprint_ReportsStoreDetails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DataverseCertificateLoader.Load(
                new DataverseCertificateOptions
                {
                    Thumbprint = "00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff:00:11:22:33",
                    StoreName = StoreName.My,
                    StoreLocation = StoreLocation.CurrentUser
                },
                _tempDir));

        Assert.Contains("00112233445566778899AABBCCDDEEFF00112233", ex.Message);
        Assert.Contains("CurrentUser\\My", ex.Message);
    }

    [Fact]
    public void CertificateLoader_MultipleSourcesOrOrphanKeyPath_ThrowsInvalidOperation()
    {
        using var cert = CreateSelfSignedCertificate();

        var multiEx = Assert.Throws<InvalidOperationException>(() =>
            DataverseCertificateLoader.Load(
                new DataverseCertificateOptions
                {
                    Instance = cert,
                    Thumbprint = "00112233445566778899AABBCCDDEEFF00112233"
                },
                _tempDir));
        Assert.Contains("only one", multiEx.Message, StringComparison.OrdinalIgnoreCase);

        var keyPathEx = Assert.Throws<InvalidOperationException>(() =>
            DataverseCertificateLoader.Load(
                new DataverseCertificateOptions
                {
                    Instance = cert,
                    KeyPath = "private.key"
                },
                _tempDir));
        Assert.Contains("KeyPath", keyPathEx.Message);
    }

    [Fact]
    public void ResolveClientConfiguration_ValidatesPrecedenceAndRequiredFields()
    {
        using var cert = CreateSelfSignedCertificate();

        // Empty options -> throws
        Assert.Throws<InvalidOperationException>(() =>
            LiveDataverseSdkProvider.ResolveClientConfiguration(new DataverseOptions(), _tempDir));

        // Empty Certificate placeholder with ConnectionString -> uses ConnectionString cleanly
        using var connStringConfig = LiveDataverseSdkProvider.ResolveClientConfiguration(
            new DataverseOptions
            {
                ConnectionString = "AuthType=ClientSecret;Url=https://org.crm.dynamics.com;ClientId=123;ClientSecret=abc",
                Certificate = new DataverseCertificateOptions
                {
                    StoreName = StoreName.My,
                    StoreLocation = StoreLocation.CurrentUser
                }
            },
            _tempDir);
        Assert.False(connStringConfig.IsCertificateAuth);
        Assert.StartsWith("AuthType=ClientSecret;", connStringConfig.ConnectionString);

        // Both ConnectionString and populated Certificate -> throws
        Assert.Throws<InvalidOperationException>(() =>
            LiveDataverseSdkProvider.ResolveClientConfiguration(
                new DataverseOptions
                {
                    ConnectionString = "AuthType=ClientSecret;Url=https://org.crm.dynamics.com",
                    Url = "https://org.crm.dynamics.com",
                    ClientId = "11111111-1111-1111-1111-111111111111",
                    Certificate = new DataverseCertificateOptions { Instance = cert }
                },
                _tempDir));

        // Missing Url -> throws
        Assert.Throws<InvalidOperationException>(() =>
            LiveDataverseSdkProvider.ResolveClientConfiguration(
                new DataverseOptions
                {
                    ClientId = "11111111-1111-1111-1111-111111111111",
                    Certificate = new DataverseCertificateOptions { Instance = cert }
                },
                _tempDir));

        // Non-HTTP(S) Url -> throws
        Assert.Throws<InvalidOperationException>(() =>
            LiveDataverseSdkProvider.ResolveClientConfiguration(
                new DataverseOptions
                {
                    Url = "ftp://org.crm.dynamics.com",
                    ClientId = "11111111-1111-1111-1111-111111111111",
                    Certificate = new DataverseCertificateOptions { Instance = cert }
                },
                _tempDir));

        // Missing ClientId -> throws
        Assert.Throws<InvalidOperationException>(() =>
            LiveDataverseSdkProvider.ResolveClientConfiguration(
                new DataverseOptions
                {
                    Url = "https://org.crm.dynamics.com",
                    Certificate = new DataverseCertificateOptions { Instance = cert }
                },
                _tempDir));

        // Invalid RedirectUri -> throws
        Assert.Throws<InvalidOperationException>(() =>
            LiveDataverseSdkProvider.ResolveClientConfiguration(
                new DataverseOptions
                {
                    Url = "https://org.crm.dynamics.com",
                    ClientId = "11111111-1111-1111-1111-111111111111",
                    RedirectUri = "not a uri",
                    Certificate = new DataverseCertificateOptions { Instance = cert }
                },
                _tempDir));

        // Valid certificate configuration with default RedirectUri
        using var certConfig = LiveDataverseSdkProvider.ResolveClientConfiguration(
            new DataverseOptions
            {
                Url = "https://org.crm.dynamics.com",
                ClientId = " 11111111-1111-1111-1111-111111111111 ",
                UseUniqueInstance = false,
                Certificate = new DataverseCertificateOptions
                {
                    Instance = cert,
                    StoreName = StoreName.Root
                }
            },
            _tempDir);

        Assert.True(certConfig.IsCertificateAuth);
        Assert.Equal(new Uri("https://org.crm.dynamics.com"), certConfig.InstanceUrl);
        Assert.Equal("11111111-1111-1111-1111-111111111111", certConfig.ClientId);
        Assert.Equal(LiveDataverseSdkProvider.DefaultCertificateRedirectUri, certConfig.RedirectUri);
        Assert.False(certConfig.UseUniqueInstance);
        Assert.Equal(StoreName.Root, certConfig.StoreName);
        Assert.Same(cert, certConfig.Certificate);
        Assert.False(certConfig.OwnsCertificate);
    }

    [Fact]
    public void ConfigurationBinding_BindsAllDataverseCertificateOptions()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PowerPagesServerLogic:DataverseMode"] = "Live",
                ["PowerPagesServerLogic:Dataverse:Url"] = "https://contoso.crm.dynamics.com",
                ["PowerPagesServerLogic:Dataverse:ClientId"] = "22222222-2222-2222-2222-222222222222",
                ["PowerPagesServerLogic:Dataverse:RedirectUri"] = "http://localhost:5000",
                ["PowerPagesServerLogic:Dataverse:UseUniqueInstance"] = "false",
                ["PowerPagesServerLogic:Dataverse:Certificate:Thumbprint"] = "ABCDEF0123456789ABCDEF0123456789ABCDEF01",
                ["PowerPagesServerLogic:Dataverse:Certificate:StoreName"] = "My",
                ["PowerPagesServerLogic:Dataverse:Certificate:StoreLocation"] = "LocalMachine",
                ["PowerPagesServerLogic:Dataverse:Certificate:ValidOnly"] = "true",
                ["PowerPagesServerLogic:Dataverse:Certificate:KeyStorageFlags"] = "MachineKeySet"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(_tempDir));
        services.AddPowerPagesServerLogicEmulator(config);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ServerLogicEmulatorOptions>>().Value;

        Assert.Equal(DataverseMode.Live, options.DataverseMode);
        Assert.Equal("https://contoso.crm.dynamics.com", options.Dataverse.Url);
        Assert.Equal("22222222-2222-2222-2222-222222222222", options.Dataverse.ClientId);
        Assert.Equal("http://localhost:5000", options.Dataverse.RedirectUri);
        Assert.False(options.Dataverse.UseUniqueInstance);
        Assert.NotNull(options.Dataverse.Certificate);
        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF01", options.Dataverse.Certificate!.Thumbprint);
        Assert.Equal(StoreName.My, options.Dataverse.Certificate.StoreName);
        Assert.Equal(StoreLocation.LocalMachine, options.Dataverse.Certificate.StoreLocation);
        Assert.True(options.Dataverse.Certificate.ValidOnly);
        Assert.Equal(X509KeyStorageFlags.MachineKeySet, options.Dataverse.Certificate.KeyStorageFlags);

        // Verify LiveDataverseSdkProvider resolves from DI cleanly without ServiceClient registered.
        var dataverseProvider = provider.GetRequiredService<IServerLogicDataverseProvider>();
        Assert.IsType<LiveDataverseSdkProvider>(dataverseProvider);
    }

    [Fact]
    public void Builder_UseDataverseCertificate_ConfiguresOptionsCorrectly()
    {
        using var cert = CreateSelfSignedCertificate();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPowerPagesServerLogicEmulator(
            opts => opts.DataverseMode = DataverseMode.Live,
            builder => builder.UseDataverseCertificate(
                "https://contoso.crm.dynamics.com",
                "33333333-3333-3333-3333-333333333333",
                cert,
                redirectUri: "http://localhost:8080",
                useUniqueInstance: false));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ServerLogicEmulatorOptions>>().Value;

        Assert.Equal("https://contoso.crm.dynamics.com", options.Dataverse.Url);
        Assert.Equal("33333333-3333-3333-3333-333333333333", options.Dataverse.ClientId);
        Assert.Equal("http://localhost:8080", options.Dataverse.RedirectUri);
        Assert.False(options.Dataverse.UseUniqueInstance);
        Assert.Same(cert, options.Dataverse.Certificate?.Instance);
    }

    [Fact]
    public void LiveDataverseSdkProvider_ClientFactoryFailure_DisposesOwnedCertButNotCallerInstance()
    {
        using var sourceCert = CreateSelfSignedCertificate();
        var pfxPath = Path.Combine(_tempDir, "owned.pfx");
        File.WriteAllBytes(pfxPath, sourceCert.Export(X509ContentType.Pkcs12));

        X509Certificate2? capturedOwnedCert = null;
        using var ownedProvider = new LiveDataverseSdkProvider(
            Microsoft.Extensions.Options.Options.Create(new ServerLogicEmulatorOptions
            {
                DataverseMode = DataverseMode.Live,
                Dataverse = new DataverseOptions
                {
                    Url = "https://org.crm.dynamics.com",
                    ClientId = "11111111-1111-1111-1111-111111111111",
                    Certificate = new DataverseCertificateOptions { Path = "owned.pfx" }
                }
            }),
            NullLogger<LiveDataverseSdkProvider>.Instance,
            serviceClient: null,
            hostEnvironment: new FakeHostEnvironment(_tempDir),
            clientFactory: (cfg, _) =>
            {
                capturedOwnedCert = cfg.Certificate;
                throw new InvalidOperationException("Simulated connection failure");
            });

        Assert.Throws<InvalidOperationException>(() => ownedProvider.RetrieveMultipleRecords("accounts"));
        Assert.NotNull(capturedOwnedCert);
        // Accessing Thumbprint on a disposed X509Certificate2 throws CryptographicException.
        Assert.ThrowsAny<CryptographicException>(() => _ = capturedOwnedCert!.Thumbprint);

        // Caller-provided Instance must NOT be disposed on factory failure.
        using var instanceProvider = new LiveDataverseSdkProvider(
            Microsoft.Extensions.Options.Options.Create(new ServerLogicEmulatorOptions
            {
                DataverseMode = DataverseMode.Live,
                Dataverse = new DataverseOptions
                {
                    Url = "https://org.crm.dynamics.com",
                    ClientId = "11111111-1111-1111-1111-111111111111",
                    Certificate = new DataverseCertificateOptions { Instance = sourceCert }
                }
            }),
            NullLogger<LiveDataverseSdkProvider>.Instance,
            serviceClient: null,
            hostEnvironment: new FakeHostEnvironment(_tempDir),
            clientFactory: (_, _) => throw new InvalidOperationException("Simulated connection failure"));

        Assert.Throws<InvalidOperationException>(() => instanceProvider.RetrieveMultipleRecords("accounts"));
        Assert.Equal(sourceCert.Thumbprint, sourceCert.Thumbprint);
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=NetServerLogic-Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
        // Re-load via PKCS#12 so Export(Pkcs12) and private key access behave identically across platforms.
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.DefaultKeySet);
    }

    private static void AssertCanSignWithRsa(X509Certificate2 cert)
    {
        using var rsa = cert.GetRSAPrivateKey();
        Assert.NotNull(rsa);
        var signature = rsa!.SignData("dataverse-assertion"u8.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.NotEmpty(signature);
    }

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "NetServerLogic.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
