using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Interop;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetServerLogic.Abstractions;
using NetServerLogic.Options;

namespace NetServerLogic.Runtime;

/// <summary>
/// Builds the per-request Power Pages <c>Server</c> global object graph inside a <see cref="Jint.Engine"/>.
/// </summary>
public sealed class ServerGlobalFactory
{
    public const string MockUserHeaderName = "X-PowerPages-Mock-User";

    private readonly IServerLogicDataverseProvider _dataverseProvider;
    private readonly IServerLogicHttpClient _httpClient;
    private readonly IServerLogicCloudFlowProvider _cloudFlowProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ServerLogicEmulatorOptions _options;

    public ServerGlobalFactory(
        IServerLogicDataverseProvider dataverseProvider,
        IServerLogicHttpClient httpClient,
        IServerLogicCloudFlowProvider cloudFlowProvider,
        ILoggerFactory loggerFactory,
        IOptions<ServerLogicEmulatorOptions> options)
    {
        _dataverseProvider = dataverseProvider;
        _httpClient = httpClient;
        _cloudFlowProvider = cloudFlowProvider;
        _loggerFactory = loggerFactory;
        _options = options.Value;
    }

    public JsObject CreateServerGlobal(
        Engine engine,
        string scriptName,
        HttpRequest? request,
        string requestBody,
        MockUserOptions? resolvedUser)
    {
        var queryObj = request is not null
            ? request.Query.ToJsObject(engine)
            : new CaseInsensitiveJsObject(engine);

        var headersObj = request is not null
            ? request.Headers.ToJsObject(engine)
            : new CaseInsensitiveJsObject(engine);

        var contextObj = new JsObject(engine);
        contextObj.FastSetDataProperty("QueryParameters", queryObj);
        contextObj.FastSetDataProperty("Headers", headersObj);
        contextObj.FastSetDataProperty("Body", requestBody ?? string.Empty);

        var logger = _loggerFactory.CreateLogger($"ServerLogic.{scriptName}");
        var loggerObj = BuildLoggerObject(engine, logger);

        JsValue userValue = resolvedUser is not null
            ? resolvedUser.ToJsObject(engine)
            : JsValue.Null;

        var siteSettingObj = BuildSiteSettingObject(engine);
        var envVarObj = BuildEnvironmentVariableObject(engine, logger);
        var connectorObj = BuildConnectorObject(engine);

        var serverObj = new JsObject(engine);
        serverObj.FastSetDataProperty("Context", contextObj);
        serverObj.FastSetDataProperty("Logger", loggerObj);
        serverObj.FastSetDataProperty("User", userValue);
        serverObj.FastSetDataProperty("SiteSetting", siteSettingObj);
        serverObj.FastSetDataProperty("EnvironmentVariable", envVarObj);
        serverObj.FastSetDataProperty("Connector", connectorObj);

        return serverObj;
    }

