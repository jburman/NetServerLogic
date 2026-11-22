namespace NetServerLogic.Abstractions;

/// <summary>
/// Asynchronous outbound HTTP client contract exposed to Server Logic scripts at <c>Server.Connector.HttpClient</c>.
/// </summary>
public interface IServerLogicHttpClient
{
    Task<string> GetAsync(string url, IDictionary<string, object?>? headers = null);

    Task<string> PostAsync(string url, string body, IDictionary<string, object?>? headers = null, string? contentType = "application/json");

    Task<string> PutAsync(string url, string body, IDictionary<string, object?>? headers = null, string? contentType = "application/json");

    Task<string> PatchAsync(string url, string body, IDictionary<string, object?>? headers = null, string? contentType = "application/json");

    Task<string> DeleteAsync(string url, IDictionary<string, object?>? headers = null);
}
