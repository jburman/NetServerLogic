using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetServerLogic.Connectors;
using NetServerLogic.Hosting;
using NetServerLogic.Metadata;
using NetServerLogic.Options;
using NetServerLogic.Runtime;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace NetServerLogic.Tests.Hosting;

public sealed class ServerLogicMiddlewareIntegrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _serverLogicDir;
    private readonly string _mocksDir;
    private readonly string _webRolesDir;

    public ServerLogicMiddlewareIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"NetServerLogic_Int_{Guid.NewGuid():N}");
        _serverLogicDir = Path.Combine(_tempRoot, ".powerpages-site", "server-logic");
        _mocksDir = Path.Combine(_tempRoot, ".serverlogic-mocks");
        _webRolesDir = Path.Combine(_tempRoot, ".powerpages-site", "web-roles");

        Directory.CreateDirectory(_serverLogicDir);
        Directory.CreateDirectory(_mocksDir);
        Directory.CreateDirectory(_webRolesDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private async Task<WebApplication> BuildTestAppAsync(
        Action<ServerLogicEmulatorOptions>? configure = null,
        HttpMessageHandler? outboundHandler = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _tempRoot,
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();

        builder.Services.AddPowerPagesServerLogicEmulator(opts =>
        {
            opts.ServerLogicRootPath = _serverLogicDir;
            opts.MockDataPath = _mocksDir;
            opts.DataverseMode = DataverseMode.Mock;
            opts.AllowedDomains = ["api.contoso.com"];
            opts.EnvironmentVariables["cr123_ExternalApiBaseUrl"] = "https://api.contoso.com";
            opts.DefaultMockUser = "StandardUser";
            opts.MockUsers["StandardUser"] = new MockUserOptions
            {
                ContactId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                FullName = "Dev Portal User",
                EmailAddress1 = "devuser@contoso.com",
                Roles = ["Authenticated Users", "Portal Member"]
            };
            opts.MockUsers["AdminUser"] = new MockUserOptions
            {
                ContactId = "99999999-8888-7777-6666-555555555555",
                FullName = "Dev Portal Admin",
                EmailAddress1 = "admin@contoso.com",
                Roles = ["Authenticated Users", "Administrators"]
            };
            configure?.Invoke(opts);
        });

        if (outboundHandler is not null)
        {
            builder.Services.AddHttpClient(ServerLogicHttpClientBridge.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => outboundHandler);
        }

        var app = builder.Build();
        app.MapPowerPagesServerLogic();
        await app.StartAsync();
        return app;
    }

    private void WriteScript(string name, string jsContent, string? ymlContent = null)
    {
        var dir = Path.Combine(_serverLogicDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.js"), jsContent);
        if (ymlContent is not null)
        {
            File.WriteAllText(Path.Combine(dir, $"{name}.serverlogic.yml"), ymlContent);
        }
    }

    [Fact]
    public async Task GoldenScript_GetAndPost_ExecuteEndToEndAgainstMockDataverseAndHttpClient()
    {
        File.WriteAllText(
            Path.Combine(_mocksDir, "accounts.json"),
            """
            [
              { "accountid": "11111111-1111-1111-1111-111111111111", "name": "Contoso Ltd", "revenue": 1500000 },
              { "accountid": "22222222-2222-2222-2222-222222222222", "name": "Small Co", "revenue": 10000 }
            ]
            """);

        WriteScript("accounts-summary", """
            function get() {
                var minRevenue = Server.Context.QueryParameters.minRevenue || "0";
                var user = Server.User;

                Server.Logger.Log("Fetching accounts for user: " + (user ? user.fullname : "anonymous"));

                var query = "$select=accountid,name,revenue&$filter=revenue ge " + minRevenue + "&$orderby=revenue desc&$top=10";
                var rawJson = Server.Connector.Dataverse.RetrieveMultipleRecords("accounts", query);
                var parsed = JSON.parse(rawJson);

                return JSON.stringify({
                    requestedBy: user ? user.contactid : null,
                    count: parsed.value.length,
                    items: parsed.value
                });
            }

            async function post() {
                var payload = JSON.parse(Server.Context.Body);
                var externalBase = Server.EnvironmentVariable.get("cr123_ExternalApiBaseUrl");

                var externalResp = await Server.Connector.HttpClient.GetAsync(externalBase + "/health");
                var created = Server.Connector.Dataverse.CreateRecord("accounts", JSON.stringify({
                    name: payload.name,
                    revenue: payload.revenue
                }));

                return JSON.stringify({
                    created: created,
                    healthCheck: externalResp
                });
            }
            """);

        var fakeOutbound = new StubOutboundHandler("""{"healthy":true}""");
        await using var app = await BuildTestAppAsync(outboundHandler: fakeOutbound);
        var client = app.GetTestClient();

        // GET
        var getResp = await client.GetAsync("/_api/serverlogics/accounts-summary?minRevenue=100000");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var getJson = await getResp.Content.ReadAsStringAsync();
        using (var getDoc = JsonDocument.Parse(getJson))
        {
            Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", getDoc.RootElement.GetProperty("requestedBy").GetString());
            Assert.Equal(1, getDoc.RootElement.GetProperty("count").GetInt32());
            Assert.Equal("Contoso Ltd", getDoc.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
        }

        // POST
        var postResp = await client.PostAsync(
            "/_api/serverlogics/accounts-summary",
            new StringContent("""{"name":"Wingtip Toys","revenue":2000000}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);

        var postJson = await postResp.Content.ReadAsStringAsync();
        using (var postDoc = JsonDocument.Parse(postJson))
        {
            var createdInner = JsonDocument.Parse(postDoc.RootElement.GetProperty("created").GetString()!);
            Assert.True(Guid.TryParse(createdInner.RootElement.GetProperty("id").GetString(), out _));

            var healthInner = JsonDocument.Parse(postDoc.RootElement.GetProperty("healthCheck").GetString()!);
            Assert.Equal(200, healthInner.RootElement.GetProperty("statusCode").GetInt32());
            Assert.Contains("healthy", healthInner.RootElement.GetProperty("body").GetString());
        }

        // Confirm newly created account shows up in subsequent GET
        var getAfterPost = await client.GetAsync("/_api/serverlogics/accounts-summary?minRevenue=100000");
        using var afterDoc = JsonDocument.Parse(await getAfterPost.Content.ReadAsStringAsync());
        Assert.Equal(2, afterDoc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("Wingtip Toys", afterDoc.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task HotReload_ReflectsScriptEditsImmediatelyOnNextRequest()
    {
        WriteScript("reloadable", """function get() { return JSON.stringify({ version: 1 }); }""");

        await using var app = await BuildTestAppAsync();
        var client = app.GetTestClient();

        var first = await client.GetStringAsync("/_api/serverlogics/reloadable");
        Assert.Contains("\"version\":1", first);

        WriteScript("reloadable", """function get() { return JSON.stringify({ version: 2 }); }""");

        var second = await client.GetStringAsync("/_api/serverlogics/reloadable");
        Assert.Contains("\"version\":2", second);
    }

    [Fact]
    public async Task RoutingErrors_404Missing_405WithAllowHeader_AndPathTraversal400()
    {
        WriteScript("only-get", """function get() { return "ok"; }""");

        await using var app = await BuildTestAppAsync();
        var client = app.GetTestClient();

        // Missing script -> 404
        var missingResp = await client.GetAsync("/_api/serverlogics/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missingResp.StatusCode);

        // Missing verb -> 405 with Allow: GET
        var methodResp = await client.PostAsync("/_api/serverlogics/only-get", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, methodResp.StatusCode);
        Assert.Contains("GET", methodResp.Content.Headers.Allow);

        // Encoded traversal via HTTP -> 400
        var traversalHttpResp = await client.GetAsync("/_api/serverlogics/..%2Fsecret");
        Assert.Equal(HttpStatusCode.BadRequest, traversalHttpResp.StatusCode);

        // Direct resolver checks for traversal attempts
        var resolver = new ServerLogicFileResolver(
            OptionsFactory.Create(new ServerLogicEmulatorOptions { ServerLogicRootPath = _serverLogicDir }));
        Assert.Equal(ServerLogicResolutionStatus.InvalidName, resolver.Resolve("../secret").Status);
        Assert.Equal(ServerLogicResolutionStatus.InvalidName, resolver.Resolve("foo/bar").Status);
        Assert.Equal(ServerLogicResolutionStatus.InvalidName, resolver.Resolve(@"foo\bar").Status);
    }

    [Fact]
    public async Task CsrfEnforcement_TokenEndpointAnd403Validation()
    {
        WriteScript("csrf-test", """function post() { return JSON.stringify({ ok: true }); }""");

        await using var app = await BuildTestAppAsync(opts =>
        {
            opts.EnableCsrfTokenEndpoint = true;
            opts.RequireCsrfToken = true;
            opts.CsrfTokenValue = "dev-local-csrf-token";
        });
        var client = app.GetTestClient();

        var tokenHtml = await client.GetStringAsync("/_layout/tokenhtml");
        Assert.Contains("__RequestVerificationToken", tokenHtml);
        Assert.Contains("dev-local-csrf-token", tokenHtml);

        // Missing token -> 403
        var missingTokenResp = await client.PostAsync("/_api/serverlogics/csrf-test", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Forbidden, missingTokenResp.StatusCode);

        // Wrong token -> 403
        using var wrongReq = new HttpRequestMessage(HttpMethod.Post, "/_api/serverlogics/csrf-test");
        wrongReq.Headers.Add("__RequestVerificationToken", "wrong-token");
        var wrongResp = await client.SendAsync(wrongReq);
        Assert.Equal(HttpStatusCode.Forbidden, wrongResp.StatusCode);

        // Valid token -> 200
        using var validReq = new HttpRequestMessage(HttpMethod.Post, "/_api/serverlogics/csrf-test");
        validReq.Headers.Add("__RequestVerificationToken", "dev-local-csrf-token");
        var validResp = await client.SendAsync(validReq);
        Assert.Equal(HttpStatusCode.OK, validResp.StatusCode);
    }

    [Fact]
    public async Task WebRoleEnforcement_Returns401_403_And200WithPersonaSwitchingAndGuidMapping()
    {
        var adminRoleGuid = "aaaaaaaa-1111-2222-3333-444444444444";
        File.WriteAllText(
            Path.Combine(_webRolesDir, "administrators.webrole.yml"),
            $"""
            adx_webroleid: {adminRoleGuid}
            adx_name: Administrators
            """);

        WriteScript(
            "admin-only",
            """function get() { return JSON.stringify({ admin: Server.User.fullname }); }""",
            $"""
            adx_serverlogicid: 00000000-0000-0000-0000-000000000001
            adx_name: admin-only
            adx_serverlogic_webrole:
              - {adminRoleGuid}
            """);

        WriteScript(
            "public-info",
            """function get() { return JSON.stringify({ user: Server.User }); }""",
            """
            adx_serverlogic_webrole:
              - Anonymous Users
            """);

        await using var app = await BuildTestAppAsync(opts => opts.EnforceWebRoles = true);
        var client = app.GetTestClient();

        // Anonymous request to admin-only -> 401
        using (var anonReq = new HttpRequestMessage(HttpMethod.Get, "/_api/serverlogics/admin-only"))
        {
            anonReq.Headers.Add(ServerGlobalFactory.MockUserHeaderName, "anonymous");
            var anonResp = await client.SendAsync(anonReq);
            Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);
        }

        // StandardUser (default) lacks Administrators role -> 403
        var forbiddenResp = await client.GetAsync("/_api/serverlogics/admin-only");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);

        // AdminUser via persona header -> 200
        using (var adminReq = new HttpRequestMessage(HttpMethod.Get, "/_api/serverlogics/admin-only"))
        {
            adminReq.Headers.Add(ServerGlobalFactory.MockUserHeaderName, "AdminUser");
            var adminResp = await client.SendAsync(adminReq);
            Assert.Equal(HttpStatusCode.OK, adminResp.StatusCode);
            Assert.Contains("Dev Portal Admin", await adminResp.Content.ReadAsStringAsync());
        }

        // Anonymous request to Anonymous Users script -> 200 with Server.User == null
        using (var pubReq = new HttpRequestMessage(HttpMethod.Get, "/_api/serverlogics/public-info"))
        {
            pubReq.Headers.Add(ServerGlobalFactory.MockUserHeaderName, "anonymous");
            var pubResp = await client.SendAsync(pubReq);
            Assert.Equal(HttpStatusCode.OK, pubResp.StatusCode);
            Assert.Contains("\"user\":null", await pubResp.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task ResponseFormat_WrappedEnvelope_WrapsOutput()
    {
        WriteScript("wrapped", """function get() { return "hello-world"; }""");

        await using var app = await BuildTestAppAsync(opts => opts.ResponseFormat = ServerLogicResponseFormat.WrappedEnvelope);
        var client = app.GetTestClient();

        var body = await client.GetStringAsync("/_api/serverlogics/wrapped");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("hello-world", doc.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public void LiveDataverseSdkProvider_HelpersAndLazyConfigValidation()
    {
        Assert.Equal("11111111-1111-1111-1111-111111111111", LiveDataverseSdkProvider.NormalizeGuid("{11111111-1111-1111-1111-111111111111}"));
        Assert.Equal(
            "accounts(11111111-1111-1111-1111-111111111111)?$select=name",
            LiveDataverseSdkProvider.BuildQueryPath("accounts", "{11111111-1111-1111-1111-111111111111}", "?$select=name"));

        var headers = LiveDataverseSdkProvider.BuildHeaders(includeReturnRepresentation: true);
        Assert.Equal(["4.0"], headers["OData-Version"]);
        Assert.Equal(["4.0"], headers["OData-MaxVersion"]);
        Assert.Contains("return=representation", headers["Prefer"]);

        using var fakeResponse = new HttpResponseMessage(HttpStatusCode.NoContent);
        fakeResponse.Headers.TryAddWithoutValidation(
            "OData-EntityId",
            "https://org.crm.dynamics.com/api/data/v9.2/accounts(aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee)");
        var idJson = LiveDataverseSdkProvider.ExtractEntityIdJson(fakeResponse);
        Assert.Contains("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", idJson);

        using var liveProvider = new LiveDataverseSdkProvider(
            OptionsFactory.Create(new ServerLogicEmulatorOptions { DataverseMode = DataverseMode.Live }),
            NullLogger<LiveDataverseSdkProvider>.Instance);
        Assert.Throws<InvalidOperationException>(() => liveProvider.RetrieveMultipleRecords("accounts"));
    }

    private sealed class StubOutboundHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
