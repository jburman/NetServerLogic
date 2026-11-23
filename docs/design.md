# NetServerLogic — Architecture & Implementation Design

## 1. Overview

This document specifies the design for **NetServerLogic**, a local **.NET 10** emulator for **Microsoft Power Pages Server Logic**, intended to run inside an ASP.NET Core development host (such as a Blazor WebAssembly or SPA dev host).

### Goals
* **Zero-Modification Deployment Parity:** Author Server Logic `.js` files directly inside `.powerpages-site/server-logic/{name}/{name}.js` and execute them locally via `/_api/serverlogics/{name}`. The exact same `.js` files must upload to Power Pages via `pac pages upload-code-site` (or `pac pages upload`) without transpilation, wrappers, or edits.
* **Pure Managed .NET Execution:** Run JavaScript inside ASP.NET Core middleware using `Jint` (4.x), targeting `net10.0` with zero native binary dependencies (works identically on `win-x64`, `win-arm64`, `linux-x64`, and `osx-arm64`).
* **Dual-Mode Dataverse Access:** Bridge `Server.Connector.Dataverse` synchronously into either:
  1. **Live Dataverse** via `Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.ExecuteWebRequest` (direct OData v9.2 Web API parity).
  2. **Local Mock Dataverse** backed by JSON seed fixtures and an in-memory CRUD + OData query store for offline development and automated testing.
* **Early Parity Guardrails:** Detect Power Pages sandbox violations (`RTSL01`), missing verb handlers, prohibited globals, synchronous/asynchronous misuse, and non-string return values locally with actionable diagnostics before code ever reaches Dataverse.

### Non-Goals
* Emulating the full Power Pages Liquid rendering engine or standard OData `/_api/{entityset}` table endpoints.
* Executing Node.js modules, npm packages, or ESM `import`/`export` graphs (Power Pages Server Logic does not support them).
* Managing Dataverse schema/solution deployment.

---

## 2. Solution & Project Structure

Design this as a reusable class library package (`NetServerLogic`) so it can be referenced directly from an ASP.NET Core dev host or distributed as a standalone NuGet package.

```text
src/
└── NetServerLogic/
    ├── NetServerLogic.csproj
    ├── Options/
    │   ├── ServerLogicEmulatorOptions.cs
    │   ├── MockUserOptions.cs
    │   └── DataverseMode.cs
    ├── Abstractions/
    │   ├── IServerLogicDataverseProvider.cs
    │   ├── IServerLogicHttpClient.cs
    │   ├── IServerLogicCloudFlowProvider.cs
    │   ├── IServerLogicScriptValidator.cs
    │   └── IServerLogicRoleProvider.cs
    ├── Runtime/
    │   ├── ServerLogicExecutionEngine.cs        # Per-request Jint sandbox runner
    │   ├── ServerLogicExecutionResult.cs
    │   ├── ServerGlobalFactory.cs               # Builds JS-native Server object graph
    │   └── JsObjectExtensions.cs                # Dictionary -> native JsObject helpers
    ├── Validation/
    │   ├── AcornimaScriptValidator.cs           # AST + token RTSL01 pre-flight checker
    │   └── ScriptValidationResult.cs
    ├── Connectors/
    │   ├── LiveDataverseSdkProvider.cs          # ServiceClient.ExecuteWebRequest bridge
    │   ├── MockDataverseProvider.cs             # JSON-seeded in-memory CRUD + OData
    │   ├── OData/
    │   │   └── SimpleODataQueryEvaluator.cs     # $select, $filter, $orderby, $top
    │   ├── ServerLogicHttpClientBridge.cs       # IHttpClientFactory + AllowedDomains
    │   └── MockCloudFlowProvider.cs             # Local stub / optional HTTP relay
    ├── Metadata/
    │   ├── ServerLogicFileResolver.cs           # Resolves .js and .serverlogic.yml
    │   └── ServerLogicYmlParser.cs              # Parses Web Role permissions from YAML
    └── Hosting/
        ├── ServerLogicServiceCollectionExtensions.cs
        ├── ServerLogicEndpointRouteBuilderExtensions.cs
        └── CsrfTokenEndpointExtensions.cs       # Optional /_layout/tokenhtml stub

tests/
└── NetServerLogic.Tests/
    ├── NetServerLogic.Tests.csproj
    ├── Runtime/
    │   ├── ExecutionEngineTests.cs
    │   └── ServerGlobalContractTests.cs
    ├── Validation/
    │   └── AcornimaScriptValidatorTests.cs
    ├── Connectors/
    │   ├── MockDataverseProviderTests.cs
    │   ├── SimpleODataQueryEvaluatorTests.cs
    │   └── HttpClientBridgeTests.cs
    └── Hosting/
        └── ServerLogicMiddlewareIntegrationTests.cs
```

