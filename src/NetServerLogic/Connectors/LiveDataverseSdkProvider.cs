using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.PowerPlatform.Dataverse.Client;
using NetServerLogic.Abstractions;
using NetServerLogic.Options;

namespace NetServerLogic.Connectors;

/// <summary>
/// Live Dataverse Web API v9.2 bridge via <see cref="ServiceClient.ExecuteWebRequest"/>.
/// </summary>
public sealed partial class LiveDataverseSdkProvider : IServerLogicDataverseProvider, IDisposable
{
    private readonly ServerLogicEmulatorOptions _options;
    private readonly ILogger<LiveDataverseSdkProvider> _logger;
    private readonly object _syncRoot = new();
    private ServiceClient? _client;
    private bool _ownsClient;
    private bool _disposed;

    public LiveDataverseSdkProvider(
        IOptions<ServerLogicEmulatorOptions> options,
        ILogger<LiveDataverseSdkProvider> logger,
        ServiceClient? serviceClient = null)
    {
        _options = options.Value;
        _logger = logger;
        _client = serviceClient;
        _ownsClient = false;
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

            var connectionString = _options.Dataverse.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Live Dataverse mode requires either a registered ServiceClient in DI or PowerPagesServerLogic:Dataverse:ConnectionString.");
            }

            _client = new ServiceClient(connectionString);
            _ownsClient = true;
            return _client;
        }
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
    }

    [GeneratedRegex(@"\(([0-9a-fA-F\-]{36})\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityIdRegex();
}
