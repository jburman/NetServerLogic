using System.Net.Http.Json;
using System.Text.Json;
using NetServerLogic.BlazorWasm.Client.Models;

namespace NetServerLogic.BlazorWasm.Client.Services;

/// <summary>
/// Calls the pass-thru Power Pages Server Logic endpoint at <c>/_api/serverlogics/contacts</c>.
/// </summary>
public sealed class ContactsServerLogicClient(HttpClient httpClient)
{
    private const string Endpoint = "/_api/serverlogics/contacts";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<List<ContactRecord>> GetContactsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(Endpoint, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GET {Endpoint} failed ({(int)response.StatusCode} {response.ReasonPhrase}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<ODataCollectionResponse<ContactRecord>>(body, JsonOptions);
        return parsed?.Value ?? [];
    }

    public async Task<ContactRecord> UpdateContactAsync(
        UpdateContactRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PatchAsJsonAsync(Endpoint, request, JsonOptions, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"PATCH {Endpoint} failed ({(int)response.StatusCode} {response.ReasonPhrase}): {body}");
        }

        return JsonSerializer.Deserialize<ContactRecord>(body, JsonOptions)
            ?? new ContactRecord
            {
                ContactId = request.ContactId,
                FirstName = request.FirstName,
                LastName = request.LastName,
                EmailAddress1 = request.EmailAddress1
            };
    }
}