### Target Framework & NuGet Dependencies
* **Target Framework:** `net10.0` (with `<FrameworkReference Include="Microsoft.AspNetCore.App" />`)
* **Packages:**
  * `Jint` (`4.x` — includes `Acornima` for JavaScript ES2023+ AST parsing)
  * `Microsoft.PowerPlatform.Dataverse.Client` (for `LiveDataverseSdkProvider`; optionally split into a separate `NetServerLogic.Dataverse` package if keeping the core library lighter is preferred)
  * `YamlDotNet` (for parsing `.serverlogic.yml` metadata)

---

## 3. File Layout & Routing Conventions

### Power Pages Directory Convention
The emulator reads files directly from the Power Pages site directory (configurable via `ServerLogicRootPath`, defaulting to `.powerpages-site/server-logic`):

```text
.powerpages-site/
└── server-logic/
    └── {name}/
        ├── {name}.js
        └── {name}.serverlogic.yml   (optional locally unless EnforceWebRoles = true)
```

* Scripts MUST be read fresh from disk on every request in development mode (no stale in-memory compilation cache) so edits to `.js` files take effect immediately on the next HTTP call.
* Sanitize `{name}` against path traversal (`..`, path separators, invalid filename characters) before resolving paths. Reject invalid names with HTTP `400 Bad Request`.

### HTTP Endpoint Mapping
Map the following routes via `app.MapPowerPagesServerLogic()`:

| Route | HTTP Methods | Purpose |
|---|---|---|
| `/_api/serverlogics/{name}` | `GET`, `POST`, `PUT`, `PATCH`, `DELETE` | Executes the matching verb handler inside `{name}.js`. |
| `/_layout/tokenhtml` | `GET` | Optional anti-forgery endpoint returning `<input name="__RequestVerificationToken" type="hidden" value="{dev-token}" />` when `EnableCsrfTokenEndpoint = true`. |

### HTTP Verb to JS Function Mapping
| HTTP Method | Required Top-Level JS Function |
|---|---|
| `GET` | `get()` |
| `POST` | `post()` |
| `PUT` | `put()` |
| `PATCH` | `patch()` |
| `DELETE` | `del()` *(not `delete`, which is a reserved JS keyword)* |

* Both synchronous (`function get() { ... }`) and asynchronous (`async function get() { ... }`) declarations must be supported.
* If the requested verb function is not defined in `{name}.js`, return **HTTP `405 Method Not Allowed`** with an `Allow` header listing the verbs that *are* exported by the script.

---

## 4. Emulating the `Server` Global Contract

Each HTTP request creates a fresh, isolated `Jint.Engine` instance with a `Server` global object matching Power Pages' runtime surface.

> **Important Jint Interop Rule:** Project dictionary-like objects (`QueryParameters`, `Headers`, `User`) as native `Jint.Native.JsObject` instances (not raw .NET `Dictionary<string, string>`), so JavaScript expressions like `Server.Context.QueryParameters.id`, `Server.Context.QueryParameters["id"]`, `Object.keys(...)`, optional chaining `?.`, and destructuring `const { id } = Server.Context.QueryParameters` all behave identically to V8/ES2023.

