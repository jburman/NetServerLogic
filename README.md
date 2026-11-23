# NetServerLogic

A local **.NET 10** emulator for **Microsoft Power Pages Server Logic** designed to run inside an ASP.NET Core development host (such as a Blazor WebAssembly or SPA dev host).

Author your Power Pages Server Logic `.js` scripts directly in `.powerpages-site/server-logic/{name}/{name}.js`, execute and debug them locally at `/_api/serverlogics/{name}`, and deploy the exact same unmodified files to Power Pages with `pac pages upload-code-site` / `pac pages upload`.

## Key Features

- **Zero-Modification Deployment Parity:** Reads `.powerpages-site/server-logic/{name}/{name}.js` fresh from disk on every request (instant hot-reload) and maps `GET`, `POST`, `PUT`, `PATCH`, `DELETE` to top-level `get()`, `post()`, `put()`, `patch()`, and `del()` handlers.
- **Pure Managed .NET 10 Sandbox:** Runs JavaScript inside isolated per-request `Jint` 4.x engines (`net10.0`, no native binaries) with strict mode, timeout cancellation, memory/recursion limits, global scrubbing (`console`, `fetch`, `setTimeout`, etc.), and native `Task`/`Promise` interop.
- **Dual-Mode Dataverse Access:**
  - **Mock Mode (`MockDataverseProvider`):** JSON-seeded in-memory CRUD + OData v4 query evaluation (`$select`, `$filter`, `$orderby`, `$top`) and custom API mocks for offline dev and integration testing.
  - **Live Mode (`LiveDataverseSdkProvider`):** Bridges `Server.Connector.Dataverse` synchronously to Dataverse Web API v9.2 via `ServiceClient.ExecuteWebRequest`.
- **Pre-Flight `RTSL01` AST Validator:** Uses Acornima's JavaScript AST parser to catch Power Pages sandbox violations (`delete`, `with`, `debugger`, `import`/`export`/`require`, prototype/metaprogramming access, `eval`/`new Function`, prohibited globals, `await` on synchronous Dataverse calls, missing handlers) without false positives in comments or strings.
- **RBAC & CSRF Parity:** Optional `.serverlogic.yml` Web Role enforcement (including sibling `web-roles/*.webrole.yml` GUID-to-name resolution), per-request user persona switching via `X-PowerPages-Mock-User`, and `/_layout/tokenhtml` + `__RequestVerificationToken` enforcement.

## Quick Start

### 1. Register & Map in ASP.NET Core

```csharp
using NetServerLogic.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPowerPagesServerLogicEmulator(
    builder.Configuration.GetSection("PowerPagesServerLogic"),
    emulator =>
    {
        emulator.AddCustomApiMock("POST cr123_CalculateDiscount", (payload, ct) =>
            """{"discountPercent": 15}""");
    });

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapPowerPagesServerLogic();
}

app.Run();
```

