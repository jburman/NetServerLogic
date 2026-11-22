using System.Net;
using System.Security;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NetServerLogic.Connectors;
using NetServerLogic.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace NetServerLogic.Tests.Connectors;

public sealed class HttpClientBridgeTests
{
    [Fact]
    public async Task AllowedDomains_PermitsExactAndWildcardSubdomains()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""")
            });

        var bridge = CreateBridge(
            new ServerLogicEmulatorOptions
            {
                AllowedDomains = ["api.github.com", "*.contoso.com"],
                HttpClientResponseMode = HttpClientResponseMode.StructuredJson
            },
            handler);

        var exactResp = await bridge.GetAsync("https://api.github.com/users");
        var wildcardResp = await bridge.GetAsync("https://sub.api.contoso.com/v1/items");

        using var exactDoc = JsonDocument.Parse(exactResp);
        Assert.Equal(200, exactDoc.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Contains("true", exactDoc.RootElement.GetProperty("body").GetString());

        using var wildcardDoc = JsonDocument.Parse(wildcardResp);
        Assert.Equal(200, wildcardDoc.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("https://evil.example.org/data")]
    [InlineData("https://evilcontoso.com/steal")]
    [InlineData("https://contoso.com/root-not-wildcard-subdomain")]
    public async Task AllowedDomains_BlocksDisallowedAndSuffixSpoofedHostsBeforeNetworkCall(string blockedUrl)
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK));

        var bridge = CreateBridge(
            new ServerLogicEmulatorOptions
            {
                AllowedDomains = ["api.github.com", "*.contoso.com"]
            },
            handler);

        await Assert.ThrowsAsync<SecurityException>(() => bridge.GetAsync(blockedUrl));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PostAsync_ForwardsCustomHeadersAndRespectsRawBodyMode()
    {
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("raw-created-body")
            };
            resp.Headers.Add("X-Server-Tag", "netserverlogic");
            return resp;
        });

        var options = new ServerLogicEmulatorOptions
        {
            AllowedDomains = ["api.contoso.com"],
            HttpClientResponseMode = HttpClientResponseMode.RawBody
        };
        var bridge = CreateBridge(options, handler);

        var headers = new Dictionary<string, object?>
        {
            ["Authorization"] = "Bearer token-123",
            ["X-Correlation-Id"] = "corr-456"
        };

        var result = await bridge.PostAsync(
            "https://api.contoso.com/orders",
            """{"total":10}""",
            headers,
            "application/json");

        Assert.Equal("raw-created-body", result);
        var captured = Assert.Single(handler.Requests);
        Assert.Equal("Bearer token-123", captured.Headers.GetValues("Authorization").Single());
        Assert.Equal("corr-456", captured.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("application/json", captured.Content?.Headers.ContentType?.MediaType);
    }

    private static ServerLogicHttpClientBridge CreateBridge(
        ServerLogicEmulatorOptions options,
        HttpMessageHandler handler)
    {
        var factory = new SingleHandlerHttpClientFactory(handler);
        return new ServerLogicHttpClientBridge(
            factory,
            OptionsFactory.Create(options),
            NullLogger<ServerLogicHttpClientBridge>.Instance);
    }

    private sealed class SingleHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request, cancellationToken));
        }
    }
}
