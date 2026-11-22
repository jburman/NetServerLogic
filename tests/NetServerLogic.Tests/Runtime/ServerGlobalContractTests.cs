using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetServerLogic.Abstractions;
using NetServerLogic.Connectors;
using NetServerLogic.Options;
using NetServerLogic.Runtime;
using NetServerLogic.Validation;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace NetServerLogic.Tests.Runtime;

public sealed class ServerGlobalContractTests
{
    [Fact]
    public async Task QueryParametersAndHeaders_SupportNativeJsAndCaseInsensitiveAccess()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?accountId=123&tag=a&tag=b");
        httpContext.Request.Headers["X-Custom-Header"] = "hello";

        var engine = CreateEngine(new ServerLogicEmulatorOptions(), NullLoggerFactory.Instance);
        const string script = """
            function get() {
                var qp = Server.Context.QueryParameters;
                const { accountId } = qp;
                return JSON.stringify({
                    dotExact: qp.accountId,
                    dotCaseInsensitive: qp.ACCOUNTID,
                    bracketLower: qp["accountid"],
                    destructured: accountId,
                    optionalMissing: qp.nonexistent?.nested ?? "fallback",
                    keys: Object.keys(qp),
                    multiValue: qp.tag,
                    headerLower: Server.Context.Headers["x-custom-header"],
                    body: Server.Context.Body
                });
            }
            """;

        var result = await engine.ExecuteAsync(
            "contract",
            "contract.js",
            script,
            "GET",
            httpContext.Request,
            "raw-body");

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        using var doc = JsonDocument.Parse(result.Output!);
        var root = doc.RootElement;
        Assert.Equal("123", root.GetProperty("dotExact").GetString());
        Assert.Equal("123", root.GetProperty("dotCaseInsensitive").GetString());
        Assert.Equal("123", root.GetProperty("bracketLower").GetString());
        Assert.Equal("123", root.GetProperty("destructured").GetString());
        Assert.Equal("fallback", root.GetProperty("optionalMissing").GetString());
        Assert.Equal("a,b", root.GetProperty("multiValue").GetString());
        Assert.Equal("hello", root.GetProperty("headerLower").GetString());
        Assert.Equal("raw-body", root.GetProperty("body").GetString());