### 2. Configure `appsettings.Development.json`

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
      "ConnectionString": "",
      "Url": "",
      "ClientId": "",
      "Certificate": {
        "Thumbprint": "",
        "StoreName": "My",
        "StoreLocation": "CurrentUser"
      }
    },
    "SiteSettings": {
      "Custom/FeatureFlag": "true"
    },
    "EnvironmentVariables": {
      "cr123_ExternalApiBaseUrl": "https://api.contoso.com"
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

> **Security note:** For `DataverseMode: "Live"`, store credentials (`PowerPagesServerLogic:Dataverse:ConnectionString`, `Certificate:Password`, or `Certificate:Base64`) in User Secrets or environment variables—never commit secrets or private key files in `appsettings.json`.

#### Live Dataverse Authentication Options

When `DataverseMode` is set to `"Live"`, `LiveDataverseSdkProvider` supports:

1. **Injected `ServiceClient` from DI** (if registered in your ASP.NET Core container).
2. **Connection string** (`PowerPagesServerLogic:Dataverse:ConnectionString`), including `AuthType=ClientSecret`, `AuthType=OAuth`, or `AuthType=Certificate`.
3. **Structured certificate-based authentication** (`PowerPagesServerLogic:Dataverse:Url`, `ClientId`, and `Certificate`) using any one of:
   - **OS Certificate Store:** `Certificate:Thumbprint` with optional `StoreName` (default `My`), `StoreLocation` (default `CurrentUser`), and `ValidOnly` (default `false`).
   - **Certificate File (`.pfx`/`.p12` or `.pem`):** `Certificate:Path` (relative to content root or absolute), optional `KeyPath` (separate PEM private key), optional `Password`, and optional `KeyStorageFlags`.
   - **Base64 Payload:** `Certificate:Base64` (base64-encoded PKCS#12 or combined PEM) with optional `Password` and `KeyStorageFlags`.
   - **Programmatic `X509Certificate2`:** `emulator.UseDataverseCertificate(url, clientId, cert)` (or `options.Dataverse.Certificate = new DataverseCertificateOptions { Instance = cert }`).

### 3. Add a Server Logic Script & Mock Data

Create `.powerpages-site/server-logic/accounts-summary/accounts-summary.js`:

```javascript
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
```

Optionally seed mock records in `.serverlogic-mocks/accounts.json`:

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

Then invoke `GET /_api/serverlogics/accounts-summary?minRevenue=100000`.

## Emulated `Server` Global Surface

| API | Mode | Description |
|---|---|---|
| `Server.Context.QueryParameters` | Property | Native JS object with case-insensitive lookup; multi-value params comma-joined. |
| `Server.Context.Headers` | Property | Native JS object with case-insensitive lookup. |
| `Server.Context.Body` | Property | Raw UTF-8 request body string (`""` when empty). |
| `Server.Logger` | Sync | `Log(...)`, `Warn(...)`, `Error(...)` writing to `ILogger` category `ServerLogic.{name}`. |
| `Server.User` | Property | `null` (anonymous) or `{ contactid, fullname, emailaddress1, roles }`. Override per request with `X-PowerPages-Mock-User`. |
| `Server.SiteSetting.Get(name)` | Sync | Reads from `PowerPagesServerLogic:SiteSettings`. |
| `Server.EnvironmentVariable.get(name)` | Sync | Reads from `PowerPagesServerLogic:EnvironmentVariables`. |
| `Server.Connector.Dataverse` | **Sync** | `RetrieveRecord`, `RetrieveMultipleRecords`, `CreateRecord`, `UpdateRecord`, `DeleteRecord`, `InvokeCustomApi` returning raw JSON strings. |
| `Server.Connector.HttpClient` | **Async** | `GetAsync`, `PostAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync` enforcing `AllowedDomains`. |
| `Server.Connector.CloudFlow` | **Async** | `TriggerAsync(flowUrlOrId, payload)` returning a `Promise<string>`. |

## Pre-Flight `RTSL01` Validation Rules

| Rule ID | Severity | Checked Construct |
|---|---|---|
| `RTSL01-DELETE` | Error | `delete obj.prop` |
| `RTSL01-WITH` | Error | `with (...)` |
| `RTSL01-DEBUGGER` | Error | `debugger` |
| `RTSL01-MODULE` | Error | `import`, `export`, `import()`, `require()` |
| `RTSL01-METAPROG` | Error | `prototype`, `__proto__`, `Proxy`, `Reflect`, `arguments.callee`, `Symbol.for`, `Object.getPrototypeOf`, `Object.setPrototypeOf`, `constructor.constructor`, `this.constructor` |
| `RTSL01-DYNAMIC-CODE` | Error | `eval()`, `new Function()` / `Function()` |
| `RTSL01-GLOBALS` | Error | Unshadowed `console`, `fetch`, `XMLHttpRequest`, `setTimeout`, `setInterval` (and `globalThis.*`) |
| `RTSL01-SYNC-AWAIT` | Error | `await Server.Connector.Dataverse.*` |
| `RTSL01-NO-HANDLER` | Error | Missing top-level `get`, `post`, `put`, `patch`, or `del` function |
| `RTSL_WARN-ARROW-CONST` | Warning | Top-level `const`/`let` initialized to arrow/function expression (elevated to error when `StrictValidatorMode = true`) |
| `RTSL_WARN-ARRAY-CALLBACK` | Warning | Inline function/arrow callback in `.map()`, `.filter()`, `.forEach()` (elevated when `StrictValidatorMode = true`) |

## Building & Testing

Requires the **.NET 10 SDK**.

```bash
dotnet build NetServerLogic.slnx --configuration Release
dotnet test NetServerLogic.slnx --configuration Release
```

For full architectural details, see [docs/design.md](docs/design.md).

## License

Licensed under the [MIT License](LICENSE).