    /// <summary>
    /// Resolves the mock portal user for the request using <c>X-PowerPages-Mock-User</c> or <c>DefaultMockUser</c>.
    /// </summary>
    public static MockUserOptions? ResolveUser(HttpRequest? request, ServerLogicEmulatorOptions options)
    {
        string? personaKey = null;

        if (request is not null &&
            request.Headers.TryGetValue(MockUserHeaderName, out var headerValues))
        {
            personaKey = headerValues.ToString().Trim();
        }
        else if (!string.IsNullOrWhiteSpace(options.DefaultMockUser))
        {
            personaKey = options.DefaultMockUser.Trim();
        }

        if (string.IsNullOrWhiteSpace(personaKey) ||
            personaKey.Equals("anonymous", StringComparison.OrdinalIgnoreCase) ||
            personaKey.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            personaKey.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (options.MockUsers.TryGetValue(personaKey, out var user))
        {
            return user;
        }

        return null;
    }

    private static JsObject BuildLoggerObject(Engine engine, ILogger logger)
    {
        var obj = new JsObject(engine);
        obj.FastSetDataProperty("Log", new ClrFunction(engine, "Log", (_, args) =>
        {
            logger.LogInformation("{Message}", FormatLogArgs(args));
            return JsValue.Undefined;
        }));
        obj.FastSetDataProperty("Warn", new ClrFunction(engine, "Warn", (_, args) =>
        {
            logger.LogWarning("{Message}", FormatLogArgs(args));
            return JsValue.Undefined;
        }));
        obj.FastSetDataProperty("Error", new ClrFunction(engine, "Error", (_, args) =>
        {
            logger.LogError("{Message}", FormatLogArgs(args));
            return JsValue.Undefined;
        }));
        return obj;
    }

    private static string FormatLogArgs(JsValue[] args)
    {
        if (args.Length == 0)
        {
            return string.Empty;
        }

        if (args.Length == 1)
        {
            return CoerceJsValueToString(args[0]);
        }

        return string.Join(" ", args.Select(CoerceJsValueToString));
    }

    private static string CoerceJsValueToString(JsValue value)
    {
        if (value.IsUndefined()) return "undefined";
        if (value.IsNull()) return "null";
        if (value.IsString()) return value.AsString();
        if (value.IsObject())
        {
            try
            {
                return JsonSerializer.Serialize(value.ToObject());
            }
            catch
            {
                return value.ToString();
            }
        }

        return value.ToString();
    }

    private JsObject BuildSiteSettingObject(Engine engine)
    {
        var obj = new JsObject(engine);
        obj.FastSetDataProperty("Get", new ClrFunction(engine, "Get", (_, args) =>
        {
            var name = args.Length > 0 && !args[0].IsNull() && !args[0].IsUndefined()
                ? args[0].ToString()
                : string.Empty;

            return _options.SiteSettings.TryGetValue(name, out var value)
                ? value
                : JsValue.Null;
        }));
        return obj;
    }

    private JsObject BuildEnvironmentVariableObject(Engine engine, ILogger logger)
    {
        JsValue GetEnvVar(string name)
        {
            return _options.EnvironmentVariables.TryGetValue(name, out var value)
                ? value
                : JsValue.Null;
        }

        var obj = new JsObject(engine);
        obj.FastSetDataProperty("get", new ClrFunction(engine, "get", (_, args) =>
        {
            var name = args.Length > 0 && !args[0].IsNull() && !args[0].IsUndefined()
                ? args[0].ToString()
                : string.Empty;
            return GetEnvVar(name);
        }));

        obj.FastSetDataProperty("Get", new ClrFunction(engine, "Get", (_, args) =>
        {
            logger.LogWarning(
                "Parity warning: Server.EnvironmentVariable.Get() was called; Power Pages SDK uses lowercase Server.EnvironmentVariable.get().");
            var name = args.Length > 0 && !args[0].IsNull() && !args[0].IsUndefined()
                ? args[0].ToString()
                : string.Empty;
            return GetEnvVar(name);
        }));

        return obj;
    }

    private JsObject BuildConnectorObject(Engine engine)
    {
        var dataverseProxy = new DataverseConnectorProxy(_dataverseProvider, _options);
        var httpClientProxy = new HttpClientConnectorProxy(_httpClient);
        var cloudFlowProxy = new CloudFlowConnectorProxy(_cloudFlowProvider);

        var connectorObj = new JsObject(engine);
        connectorObj.FastSetDataProperty("Dataverse", JsValue.FromObject(engine, dataverseProxy));
        connectorObj.FastSetDataProperty("HttpClient", JsValue.FromObject(engine, httpClientProxy));
        connectorObj.FastSetDataProperty("CloudFlow", JsValue.FromObject(engine, cloudFlowProxy));
        return connectorObj;
    }

    private sealed class DataverseConnectorProxy
    {
        private readonly IServerLogicDataverseProvider _provider;
        private readonly ServerLogicEmulatorOptions _options;

        public DataverseConnectorProxy(IServerLogicDataverseProvider provider, ServerLogicEmulatorOptions options)
        {
            _provider = provider;
            _options = options;
        }

        public string RetrieveRecord(string entitySetName, string id, string? options = null, bool skipCache = false)
            => _provider.RetrieveRecord(entitySetName, id, options, skipCache);

        public string RetrieveMultipleRecords(string entitySetName, string? options = null, bool skipCache = false)
            => _provider.RetrieveMultipleRecords(entitySetName, options, skipCache);

        public string CreateRecord(string entitySetName, JsValue jsonPayload)
            => _provider.CreateRecord(entitySetName, ResolveJsonPayload(jsonPayload, nameof(CreateRecord)));

        public string UpdateRecord(string entitySetName, string id, JsValue jsonPayload)
            => _provider.UpdateRecord(entitySetName, id, ResolveJsonPayload(jsonPayload, nameof(UpdateRecord)));

        public string DeleteRecord(string entitySetName, string id)
            => _provider.DeleteRecord(entitySetName, id);

        public string InvokeCustomApi(string httpMethod, string url, JsValue? payload = null)
        {
            if (payload is null || payload.IsNull() || payload.IsUndefined())
            {
                return _provider.InvokeCustomApi(httpMethod, url, null);
            }

            return _provider.InvokeCustomApi(httpMethod, url, ResolveJsonPayload(payload, nameof(InvokeCustomApi)));
        }

        private string ResolveJsonPayload(JsValue payload, string methodName)
        {
            if (payload.IsString())
            {
                return payload.AsString();
            }

            if (_options.StrictDataversePayloadMode)
            {
                throw new InvalidOperationException(
                    $"Parity violation: Server.Connector.Dataverse.{methodName} requires a JSON string payload (e.g., JSON.stringify(payload)), but received {payload.Type}.");
            }

            return JsonSerializer.Serialize(payload.ToObject());
        }
    }

    private sealed class HttpClientConnectorProxy
    {
        private readonly IServerLogicHttpClient _httpClient;

        public HttpClientConnectorProxy(IServerLogicHttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public Task<string> GetAsync(string url, JsValue? headers = null)
            => _httpClient.GetAsync(url, headers.ToHeaderDictionary());

        public Task<string> PostAsync(string url, string body, JsValue? headers = null, string? contentType = "application/json")
            => _httpClient.PostAsync(url, body, headers.ToHeaderDictionary(), contentType);

        public Task<string> PutAsync(string url, string body, JsValue? headers = null, string? contentType = "application/json")
            => _httpClient.PutAsync(url, body, headers.ToHeaderDictionary(), contentType);

        public Task<string> PatchAsync(string url, string body, JsValue? headers = null, string? contentType = "application/json")
            => _httpClient.PatchAsync(url, body, headers.ToHeaderDictionary(), contentType);

        public Task<string> DeleteAsync(string url, JsValue? headers = null)
            => _httpClient.DeleteAsync(url, headers.ToHeaderDictionary());
    }

    private sealed class CloudFlowConnectorProxy
    {
        private readonly IServerLogicCloudFlowProvider _cloudFlowProvider;

        public CloudFlowConnectorProxy(IServerLogicCloudFlowProvider cloudFlowProvider)
        {
            _cloudFlowProvider = cloudFlowProvider;
        }

        public Task<string> TriggerAsync(string flowUrlOrId, string payload)
            => _cloudFlowProvider.TriggerAsync(flowUrlOrId, payload);
    }
}
