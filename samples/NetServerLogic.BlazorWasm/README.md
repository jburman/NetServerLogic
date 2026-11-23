# NetServerLogic Blazor WASM + Fluent UI Sample

Minimal sample demonstrating a **Blazor WebAssembly** app styled with **Microsoft Fluent UI** (no Bootstrap) hosted by an **ASP.NET Core** dev server wired up to **NetServerLogic** using live Dataverse certificate authentication.

## Project Layout

- `Server/` (`NetServerLogic.BlazorWasm.Server`) — Minimal ASP.NET Core dev host registering `AddPowerPagesServerLogicEmulator(...)` and `MapPowerPagesServerLogic()`, serving the Blazor WASM client.
- `Client/` (`NetServerLogic.BlazorWasm.Client`) — Standalone Blazor WASM app using `Microsoft.FluentUI.AspNetCore.Components` (no Bootstrap). Lists Dataverse `contacts` (Name & Email) via `GET /_api/serverlogics/contacts` and lets you edit a contact via `PATCH /_api/serverlogics/contacts`.
- `.powerpages-site/server-logic/contacts/contacts.js` — Unmodified Power Pages Server Logic pass-thru script (`get()` + `patch()`).

> **Local vs. Deployed Permissions Note:** Locally, `NetServerLogic` executes Dataverse calls using the configured Entra ID Application User, so `GET /_api/serverlogics/contacts` returns all contacts visible to that App User. When the same `contacts.js` is deployed to Power Pages, Power Pages table permissions automatically scope records to the signed-in portal user.

## Configuration

Update `Server/appsettings.Development.json` (or override via User Secrets / environment variables) with your Dataverse environment URL, Entra ID Application (Client) ID, and the thumbprint of your installed local certificate:

```json
{
  "PowerPagesServerLogic": {
    "ServerLogicRootPath": "../.powerpages-site/server-logic",
    "DataverseMode": "Live",
    "Dataverse": {
      "Url": "https://your-org.crm.dynamics.com",
      "ClientId": "00000000-0000-0000-0000-000000000000",
      "Certificate": {
        "Thumbprint": "0000000000000000000000000000000000000000",
        "StoreName": "My",
        "StoreLocation": "CurrentUser"
      }
    }
  }
}
```

## Running

```bash
dotnet run --project samples/NetServerLogic.BlazorWasm/Server
```
