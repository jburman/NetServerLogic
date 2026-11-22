using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NetServerLogic.Abstractions;
using NetServerLogic.Options;
using NetServerLogic.Runtime;
using NetServerLogic.Validation;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace NetServerLogic.Tests.Runtime;

public sealed class ExecutionEngineTests
{
    private static ServerLogicExecutionEngine CreateEngine(
        ServerLogicEmulatorOptions? options = null,
        IServerLogicDataverseProvider? dataverse = null,
        IServerLogicHttpClient? httpClient = null,
        IServerLogicCloudFlowProvider? cloudFlow = null,
        IServerLogicScriptValidator? validator = null)
    {
        var opts = OptionsFactory.Create(options ?? new ServerLogicEmulatorOptions());
        var dv = dataverse ?? new StubDataverseProvider();
        var http = httpClient ?? new StubHttpClient();
        var cf = cloudFlow ?? new StubCloudFlowProvider();
        var val = validator ?? new AcornimaScriptValidator();

        var globalFactory = new ServerGlobalFactory(dv, http, cf, NullLoggerFactory.Instance, opts);
        return new ServerLogicExecutionEngine(val, globalFactory, opts, NullLogger<ServerLogicExecutionEngine>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_SyncGet_Returns200WithStringOutput()
    {
        var engine = CreateEngine();
        const string script = """
            function get() {
                return JSON.stringify({ ok: true, method: "GET" });
            }
            """;

        var result = await engine.ExecuteAsync("test", "test.js", script, "GET");

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.True(result.IsSuccess);
        using var doc = JsonDocument.Parse(result.Output!);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("GET", doc.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_AsyncPost_AwaitsHttpClientPromise()
    {
        var http = new StubHttpClient { NextResponse = """{"statusCode":200,"body":"pong"}""" };
        var engine = CreateEngine(httpClient: http);
        const string script = """
            async function post() {
                var resp = await Server.Connector.HttpClient.GetAsync("https://api.contoso.com/ping");
                return JSON.stringify({ response: JSON.parse(resp) });
            }
            """;

        var result = await engine.ExecuteAsync("async-post", "async-post.js", script, "POST");

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        using var doc = JsonDocument.Parse(result.Output!);
        Assert.Equal("pong", doc.RootElement.GetProperty("response").GetProperty("body").GetString());
    }

    [Theory]
    [InlineData("DELETE", "del")]
    [InlineData("PUT", "put")]
    [InlineData("PATCH", "patch")]
    public async Task ExecuteAsync_VerbsMapToExpectedFunctions(string httpMethod, string jsFunction)
    {
        var engine = CreateEngine();
        var script = $$"""
            function {{jsFunction}}() {
                return JSON.stringify({ handled: "{{jsFunction}}" });
            }
            """;

        var result = await engine.ExecuteAsync("verbs", "verbs.js", script, httpMethod);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Contains(jsFunction, result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MissingVerbHandler_Returns405WithAllowedMethods()
    {
        var engine = CreateEngine();
        const string script = """
            function get() { return "ok"; }
            function del() { return "deleted"; }
            """;

        var result = await engine.ExecuteAsync("partial", "partial.js", script, "POST");

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, result.StatusCode);
        Assert.NotNull(result.AllowedMethods);
        Assert.Equal(["GET", "DELETE"], result.AllowedMethods);
    }

    [Theory]
    [InlineData("return { ok: true };")]
    [InlineData("return 42;")]
    [InlineData("return null;")]
    [InlineData("return;")]
    public async Task ExecuteAsync_NonStringReturn_Returns400ParityViolation(string returnStatement)
    {
        var engine = CreateEngine();
        var script = $$"""
            function get() {
                {{returnStatement}}
            }
            """;

        var result = await engine.ExecuteAsync("non-string", "non-string.js", script, "GET");

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal("PARITY_RETURN_TYPE", result.ErrorCode);
        Assert.Contains("requires returning a string", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_InfiniteLoop_TimesOutWith408()
    {
        var engine = CreateEngine(new ServerLogicEmulatorOptions { TimeoutInSeconds = 1 });
        const string script = """
            function get() {
                while (true) { }
                return "never";
            }
            """;

        var result = await engine.ExecuteAsync("timeout", "timeout.js", script, "GET");

        Assert.Equal(StatusCodes.Status408RequestTimeout, result.StatusCode);
        Assert.Equal("SCRIPT_TIMEOUT", result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_ScrubbedConsoleGlobal_ThrowsEvenIfValidatorSkipped()
    {
        var engine = CreateEngine(validator: new PassthroughValidator());
        const string script = """
            function get() {
                console.log("hello");
                return "ok";
            }
            """;

        var result = await engine.ExecuteAsync("scrub", "scrub.js", script, "GET");

        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Equal("JS_RUNTIME_ERROR", result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_StrictDataversePayloadMode_RejectsNonStringObjectPayload()
    {
        var engine = CreateEngine(new ServerLogicEmulatorOptions { StrictDataversePayloadMode = true });
        const string script = """
            function post() {
                return Server.Connector.Dataverse.CreateRecord("accounts", { name: "Contoso" });
            }
            """;

        var result = await engine.ExecuteAsync("strict-dv", "strict-dv.js", script, "POST");

        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("JSON string payload", result.ErrorMessage);
    }

    private sealed class PassthroughValidator : IServerLogicScriptValidator
    {
        public ScriptValidationResult Validate(string scriptSource, string scriptName)
            => new(Array.Empty<ScriptValidationDiagnostic>());
    }

    private sealed class StubDataverseProvider : IServerLogicDataverseProvider
    {
        public string RetrieveRecord(string entitySetName, string id, string? options = null, bool skipCache = false) => "{}";
        public string RetrieveMultipleRecords(string entitySetName, string? options = null, bool skipCache = false) => """{"value":[]}""";
        public string CreateRecord(string entitySetName, string jsonPayload) => """{"id":"11111111-1111-1111-1111-111111111111"}""";
        public string UpdateRecord(string entitySetName, string id, string jsonPayload) => "";
        public string DeleteRecord(string entitySetName, string id) => "";
        public string InvokeCustomApi(string httpMethod, string url, string? payload = null) => "{}";
    }

    private sealed class StubHttpClient : IServerLogicHttpClient
    {
        public string NextResponse { get; init; } = "{}";
        public Task<string> GetAsync(string url, IDictionary<string, object?>? headers = null) => Task.FromResult(NextResponse);
        public Task<string> PostAsync(string url, string body, IDictionary<string, object?>? headers = null, string? contentType = "application/json") => Task.FromResult(NextResponse);
        public Task<string> PutAsync(string url, string body, IDictionary<string, object?>? headers = null, string? contentType = "application/json") => Task.FromResult(NextResponse);
        public Task<string> PatchAsync(string url, string body, IDictionary<string, object?>? headers = null, string? contentType = "application/json") => Task.FromResult(NextResponse);
        public Task<string> DeleteAsync(string url, IDictionary<string, object?>? headers = null) => Task.FromResult(NextResponse);
    }

    private sealed class StubCloudFlowProvider : IServerLogicCloudFlowProvider
    {
        public Task<string> TriggerAsync(string flowUrlOrId, string payload, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"status":"Succeeded"}""");
    }
}
