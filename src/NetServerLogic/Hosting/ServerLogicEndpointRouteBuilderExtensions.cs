using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetServerLogic.Abstractions;
using NetServerLogic.Metadata;
using NetServerLogic.Options;
using NetServerLogic.Runtime;

namespace NetServerLogic.Hosting;

/// <summary>
/// Maps Power Pages Server Logic HTTP endpoints (<c>/_api/serverlogics/{name}</c> and optional CSRF token stub).
/// </summary>
public static class ServerLogicEndpointRouteBuilderExtensions
{
    private static readonly string[] SupportedHttpMethods =
    [
        HttpMethods.Get,
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete
    ];

    public static IEndpointRouteBuilder MapPowerPagesServerLogic(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<ServerLogicEmulatorOptions>>().Value;

        if (options.EnableCsrfTokenEndpoint)
        {
            endpoints.MapPowerPagesCsrfTokenEndpoint();
        }

        endpoints.MapMethods("/_api/serverlogics/{name}", SupportedHttpMethods, HandleServerLogicRequestAsync);

        return endpoints;
    }

    private static async Task<IResult> HandleServerLogicRequestAsync(
        HttpContext context,
        string name)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<IOptions<ServerLogicEmulatorOptions>>().Value;
        var resolver = services.GetRequiredService<ServerLogicFileResolver>();
        var roleProvider = services.GetRequiredService<IServerLogicRoleProvider>();
        var executionEngine = services.GetRequiredService<ServerLogicExecutionEngine>();

        // 1. Validate & resolve script path (400 on path traversal / invalid name, 404 if missing)
        var resolution = resolver.Resolve(name);
        if (resolution.Status == ServerLogicResolutionStatus.InvalidName)
        {
            return Results.Json(
                new { errorCode = "INVALID_SCRIPT_NAME", message = resolution.ErrorMessage },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (resolution.Status == ServerLogicResolutionStatus.NotFound || resolution.ScriptPath is null)
        {
            return Results.Json(
                new { errorCode = "SCRIPT_NOT_FOUND", message = resolution.ErrorMessage },
                statusCode: StatusCodes.Status404NotFound);
        }

        // 2. Enforce optional anti-forgery token parity
        if (options.RequireCsrfToken)
        {
            if (!context.Request.Headers.TryGetValue(CsrfTokenEndpointExtensions.TokenHeaderAndFieldName, out var tokenHeader) ||
                !string.Equals(tokenHeader.ToString(), options.CsrfTokenValue, StringComparison.Ordinal))
            {
                return Results.Json(
                    new
                    {
                        errorCode = "CSRF_TOKEN_INVALID",
                        message = $"Missing or invalid '{CsrfTokenEndpointExtensions.TokenHeaderAndFieldName}' header."
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        // 3. Resolve mock user persona and enforce optional Web Roles
        var resolvedUser = ServerGlobalFactory.ResolveUser(context.Request, options);
        if (options.EnforceWebRoles)
        {
            var allowedRoles = await roleProvider
                .GetAllowedRolesAsync(resolution.ScriptName, resolution.YmlPath, context.RequestAborted)
                .ConfigureAwait(false);

            var authFailure = EvaluateWebRoles(resolvedUser, allowedRoles);
            if (authFailure is not null)
            {
                return authFailure;
            }
        }

        // 4. Read request body & fresh script from disk, then execute
        var requestBody = await ReadBodyAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        var scriptSource = await resolver.ReadScriptAsync(resolution.ScriptPath, context.RequestAborted).ConfigureAwait(false);

        var result = await executionEngine.ExecuteAsync(
            resolution.ScriptName,
            resolution.ScriptPath,
            scriptSource,
            context.Request.Method,
            context.Request,
            requestBody,
            resolvedUser,
            context.RequestAborted).ConfigureAwait(false);

        if (result.StatusCode == StatusCodes.Status405MethodNotAllowed)
        {
            if (result.AllowedMethods is { Count: > 0 })
            {
                context.Response.Headers.Allow = string.Join(", ", result.AllowedMethods);
            }

            return Results.Json(
                new
                {
                    errorCode = result.ErrorCode,
                    message = result.ErrorMessage,
                    allowedMethods = result.AllowedMethods
                },
                statusCode: StatusCodes.Status405MethodNotAllowed);
        }

        if (!result.IsSuccess)
        {
            return Results.Json(
                new
                {
                    errorCode = result.ErrorCode,
                    message = result.ErrorMessage,
                    diagnostics = result.ValidationDiagnostics
                },
                statusCode: result.StatusCode);
        }

        if (options.ResponseFormat == ServerLogicResponseFormat.WrappedEnvelope)
        {
            return Results.Json(new { result = result.Output ?? string.Empty });
        }

        return Results.Text(
            result.Output ?? string.Empty,
            "application/json",
            Encoding.UTF8,
            StatusCodes.Status200OK);
    }

    private static IResult? EvaluateWebRoles(
        MockUserOptions? user,
        IReadOnlyCollection<string>? allowedRoles)
    {
        if (allowedRoles is null || allowedRoles.Count == 0)
        {
            return user is null
                ? Results.Json(new { errorCode = "UNAUTHORIZED", message = "Authentication required." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Json(new { errorCode = "FORBIDDEN", message = "No Web Roles permit access to this Server Logic script." }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (allowedRoles.Any(r => r.Equals("Anonymous Users", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (user is null)
        {
            return Results.Json(
                new { errorCode = "UNAUTHORIZED", message = "Anonymous access is not permitted for this Server Logic script." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (allowedRoles.Any(r => r.Equals("Authenticated Users", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var permitted = user.Roles.Any(userRole =>
            allowedRoles.Any(allowed => allowed.Equals(userRole, StringComparison.OrdinalIgnoreCase)));

        if (!permitted)
        {
            return Results.Json(
                new { errorCode = "FORBIDDEN", message = "Current user does not belong to an authorized Web Role." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.Body is null || request.Body == Stream.Null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
