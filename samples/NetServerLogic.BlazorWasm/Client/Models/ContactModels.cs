using System.Text.Json.Serialization;

namespace NetServerLogic.BlazorWasm.Client.Models;

public sealed class ODataCollectionResponse<T>
{
    [JsonPropertyName("value")]
    public List<T> Value { get; set; } = [];
}

public sealed class ContactRecord
{
    [JsonPropertyName("contactid")]
    public string ContactId { get; set; } = string.Empty;

    [JsonPropertyName("firstname")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastname")]
    public string? LastName { get; set; }

    [JsonPropertyName("fullname")]
    public string? FullName { get; set; }

    [JsonPropertyName("emailaddress1")]
    public string? EmailAddress1 { get; set; }

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FullName))
            {
                return FullName;
            }

            var combined = $"{FirstName} {LastName}".Trim();
            return string.IsNullOrEmpty(combined) ? "(Unnamed)" : combined;
        }
    }
}

public sealed class UpdateContactRequest
{
    [JsonPropertyName("contactid")]
    public string ContactId { get; set; } = string.Empty;

    [JsonPropertyName("firstname")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastname")]
    public string? LastName { get; set; }

    [JsonPropertyName("emailaddress1")]
    public string? EmailAddress1 { get; set; }
}