### 4.1 Complete API Surface

| Global Path | Sync / Async | Signature / Shape | Behavior |
|---|---|---|---|
| `Server.Context.QueryParameters` | Property | `JsObject` (`Record<string, string>`) | Populated from `HttpContext.Request.Query`. Case-insensitive lookup wrapper or normalized keys; multi-value params joined with `,` (matching ASP.NET `StringValues.ToString()`). |
| `Server.Context.Headers` | Property | `JsObject` (`Record<string, string>`) | Populated from `HttpContext.Request.Headers`. |
| `Server.Context.Body` | Property | `string` | Raw UTF-8 request body string (empty string `""` if no body). |
| `Server.Logger` | Sync | `Log(string)`, `Warn(string)`, `Error(string)` | Writes to `ILogger` category `ServerLogic.{name}` at `Information`, `Warning`, and `Error` levels. Accept `object?` / coerce non-strings via `.ToString()` so logging numbers/objects doesn't crash interop. |
| `Server.User` | Property | `JsObject` or `null` | `null` when anonymous. When authenticated: `{ contactid: string, fullname: string, emailaddress1: string, roles: string[] }`. Supports per-request override via optional dev header `X-PowerPages-Mock-User` (persona name from config). |
| `Server.SiteSetting` | Sync | `Get(string name): string \| null` | Reads from `PowerPagesServerLogic:SiteSettings` config section. |
| `Server.EnvironmentVariable` | Sync | `get(string name): string \| null` | Reads from `PowerPagesServerLogic:EnvironmentVariables` config section. *(Note lowercase `get` matching Power Pages SDK; optionally log a parity warning if `Get` is called.)* |
| `Server.Connector.Dataverse` | **Sync** | See Section 4.2 | **MUST be synchronous and return raw JSON `string`s.** Must NOT return Promises. |
| `Server.Connector.HttpClient` | **Async** | See Section 4.3 | **MUST return `Task<string>`** (automatically bridged to JS `Promise<string>` by Jint). |
| `Server.Connector.CloudFlow` | **Async** | `TriggerAsync(string flowUrlOrId, string payload): Promise<string>` | Delegates to `IServerLogicCloudFlowProvider`. |

### 4.2 `IServerLogicDataverseProvider` Contract

```csharp
public interface IServerLogicDataverseProvider
{
    string RetrieveRecord(string entitySetName, string id, string? options = null, bool skipCache = false);
    string RetrieveMultipleRecords(string entitySetName, string? options = null, bool skipCache = false);
    string CreateRecord(string entitySetName, string jsonPayload);
    string UpdateRecord(string entitySetName, string id, string jsonPayload);
    string DeleteRecord(string entitySetName, string id);
    string InvokeCustomApi(string httpMethod, string url, string? payload = null);
}
```

**Strict Parity Requirements:**
* `entitySetName` is the **plural OData entity set name** (e.g., `"accounts"`, `"contacts"`, `"cr123_orders"`), never the singular logical name.
* `id` accepts GUIDs with or without curly braces (`"00000000-..."` or `"{00000000-...}"`); normalize by trimming `{` and `}`.
* `options` is an OData query string with or without a leading `?` (e.g., `"$select=name&$filter=statecode eq 0"`).
* `jsonPayload` in `CreateRecord` / `UpdateRecord`: Power Pages expects a JSON string (`JSON.stringify(payload)`). If a JS script passes a raw JS object instead of a string, reject with a clear parity error in strict mode.
* All methods return a raw JSON `string` (or empty string `""` for `204 No Content` operations like `DeleteRecord`/`UpdateRecord`), matching the requirement that scripts call `JSON.parse(...)` on returned data.

### 4.3 `IServerLogicHttpClient` Contract

