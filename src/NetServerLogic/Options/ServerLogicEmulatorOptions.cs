namespace NetServerLogic.Options;

/// <summary>
/// Controls how the HTTP endpoint formats the string returned by a Server Logic verb handler.
/// </summary>
public enum ServerLogicResponseFormat
{
    RawString,
    WrappedEnvelope
}

/// <summary>
/// Controls what string <c>Server.Connector.HttpClient.*Async</c> resolves to in JavaScript.
/// </summary>
public enum HttpClientResponseMode
{
    StructuredJson,
    RawBody
}

/// <summary>
/// Controls the return string format of <c>Server.Connector.Dataverse.CreateRecord</c>.
/// </summary>
public enum CreateRecordReturnMode
{
    IdObject,
    FullRepresentation,
    Empty
}

/// <summary>
/// Dataverse connection and schema options.
/// </summary>
public sealed class DataverseOptions
{
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Dataverse environment URL (e.g., <c>https://contoso.crm.dynamics.com</c>).
    /// Required when using <see cref="Certificate"/> authentication.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Microsoft Entra ID application (client) ID.
    /// Required when using <see cref="Certificate"/> authentication.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Optional redirect URI passed to <c>ServiceClient</c> for certificate authentication.
    /// Defaults to <c>http://localhost</c>.
    /// </summary>
    public string? RedirectUri { get; set; }

    /// <summary>
    /// Forces creation of a unique <c>ServiceClient</c> connection instance instead of reusing
    /// a cached SDK connection. Defaults to <see langword="true"/>.
    /// </summary>
    public bool UseUniqueInstance { get; set; } = true;

    /// <summary>
    /// Certificate-based authentication configuration for live Dataverse mode.
    /// </summary>
    public DataverseCertificateOptions? Certificate { get; set; }

    public CreateRecordReturnMode CreateRecordReturnMode { get; set; } = CreateRecordReturnMode.IdObject;

    public Dictionary<string, string> PrimaryKeyOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Configuration options for the NetServerLogic Power Pages Server Logic emulator.
/// </summary>
public sealed class ServerLogicEmulatorOptions
{
    public const string SectionName = "PowerPagesServerLogic";

    public string ServerLogicRootPath { get; set; } = ".powerpages-site/server-logic";

    public DataverseMode DataverseMode { get; set; } = DataverseMode.Mock;

    public string MockDataPath { get; set; } = ".serverlogic-mocks";

    public int TimeoutInSeconds { get; set; } = 120;

    public long MaxMemoryBytes { get; set; } = 67_108_864L;

    public int MaxRecursionDepth { get; set; } = 64;

    public bool StrictValidatorMode { get; set; }

    public bool StrictDataversePayloadMode { get; set; } = true;

    public bool RequireCsrfToken { get; set; }

    public bool EnableCsrfTokenEndpoint { get; set; } = true;

    public string CsrfTokenValue { get; set; } = "dev-local-csrf-token";

    public bool EnforceWebRoles { get; set; }

    public ServerLogicResponseFormat ResponseFormat { get; set; } = ServerLogicResponseFormat.RawString;

    public HttpClientResponseMode HttpClientResponseMode { get; set; } = HttpClientResponseMode.StructuredJson;

    public List<string> AllowedDomains { get; set; } = new();

    public DataverseOptions Dataverse { get; set; } = new();

    public Dictionary<string, string> SiteSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> EnvironmentVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? DefaultMockUser { get; set; }

    public Dictionary<string, MockUserOptions> MockUsers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Func<string?, CancellationToken, string>> CustomApiMocks { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
