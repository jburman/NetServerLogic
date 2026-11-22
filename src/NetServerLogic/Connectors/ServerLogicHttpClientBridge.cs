using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetServerLogic.Abstractions;
using NetServerLogic.Options;

namespace NetServerLogic.Connectors;

/// <summary>
/// Outbound HTTP bridge for <c>Server.Connector.HttpClient</c> backed by <see cref="IHttpClientFactory"/>
/// with Power Pages <c>AllowedDomains</c> enforcement.
/// </summary>
public sealed class ServerLogicHttpClientBridge : IServerLogicHttpClient
{
    public const string HttpClientName = "NetServerLogic";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ServerLogicEmulatorOptions _options;
    private readonly ILogger<ServerLogicHttpClientBridge> _logger;

    public ServerLogicHttpClientBridge(
        IHttpClientFactory httpClientFactory,
        IOptions<ServerLogicEmulatorOptions> options,
        ILogger<ServerLogicHttpClientBridge> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public Task<string> GetAsync(string url, IDictionary<string, object?>? headers = null)
        => SendAsync(HttpMethod.Get, url, body: null, headers, contentType: null);

    public Task<string> PostAsync(
        string url,
        string body,
        IDictionary<string, object?>? headers = null,
        string? contentType = "application/json")
        => SendAsync(HttpMethod.Post, url, body, headers, contentType);

    public Task<string> PutAsync(
        string url,
        string body,
        IDictionary<string, object?>? headers = null,
        string? contentType = "application/json")
        => SendAsync(HttpMethod.Put, url, body, headers, contentType);

    public Task<string> PatchAsync(
        string url,
        string body,
        IDictionary<string, object?>? headers = null,
        string? contentType = "application/json")
        => SendAsync(HttpMethod.Patch, url, body, headers, contentType);

    public Task<string> DeleteAsync(string url, IDictionary<string, object?>? headers = null)
        => SendAsync(HttpMethod.Delete, url, body: null, headers, contentType: null);

    private async Task<string> SendAsync(
        HttpMethod method,
        string url,
        string? body,
        IDictionary<string, object?>? headers,
        string? contentType)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid outbound HTTP URL '{url}'. An absolute HTTP/HTTPS URL is required.", nameof(url));
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new SecurityException($"Unsupported URI scheme '{uri.Scheme}'. Only HTTP and HTTPS are allowed.");
        }

        EnsureDomainAllowed(uri.Host);

        using var request = new HttpRequestMessage(method, uri);

        if (body is not null)
        {
            var effectiveMediaType = ResolveContentType(headers, contentType) ?? "application/json";
            request.Content = new StringContent(body, Encoding.UTF8, effectiveMediaType);
        }

        ApplyHeaders(request, headers);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        _logger.LogDebug("Issuing outbound {Method} request to {Host}", method, uri.Host);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (_options.HttpClientResponseMode == HttpClientResponseMode.RawBody)
        {
            return responseBody;
        }

        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectHeaders(response.Headers, responseHeaders);
        CollectHeaders(response.Content.Headers, responseHeaders);

        return JsonSerializer.Serialize(new
        {
            statusCode = (int)response.StatusCode,
            headers = responseHeaders,
            body = responseBody
        });
    }

    private void EnsureDomainAllowed(string host)
    {
        if (_options.AllowedDomains.Count == 0)
        {
            return;
        }

        foreach (var pattern in _options.AllowedDomains)
        {
            if (IsHostMatch(host, pattern))
            {
                return;
            }
        }

        throw new SecurityException(
            $"Outbound HTTP request to host '{host}' was blocked because it is not in AllowedDomains.");
    }

    internal static bool IsHostMatch(string host, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var trimmed = pattern.Trim();
        var colonIndex = trimmed.IndexOf(':');
        if (colonIndex > 0)
        {
            trimmed = trimmed[..colonIndex];
        }

        if (trimmed.StartsWith("*.", StringComparison.Ordinal))
        {
            var domainSuffix = trimmed[1..]; // keeps leading '.' e.g. ".contoso.com"
            return host.Length > domainSuffix.Length &&
                   host.EndsWith(domainSuffix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(host, trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveContentType(IDictionary<string, object?>? headers, string? fallback)
    {
        if (headers is not null)
        {
            foreach (var kvp in headers)
            {
                if (kvp.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                    kvp.Value is not null)
                {
                    var raw = Convert.ToString(kvp.Value);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var semi = raw.IndexOf(';');
                        return semi >= 0 ? raw[..semi].Trim() : raw.Trim();
                    }
                }
            }
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, object?>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var kvp in headers)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value is null)
            {
                continue;
            }

            var stringValue = Convert.ToString(kvp.Value);
            if (stringValue is null)
            {
                continue;
            }

            if (kvp.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Content is not null && MediaTypeHeaderValue.TryParse(stringValue, out var mediaType))
                {
                    request.Content.Headers.ContentType = mediaType;
                }

                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(kvp.Key, stringValue) && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(kvp.Key, stringValue);
            }
        }
    }

    private static void CollectHeaders(HttpHeaders headers, Dictionary<string, string> target)
    {
        foreach (var header in headers)
        {
            target[header.Key] = string.Join(", ", header.Value);
        }
    }
}
