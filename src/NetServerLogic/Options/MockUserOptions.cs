namespace NetServerLogic.Options;

/// <summary>
/// Represents an authenticated portal user persona exposed via <c>Server.User</c>.
/// </summary>
public sealed class MockUserOptions
{
    public string ContactId { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public string EmailAddress1 { get; set; } = string.Empty;

    public List<string> Roles { get; set; } = new();
}