        var keys = root.GetProperty("keys").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["accountId", "tag"], keys);
    }

    [Fact]
    public async Task UserSiteSettingsEnvVarsLoggerAndCloudFlow_MatchContract()
    {
        var recordingFactory = new RecordingLoggerFactory();
        var options = new ServerLogicEmulatorOptions
        {
            SiteSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Custom/Flag"] = "enabled"
            },
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["cr123_ApiUrl"] = "https://api.contoso.com"
            }
        };

        var user = new MockUserOptions
        {
            ContactId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            FullName = "Dev Portal User",
            EmailAddress1 = "devuser@contoso.com",
            Roles = ["Authenticated Users", "Portal Member"]
        };

        var engine = CreateEngine(options, recordingFactory);
        const string script = """
            async function post() {
                Server.Logger.Log("info-msg");
                Server.Logger.Warn(123);
                Server.Logger.Error({ code: "E1" });

                var flowResp = await Server.Connector.CloudFlow.TriggerAsync("flow-1", "{\"ok\":true}");

                return JSON.stringify({
                    userId: Server.User.contactid,
                    fullName: Server.User.fullname,
                    email: Server.User.emailaddress1,
                    rolesIsArray: Array.isArray(Server.User.roles),
                    roles: Server.User.roles,
                    siteSetting: Server.SiteSetting.Get("Custom/Flag"),
                    missingSetting: Server.SiteSetting.Get("Missing"),
                    envVarLower: Server.EnvironmentVariable.get("cr123_ApiUrl"),
                    envVarUpper: Server.EnvironmentVariable.Get("cr123_ApiUrl"),
                    flow: JSON.parse(flowResp)
                });
            }
            """;

        var result = await engine.ExecuteAsync("globals", "globals.js", script, "POST", resolvedUser: user);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        using var doc = JsonDocument.Parse(result.Output!);
        var root = doc.RootElement;
        Assert.Equal(user.ContactId, root.GetProperty("userId").GetString());
        Assert.Equal(user.FullName, root.GetProperty("fullName").GetString());
        Assert.Equal(user.EmailAddress1, root.GetProperty("email").GetString());
        Assert.True(root.GetProperty("rolesIsArray").GetBoolean());
        Assert.Equal(2, root.GetProperty("roles").GetArrayLength());
        Assert.Equal("enabled", root.GetProperty("siteSetting").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("missingSetting").ValueKind);
        Assert.Equal("https://api.contoso.com", root.GetProperty("envVarLower").GetString());
        Assert.Equal("https://api.contoso.com", root.GetProperty("envVarUpper").GetString());
        Assert.Equal("Succeeded", root.GetProperty("flow").GetProperty("status").GetString());

        Assert.Contains(recordingFactory.Entries, e => e.Category == "ServerLogic.globals" && e.Level == LogLevel.Information && e.Message.Contains("info-msg"));
        Assert.Contains(recordingFactory.Entries, e => e.Category == "ServerLogic.globals" && e.Level == LogLevel.Warning && e.Message.Contains("123"));
        Assert.Contains(recordingFactory.Entries, e => e.Category == "ServerLogic.globals" && e.Level == LogLevel.Error && e.Message.Contains("E1"));
        Assert.Contains(recordingFactory.Entries, e => e.Category == "ServerLogic.globals" && e.Level == LogLevel.Warning && e.Message.Contains("Parity warning"));
    }

    [Fact]
    public async Task AnonymousUser_SetsServerUserToNull()
    {
        var engine = CreateEngine(new ServerLogicEmulatorOptions(), NullLoggerFactory.Instance);
        const string script = """
            function get() {
                return JSON.stringify({ isNull: Server.User === null });
            }
            """;

        var result = await engine.ExecuteAsync("anon", "anon.js", script, "GET", resolvedUser: null);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        using var doc = JsonDocument.Parse(result.Output!);
        Assert.True(doc.RootElement.GetProperty("isNull").GetBoolean());
    }

    private static ServerLogicExecutionEngine CreateEngine(
        ServerLogicEmulatorOptions options,
        ILoggerFactory loggerFactory)
    {
        var opts = OptionsFactory.Create(options);
        var dv = new MockDataverseProvider(opts);
        var http = new DummyHttpClient();
        var cf = new MockCloudFlowProvider(NullLogger<MockCloudFlowProvider>.Instance);
        var globalFactory = new ServerGlobalFactory(dv, http, cf, loggerFactory, opts);
        return new ServerLogicExecutionEngine(
            new AcornimaScriptValidator(),
            globalFactory,
            opts,
            NullLogger<ServerLogicExecutionEngine>.Instance);
    }

    private sealed class DummyHttpClient : IServerLogicHttpClient
    {
        public Task<string> GetAsync(string url, IDictionary<string, object?>? headers = null) => Task.FromResult("{}");
        public Task<string> PostAsync(string url, string body, IDictionary<string, object?>? headers = null, string? contentType = "application/json") => Task.FromResult("{}");
        public Task<string> PutAsync(string url, string body, IDictionary<string, object?>? headers = null, string? contentType = "application/json") => Task.FromResult("{}");
        public Task<string> PatchAsync(string url, string body, IDictionary<string, object?>? headers = null, string? contentType = "application/json") => Task.FromResult("{}");
        public Task<string> DeleteAsync(string url, IDictionary<string, object?>? headers = null) => Task.FromResult("{}");
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);

        public void Dispose() { }

        private sealed class RecordingLogger(string category, List<(string, LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Add((category, logLevel, formatter(state, exception)));
        }
    }
}
