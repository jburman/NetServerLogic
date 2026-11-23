using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.PowerPlatform.Dataverse.Client;
using NetServerLogic.Abstractions;
using NetServerLogic.Options;

namespace NetServerLogic.Connectors;

internal sealed class DataverseClientConfiguration : IDisposable
{
    public string? ConnectionString { get; init; }

    public Uri? InstanceUrl { get; init; }

    public string? ClientId { get; init; }

    public Uri? RedirectUri { get; init; }

    public bool UseUniqueInstance { get; init; }

    public StoreName StoreName { get; init; } = StoreName.My;

    public X509Certificate2? Certificate { get; init; }

    public bool OwnsCertificate { get; init; }

    public bool IsCertificateAuth => Certificate is not null;

    public void Dispose()
    {
        if (OwnsCertificate)
        {
            Certificate?.Dispose();
        }
    }
}

/// <summary>
/// Live Dataverse Web API v9.2 bridge via <see cref="ServiceClient.ExecuteWebRequest"/>.
/// </summary>
public sealed partial class LiveDataverseSdkProvider : IServerLogicDataverseProvider, IDisposable
{
    internal static readonly Uri DefaultCertificateRedirectUri = new("http://localhost");

    private readonly ServerLogicEmulatorOptions _options;
    private readonly ILogger<LiveDataverseSdkProvider> _logger;
    private readonly string _contentRootPath;
    private readonly Func<DataverseClientConfiguration, ILogger, ServiceClient> _clientFactory;
    private readonly object _syncRoot = new();
    private ServiceClient? _client;
    private X509Certificate2? _ownedCertificate;
    private bool _ownsClient;
    private bool _disposed;

    public LiveDataverseSdkProvider(
        IOptions<ServerLogicEmulatorOptions> options,
        ILogger<LiveDataverseSdkProvider> logger,
        ServiceClient? serviceClient = null,
        IHostEnvironment? hostEnvironment = null)
        : this(options, logger, serviceClient, hostEnvironment, CreateServiceClient)
    {
    }

    internal LiveDataverseSdkProvider(
        IOptions<ServerLogicEmulatorOptions> options,
        ILogger<LiveDataverseSdkProvider> logger,
        ServiceClient? serviceClient,
        IHostEnvironment? hostEnvironment,
        Func<DataverseClientConfiguration, ILogger, ServiceClient> clientFactory)
    {
        _options = options.Value;
        _logger = logger;
        _client = serviceClient;
        _ownsClient = false;
        _contentRootPath = string.IsNullOrWhiteSpace(hostEnvironment?.ContentRootPath)
            ? Directory.GetCurrentDirectory()
            : hostEnvironment.ContentRootPath;
        _clientFactory = clientFactory;
    }

    public string RetrieveRecord(string entitySetName, string id, string? options = null, bool skipCache = false)
    {
        var queryPath = BuildQueryPath(entitySetName, id, options);
        using var response = ExecuteRequest(HttpMethod.Get, queryPath, body: string.Empty, includeReturnRepresentation: false);
        return ReadResponseOrThrow(response);
    }

    public string RetrieveMultipleRecords(string entitySetName, string? options = null, bool skipCache = false)
    {
        var queryPath = BuildQueryPath(entitySetName, id: null, options);
        using var response = ExecuteRequest(HttpMethod.Get, queryPath, body: string.Empty, includeReturnRepresentation: false);
        return ReadResponseOrThrow(response);
    }

    public string CreateRecord(string entitySetName, string jsonPayload)
    {
        var queryPath = BuildQueryPath(entitySetName);
        var returnRep = _options.Dataverse.CreateRecordReturnMode == CreateRecordReturnMode.FullRepresentation;
        using var response = ExecuteRequest(HttpMethod.Post, queryPath, jsonPayload, returnRep);
        var body = ReadResponseOrThrow(response);

        if (_options.Dataverse.CreateRecordReturnMode == CreateRecordReturnMode.Empty)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        var idJson = ExtractEntityIdJson(response);
        return idJson ?? string.Empty;
    }