```csharp
public interface IServerLogicHttpClient
{
    Task<string> GetAsync(string url, IDictionary<string, object?>? headers = null);
    Task<string> PostAsync(string url, string body, IDictionary<string, object?>? headers = null, string? contentType = "application/json");
    Task<string> PutAsync(string url, string body, IDictionary<string, object?>? headers = null, string? contentType = "application/json");
    Task<string> PatchAsync(string url, string body, IDictionary<string, object?>? headers = null, string? contentType = "application/json");
    Task<string> DeleteAsync(string url, IDictionary<string, object?>? headers = null);
}
```

**Domain Allowlist Enforcement:**
* Parse `new Uri(url).Host` and check against `Options.AllowedDomains` (mirroring Power Pages' `ServerLogic/AllowedDomains` site setting).
* Support exact host matches and wildcard subdomains (e.g., `*.contoso.com`).
* If `AllowedDomains` is non-empty and the target host is not allowed, throw a descriptive `SecurityException` / parity error before issuing the outbound HTTP request.
* Support configurable response serialization (`HttpClientResponseMode`):
  * `StructuredJson` (default): JSON string containing `{ statusCode, headers, body }`
  * `RawBody`: raw response body string
  *(Verify against live Power Pages behavior in Section 11 and set the default accordingly.)*

---

## 5. JavaScript Runtime & Sandbox Configuration (`Jint`)

Each request executes inside `ServerLogicExecutionEngine`:

1. **Isolation & Timeouts:**
   * Create a new `Jint.Engine` per request (never reuse `Engine` instances across HTTP requests).
   * Create a linked `CancellationTokenSource` combining `HttpContext.RequestAborted` and `TimeSpan.FromSeconds(options.TimeoutInSeconds)` (default: `120` seconds, matching `ServerLogic/TimeoutInSeconds`).
   * Configure Jint options:
     ```csharp
     var engine = new Engine(opts =>
     {
         opts.Strict();
         opts.CancellationToken(cts.Token);
         opts.LimitRecursion(64);
         opts.LimitMemory(options.MaxMemoryBytes); // e.g., 64 MB default
         opts.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
     });
     ```
2. **Global Scrubbing:**
   * Explicitly remove or shadow non-Power Pages globals before running user code:
     `console`, `fetch`, `XMLHttpRequest`, `setTimeout`, `setInterval`, `clearTimeout`, `clearInterval`, `require`, `import`, `process`, `window`, `globalThis.console`.
   * Accessing `console.log` must fail locally just as it does in Power Pages, guiding the developer to use `Server.Logger.Log(...)`.
3. **Execution & Promise Unwrapping:**
   * Evaluate the script source via `engine.Execute(scriptSource, scriptPath)` so stack traces include the real filename and line number.
   * Resolve the target verb function (`get`, `post`, `put`, `patch`, `del`).
   * Invoke and await completion:
     ```csharp
     var rawResult = engine.Invoke(handlerFn);
     var resolved = await rawResult.UnwrapIfPromiseAsync(cts.Token);
     ```
4. **Return Value Enforcement:**
   * Verify `resolved.IsString()` is `true`.
   * If the function returns `undefined`, `null`, a JS object, or a number, return **HTTP `400 Bad Request`** with a clear diagnostic:
     `"Parity violation: '{verb}()' in '{name}.js' returned {resolved.Type}, but Power Pages Server Logic requires returning a string (e.g., JSON.stringify(result))."`

---

## 6. Pre-Flight Static Validator (`RTSL01` Parity Engine)

Power Pages runs a server-side script validator that rejects prohibited constructs with `RTSL01: Script validation failed`. Implement `AcornimaScriptValidator` using `Acornima.Parser` (already included with `Jint`) so validation operates on the real JavaScript AST rather than brittle string searches that trigger false positives inside comments or string literals.

### Validation Severity Levels
* **Error (`RTSL01`):** Always blocks execution and returns HTTP `400 Bad Request` with exact line/column numbers and rule ID.
* **Warning (`RTSL_WARN`):** Logged via `ILogger.LogWarning` by default; promoted to HTTP `400` when `Options.StrictValidatorMode = true`.

### Validation Ruleset

| Rule ID | Severity | AST / Pattern Check | Rationale |
|---|---|---|---|
| `RTSL01-DELETE` | Error | `UnaryExpression` with `Operator.Delete` (`delete obj.prop`) | Power Pages blocks the `delete` keyword; use `obj.prop = undefined`. |
| `RTSL01-WITH` | Error | `WithStatement` | Blocked by strict mode and Power Pages sandbox. |
| `RTSL01-DEBUGGER` | Error | `DebuggerStatement` | Blocked by Power Pages validator. |
| `RTSL01-MODULE` | Error | `ImportDeclaration`, `ExportDeclaration`, `ImportExpression`, or call to `require(...)` | No module loading in Server Logic. |
| `RTSL01-METAPROG` | Error | Identifiers/member access to `prototype`, `__proto__`, `Proxy`, `Reflect`, `arguments.callee`, `Symbol.for`, `Object.getPrototypeOf`, `Object.setPrototypeOf`, `constructor.constructor`, `this.constructor` | Prototype pollution / sandbox escape blocks. |
| `RTSL01-DYNAMIC-CODE` | Error | Calls to `eval(...)` or `new Function(...)` | Dynamic code evaluation is prohibited. |
| `RTSL01-GLOBALS` | Error | Unshadowed references to `console`, `fetch`, `XMLHttpRequest`, `setTimeout`, `setInterval` | Not available in Power Pages runtime. |
| `RTSL01-SYNC-AWAIT` | Error | `AwaitExpression` whose argument is a `CallExpression` on `Server.Connector.Dataverse.*` | `Server.Connector.Dataverse` is strictly synchronous; `await` on it is blocked in Power Pages. |
| `RTSL01-NO-HANDLER` | Error | No top-level `get`, `post`, `put`, `patch`, or `del` function defined | Script has no callable HTTP entry point. |
| `RTSL_WARN-ARROW-CONST` | Warning | Top-level `const`/`let` initialized to `ArrowFunctionExpression` or `FunctionExpression` | Known Power Pages validator quirk that can trigger HTTP 400 in production; prefer `function name() {}`. |
| `RTSL_WARN-ARRAY-CALLBACK` | Warning | Anonymous arrow/function expressions passed to `.map()`, `.filter()`, `.forEach()` | Documented validator quirk in some Power Pages builds; warn developer to prefer `for` loops if deploying to affected versions. |

---

## 7. Dataverse Providers

### 7.1 `LiveDataverseSdkProvider`
Bridges directly to Dataverse's Web API (`api/data/v9.2/`) through `Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.ExecuteWebRequest`:

* **Why `ExecuteWebRequest` instead of `IOrganizationService`:** `Server.Connector.Dataverse` speaks plural OData entity sets, OData query options (`$select`, `$expand`, `$filter`), and raw JSON payloads. `ExecuteWebRequest` reuses `ServiceClient`'s authenticated token cache, retry policies, and connection pooling while delivering 100% Web API wire parity without translating OData to `QueryExpression`.
* **Headers:** Attach `OData-Version: 4.0`, `OData-MaxVersion: 4.0`, and `Prefer: odata.include-annotations="*"` (and optionally `Prefer: return=representation` on `POST`/`PATCH` depending on `CreateRecordReturnMode`).
* **Authentication:** Accept an injected `ServiceClient` from DI, a connection string configured via User Secrets / environment variables (`PowerPagesServerLogic:Dataverse:ConnectionString`), or structured certificate-based authentication (`PowerPagesServerLogic:Dataverse:Url`, `ClientId`, and `Certificate` supporting X.509 store thumbprint, `.pfx`/`.p12`/`.pem` files, base64-encoded payloads, or a programmatic `X509Certificate2` instance). Never store secrets or private key material in `appsettings.json`.
* **Error Handling:** When `response.IsSuccessStatusCode` is `false`, throw an exception including HTTP status code and Dataverse error body so the JS `try/catch` block receives a realistic error message.

### 7.2 `MockDataverseProvider`
Enables zero-cloud local development and deterministic integration testing:

* **Seed Loading:** On startup (or lazy per entity set), load JSON files from `Options.MockDataPath` (default: `.serverlogic-mocks/{entitySetName}.json`). Each file contains a JSON array of Dataverse-shaped objects:
  ```json
  [
    {
      "accountid": "11111111-1111-1111-1111-111111111111",
      "name": "Contoso Ltd",
      "revenue": 1500000,
      "statecode": 0
    }
  ]
  ```
* **Primary Key Detection:** Infer the primary key property by convention (`{singularEntityName}id` or explicit `id` / first property ending in `id` with a GUID value), or via optional `PrimaryKeyOverrides` dictionary in options.
* **In-Memory CRUD:**
  * `RetrieveMultipleRecords`: Returns `{ "@odata.context": "...", "value": [ ... ] }` after applying `SimpleODataQueryEvaluator`.
  * `RetrieveRecord`: Finds record by GUID, applies `$select` if present, returns serialized JSON object; throws 404-style error if not found.
  * `CreateRecord`: Parses JSON payload, generates a new GUID if primary key is missing, stores in-memory, and returns JSON containing the created ID / representation.
  * `UpdateRecord`: Merges properties from JSON payload onto existing record in-memory (shallow merge of JSON properties); throws 404-style error if not found.
  * `DeleteRecord`: Removes record by GUID; returns `""`.
  * `InvokeCustomApi`: Looks up a registered delegate in `MockCustomApiHandlers` dictionary (keyed by `"{METHOD} {url}"`) or falls back to `.serverlogic-mocks/custom-apis/{sanitizedUrl}.json`.
* **Simple OData Query Evaluator (`SimpleODataQueryEvaluator`):**
  * `$select=col1,col2` — projects only selected keys (plus primary key if needed).
  * `$top=N` — limits result count.
  * `$orderby=col asc|desc` — sorts numbers, strings, booleans, and ISO dates.
  * `$filter=...` — supports `eq`, `ne`, `gt`, `ge`, `lt`, `le`, `contains(field,'val')`, `startswith(field,'val')`, `endswith(field,'val')`, and boolean combinators `and` / `or` with parentheses.
  * Log a warning if an unsupported query option (e.g., complex `$expand` or `$apply`) is encountered in mock mode rather than failing silently.

---

## 8. Metadata, RBAC & CSRF Parity

### 8.1 `.serverlogic.yml` Role Enforcement
When `Options.EnforceWebRoles = true`:
* Parse `.powerpages-site/server-logic/{name}/{name}.serverlogic.yml` (and any companion web role mapping files if present) using `YamlDotNet`.
* Determine the set of allowed Web Roles for the script.
* Evaluate against `Server.User`:
  * If `Anonymous Users` is allowed -> permit even when `Server.User == null`.
  * If `Server.User == null` and anonymous is not allowed -> return **HTTP `401 Unauthorized`**.
  * If `Server.User != null` and none of `Server.User.roles` intersect the script's allowed roles (and `Authenticated Users` is not allowed) -> return **HTTP `403 Forbidden`**.

### 8.2 CSRF Token Emulation
When `Options.RequireCsrfToken = true`:
* Expose `GET /_layout/tokenhtml` returning:
  ```html
  <input name="__RequestVerificationToken" type="hidden" value="dev-local-csrf-token" />
  ```
* Require the `__RequestVerificationToken` header on all requests to `/_api/serverlogics/{name}` (matching the configured dev token). Return **HTTP `403 Forbidden`** if missing or mismatched.
* This validates that the Blazor WASM `DelegatingHandler` correctly fetches and attaches anti-forgery tokens before deploying to Power Pages.

---

## 9. Configuration & Hosting Integration

### 9.1 Configuration Schema (`appsettings.Development.json`)

```json
{
  "PowerPagesServerLogic": {
    "ServerLogicRootPath": ".powerpages-site/server-logic",
    "DataverseMode": "Mock",
    "MockDataPath": ".serverlogic-mocks",
    "TimeoutInSeconds": 120,
    "MaxMemoryBytes": 67108864,
    "StrictValidatorMode": false,
    "RequireCsrfToken": false,
    "EnableCsrfTokenEndpoint": true,
    "EnforceWebRoles": false,
    "ResponseFormat": "RawString",
    "HttpClientResponseMode": "StructuredJson",
    "AllowedDomains": [
      "api.github.com",
      "*.contoso.com"
    ],
    "Dataverse": {
      "ConnectionString": ""
    },
    "SiteSettings": {
      "ServerLogic/TimeoutInSeconds": "120",
      "Custom/FeatureFlag": "true"
    },
    "EnvironmentVariables": {
      "cr123_ExternalApiBaseUrl": "[https://api.contoso.com](https://api.contoso.com)"
    },
    "DefaultMockUser": "StandardUser",
    "MockUsers": {
      "StandardUser": {
        "ContactId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        "FullName": "Dev Portal User",
        "EmailAddress1": "devuser@contoso.com",
        "Roles": [ "Authenticated Users", "Portal Member" ]
      },
      "AdminUser": {
        "ContactId": "99999999-8888-7777-6666-555555555555",
        "FullName": "Dev Portal Admin",
        "EmailAddress1": "admin@contoso.com",
        "Roles": [ "Authenticated Users", "Administrators" ]
      }
    }
  }
}
```

### 9.2 ASP.NET Core Integration (Blazor WASM Dev Host)

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPowerPagesServerLogicEmulator(
    builder.Configuration.GetSection("PowerPagesServerLogic"),
    emulator =>
    {
        // Optional programmatic customization
        emulator.AddCustomApiMock("POST cr123_CalculateDiscount", (payload, ct) =>
            """{"discountPercent": 15}""");
    });

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.MapPowerPagesServerLogic(); // Maps /_api/serverlogics/{name} and optional /_layout/tokenhtml
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
```

---

## 10. Golden Test Fixture (Example Server Logic Script)

The emulator MUST execute the following unmodified Power Pages Server Logic script identically to the cloud runtime:

```javascript
// .powerpages-site/server-logic/accounts-summary/accounts-summary.js

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
```

---

## 11. Live Power Pages Parity Verification Checklist

Before finalizing defaults for production usage, capture one live `200 OK` response from a deployed Power Pages site for each of these three wire-format behaviors and align the corresponding option defaults in `ServerLogicEmulatorOptions`:

1. **HTTP Response Envelope (`ResponseFormat`):** Confirm whether `/_api/serverlogics/{name}` returns the JS string directly as the HTTP response body (`RawString`) or wrapped in a JSON envelope (`WrappedEnvelope`, e.g., `{ "result": "..." }`) on the target Power Pages runtime version.
2. **`CreateRecord` Return Payload:** Confirm whether `Server.Connector.Dataverse.CreateRecord` returns a JSON string containing the new record ID (e.g., `{"id":"..."}` or full entity representation via `Prefer: return=representation`) or an empty string, and mirror that format in both `LiveDataverseSdkProvider` and `MockDataverseProvider`.
3. **`HttpClient.*Async` Return Shape (`HttpClientResponseMode`):** Confirm whether `Server.Connector.HttpClient.GetAsync` resolves to a JSON string containing `{ statusCode, headers, body }` or the raw response body string.

---

## 12. Phased Implementation Plan (For Coding Agent)

Execute the implementation in the following order, running `dotnet test` after each phase:

### Phase 1 — Core Runtime & Middleware
1. Scaffold `NetServerLogic` and `NetServerLogic.Tests` targeting `net10.0`.
2. Implement `ServerLogicEmulatorOptions`, core interfaces, `ServerLogicFileResolver` (with path-traversal protection), and `JsObjectExtensions`.
3. Implement `ServerGlobalFactory` and `ServerLogicExecutionEngine` using `Jint` 4.x (`Strict()`, `CancellationToken`, `ExperimentalFeature.TaskInterop`, `UnwrapIfPromiseAsync`).
4. Implement global scrubbing (`console`, `fetch`, `setTimeout`, etc.) and strict string return-type enforcement.
5. Implement `MapPowerPagesServerLogic()` endpoint routing (`GET`/`POST`/`PUT`/`PATCH`/`DELETE` -> `get`/`post`/`put`/`patch`/`del`, `404`, `405`, `400`, `500` handling).

### Phase 2 — Pre-Flight `RTSL01` AST Validator
1. Implement `AcornimaScriptValidator` walking the parsed AST to enforce every rule in Section 6.
2. Ensure string literals and comments containing blocked words (e.g., `"delete"` in a log message or `// prototype`) do **not** trigger false positives.
3. Wire validator into `ServerLogicExecutionEngine` prior to `engine.Execute(...)`, returning structured `RTSL01` JSON errors with line and column numbers.

### Phase 3 — Mock Dataverse, OData Evaluator, HttpClient & CloudFlow
1. Implement `SimpleODataQueryEvaluator` supporting `$select`, `$top`, `$orderby`, and `$filter` (`eq`, `ne`, `gt`, `ge`, `lt`, `le`, `contains`, `startswith`, `endswith`, `and`, `or`, parentheses).
2. Implement `MockDataverseProvider` loading seed JSON files from `MockDataPath` with thread-safe in-memory CRUD and custom API mocks.
3. Implement `ServerLogicHttpClientBridge` backed by `IHttpClientFactory` with `AllowedDomains` wildcard enforcement.
4. Implement `MockCloudFlowProvider`.

### Phase 4 — Live Dataverse SDK Bridge, RBAC & CSRF
1. Implement `LiveDataverseSdkProvider` using `ServiceClient.ExecuteWebRequest` with synchronous execution and OData v9.2 headers.
2. Implement `ServerLogicYmlParser` using `YamlDotNet` and enforce Web Roles against `Server.User.roles` when `EnforceWebRoles = true`.
3. Implement `X-PowerPages-Mock-User` persona switching and optional `/_layout/tokenhtml` CSRF endpoint + header enforcement.

### Phase 5 — Test Suite & Acceptance Verification
Write and verify passing xUnit tests covering:
* **Execution Engine:** Sync `get()`, async `post()`, `DELETE` -> `del()`, missing verb -> `405`, missing script -> `404`, path traversal attempt -> `400`, non-string return value -> `400`, timeout cancellation -> `408`/`504`, `console.log` throws error, hot-reload reflects on-disk file edits immediately.
* **AST Validator:** Blocks `delete obj.x`, `eval()`, `import`, `require()`, `prototype`, `await Server.Connector.Dataverse.RetrieveRecord(...)`; ignores `"delete"` inside string literals and comments; emits warnings for `const fn = () => {}` and elevates them when `StrictValidatorMode = true`.
* **Mock Dataverse & OData:** Seed loading, `CreateRecord` -> `RetrieveRecord` -> `UpdateRecord` -> `DeleteRecord` lifecycle, `$select`, `$orderby`, `$top`, compound `$filter` expressions, and `InvokeCustomApi`.
* **HttpClient Bridge:** Allowed domain succeeds, disallowed domain rejected before network call, custom headers forwarded.
* **Integration (`WebApplicationFactory`):** Full end-to-end execution of the Section 10 golden script, CSRF rejection/acceptance, and Web Role `401`/`403`/`200` enforcement.