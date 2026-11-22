namespace NetServerLogic.Abstractions;

/// <summary>
/// Synchronous Dataverse connector contract exposed to Server Logic scripts at <c>Server.Connector.Dataverse</c>.
/// </summary>
public interface IServerLogicDataverseProvider
{
    string RetrieveRecord(string entitySetName, string id, string? options = null, bool skipCache = false);

    string RetrieveMultipleRecords(string entitySetName, string? options = null, bool skipCache = false);

    string CreateRecord(string entitySetName, string jsonPayload);

    string UpdateRecord(string entitySetName, string id, string jsonPayload);

    string DeleteRecord(string entitySetName, string id);

    string InvokeCustomApi(string httpMethod, string url, string? payload = null);
}