    public string UpdateRecord(string entitySetName, string id, string jsonPayload)
    {
        var queryPath = BuildQueryPath(entitySetName, id);
        using var response = ExecuteRequest(HttpMethod.Patch, queryPath, jsonPayload, includeReturnRepresentation: false);
        var body = ReadResponseOrThrow(response);
        return response.StatusCode == HttpStatusCode.NoContent ? string.Empty : body;
    }

    public string DeleteRecord(string entitySetName, string id)
    {
        var queryPath = BuildQueryPath(entitySetName, id);
        using var response = ExecuteRequest(HttpMethod.Delete, queryPath, body: string.Empty, includeReturnRepresentation: false);
        _ = ReadResponseOrThrow(response);
        return string.Empty;
    }

    public string InvokeCustomApi(string httpMethod, string url, string? payload = null)
    {
        var method = HttpMethod.Parse((httpMethod ?? "POST").Trim().ToUpperInvariant());
        var cleanUrl = (url ?? string.Empty).Trim().TrimStart('/');
        using var response = ExecuteRequest(method, cleanUrl, payload ?? string.Empty, includeReturnRepresentation: false);
        return ReadResponseOrThrow(response);
    }

    private HttpResponseMessage ExecuteRequest(
        HttpMethod method,
        string queryString,
        string body,
        bool includeReturnRepresentation)
    {
        var client = GetOrCreateClient();
        var headers = BuildHeaders(includeReturnRepresentation);
        _logger.LogDebug("Executing live Dataverse {Method} {Path}", method, queryString);
        return client.ExecuteWebRequest(method, queryString, body, headers, "application/json");
    }

