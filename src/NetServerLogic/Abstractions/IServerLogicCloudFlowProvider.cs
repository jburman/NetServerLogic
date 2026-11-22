namespace NetServerLogic.Abstractions;

/// <summary>
/// Asynchronous Cloud Flow trigger contract exposed to Server Logic scripts at <c>Server.Connector.CloudFlow</c>.
/// </summary>
public interface IServerLogicCloudFlowProvider
{
    Task<string> TriggerAsync(string flowUrlOrId, string payload, CancellationToken cancellationToken = default);
}
