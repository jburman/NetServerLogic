namespace NetServerLogic.Options;

/// <summary>
/// Controls how <c>Server.Connector.Dataverse</c> resolves Dataverse operations.
/// </summary>
public enum DataverseMode
{
    /// <summary>
    /// Uses the local JSON-seeded in-memory Dataverse store.
    /// </summary>
    Mock,

    /// <summary>
    /// Connects to a live Dataverse environment via <c>ServiceClient.ExecuteWebRequest</c>.
    /// </summary>
    Live
}