    private ServiceClient GetOrCreateClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_client is not null)
        {
            return _client;
        }

        lock (_syncRoot)
        {
            if (_client is not null)
            {
                return _client;
            }

            var config = ResolveClientConfiguration(_options.Dataverse, _contentRootPath);
            ServiceClient? client = null;
            try
            {
                client = _clientFactory(config, _logger);
                if (!client.IsReady)
                {
                    var lastError = string.IsNullOrWhiteSpace(client.LastError)
                        ? "Unknown Dataverse connection error."
                        : client.LastError;
                    var lastException = client.LastException;
                    throw new InvalidOperationException(
                        $"Failed to initialize live Dataverse ServiceClient: {lastError}",
                        lastException);
                }

                _client = client;
                _ownsClient = true;
                _ownedCertificate = config.OwnsCertificate ? config.Certificate : null;
                return _client;
            }
            catch
            {
                client?.Dispose();
                config.Dispose();
                throw;
            }
        }
    }

    internal static DataverseClientConfiguration ResolveClientConfiguration(
        DataverseOptions dataverseOptions,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(dataverseOptions);

        var hasConnectionString = !string.IsNullOrWhiteSpace(dataverseOptions.ConnectionString);
        var hasCertificate = dataverseOptions.Certificate?.HasCertificateSource() == true;

        if (hasConnectionString && hasCertificate)
        {
            throw new InvalidOperationException(
                "Configure either PowerPagesServerLogic:Dataverse:ConnectionString or " +
                "PowerPagesServerLogic:Dataverse:Certificate, not both.");
        }

        if (hasCertificate)
        {
            if (string.IsNullOrWhiteSpace(dataverseOptions.Url) ||
                !Uri.TryCreate(dataverseOptions.Url.Trim(), UriKind.Absolute, out var instanceUrl) ||
                (instanceUrl.Scheme != Uri.UriSchemeHttps && instanceUrl.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException(
                    "Live Dataverse certificate authentication requires a valid absolute HTTP(S) URL in " +
                    "PowerPagesServerLogic:Dataverse:Url.");
            }

            if (string.IsNullOrWhiteSpace(dataverseOptions.ClientId))
            {
                throw new InvalidOperationException(
                    "Live Dataverse certificate authentication requires an Entra ID application ID in " +
                    "PowerPagesServerLogic:Dataverse:ClientId.");
            }

            Uri redirectUri;
            if (string.IsNullOrWhiteSpace(dataverseOptions.RedirectUri))
            {
                redirectUri = DefaultCertificateRedirectUri;
            }
            else if (!Uri.TryCreate(dataverseOptions.RedirectUri.Trim(), UriKind.Absolute, out var parsedRedirect))
            {
                throw new InvalidOperationException(
                    "PowerPagesServerLogic:Dataverse:RedirectUri must be a valid absolute URI.");
            }
            else
            {
                redirectUri = parsedRedirect;
            }

            var certOptions = dataverseOptions.Certificate!;
            var loaded = DataverseCertificateLoader.Load(certOptions, contentRootPath);

            return new DataverseClientConfiguration
            {
                InstanceUrl = instanceUrl,
                ClientId = dataverseOptions.ClientId.Trim(),
                RedirectUri = redirectUri,
                UseUniqueInstance = dataverseOptions.UseUniqueInstance,
                StoreName = certOptions.StoreName,
                Certificate = loaded.Certificate,
                OwnsCertificate = loaded.OwnsCertificate
            };
        }

        if (hasConnectionString)
        {
            return new DataverseClientConfiguration
            {
                ConnectionString = dataverseOptions.ConnectionString!.Trim()
            };
        }

        throw new InvalidOperationException(
            "Live Dataverse mode requires a registered ServiceClient in DI, " +
            "PowerPagesServerLogic:Dataverse:ConnectionString, or " +
            "PowerPagesServerLogic:Dataverse:Certificate (with Url and ClientId).");
    }

    private static ServiceClient CreateServiceClient(DataverseClientConfiguration config, ILogger logger)
    {
        if (config.IsCertificateAuth)
        {
            return new ServiceClient(
                certificate: config.Certificate!,
                certificateStoreName: config.StoreName,
                certificateThumbPrint: config.Certificate!.Thumbprint,
                instanceUrl: config.InstanceUrl!,
                useUniqueInstance: config.UseUniqueInstance,
                orgDetail: null,
                clientId: config.ClientId!,
                redirectUri: config.RedirectUri!,
                logger: logger);
        }

        return new ServiceClient(config.ConnectionString!, logger);
    }

    internal static string NormalizeGuid(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Record ID must not be empty.", nameof(id));
        }

        return id.Trim().Trim('{', '}');
    }

    internal static string BuildQueryPath(string entitySetName, string? id = null, string? options = null)
    {
        if (string.IsNullOrWhiteSpace(entitySetName))
        {
            throw new ArgumentException("Entity set name must not be empty.", nameof(entitySetName));
        }

        var path = entitySetName.Trim().TrimStart('/');
        if (!string.IsNullOrWhiteSpace(id))
        {
            path = $"{path}({NormalizeGuid(id)})";
        }

        if (!string.IsNullOrWhiteSpace(options))
        {
            var trimmedOptions = options.Trim().TrimStart('?');
            if (!string.IsNullOrEmpty(trimmedOptions))
            {
                path = $"{path}?{trimmedOptions}";
            }
        }

        return path;
    }

    internal static Dictionary<string, List<string>> BuildHeaders(bool includeReturnRepresentation = false)
    {
        var preferValues = new List<string> { "odata.include-annotations=\"*\"" };
        if (includeReturnRepresentation)
        {
            preferValues.Add("return=representation");
        }

        return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["OData-Version"] = ["4.0"],
            ["OData-MaxVersion"] = ["4.0"],
            ["Prefer"] = preferValues
        };
    }

    internal static string? ExtractEntityIdJson(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("OData-EntityId", out var values))
        {
            var entityUrl = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(entityUrl))
            {
                var match = EntityIdRegex().Match(entityUrl);
                if (match.Success)
                {
                    return new JsonObject { ["id"] = match.Groups[1].Value }.ToJsonString();
                }
            }
        }

        return null;
    }

    private static string ReadResponseOrThrow(HttpResponseMessage response)
    {
        var body = response.Content is not null
            ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            : string.Empty;

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Dataverse Web API request failed with status {(int)response.StatusCode} ({response.StatusCode}): {body}",
                null,
                response.StatusCode);
        }

        return response.StatusCode == HttpStatusCode.NoContent ? string.Empty : body;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsClient)
        {
            _client?.Dispose();
        }

        _ownedCertificate?.Dispose();
        _ownedCertificate = null;
    }

    [GeneratedRegex(@"\(([0-9a-fA-F\-]{36})\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityIdRegex();
}
