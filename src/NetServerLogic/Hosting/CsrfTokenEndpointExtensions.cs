using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetServerLogic.Options;

namespace NetServerLogic.Hosting;

/// <summary>
/// Maps the optional local Power Pages anti-forgery token endpoint (<c>/_layout/tokenhtml</c>).
/// </summary>
public static class CsrfTokenEndpointExtensions
{
    public const string DefaultRoute = "/_layout/tokenhtml";
    public const string TokenHeaderAndFieldName = "__RequestVerificationToken";

    public static IEndpointConventionBuilder MapPowerPagesCsrfTokenEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern = DefaultRoute)
    {
        return endpoints.MapGet(pattern, (HttpContext context) =>
        {
            var options = context.RequestServices.GetRequiredService<IOptions<ServerLogicEmulatorOptions>>().Value;
            var encodedToken = WebUtility.HtmlEncode(options.CsrfTokenValue);
            var html = $"<input name=\"{TokenHeaderAndFieldName}\" type=\"hidden\" value=\"{encodedToken}\" />";
            return Results.Content(html, "text/html; charset=utf-8");
        });
    }
}
