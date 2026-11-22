using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetServerLogic.Abstractions;

namespace NetServerLogic.Connectors;

/// <summary>
/// Local stub implementation of <c>Server.Connector.CloudFlow.TriggerAsync</c>.
/// </summary>
public sealed class MockCloudFlowProvider : IServerLogicCloudFlowProvider
{
    private readonly ILogger<MockCloudFlowProvider> _logger;

    public MockCloudFlowProvider(ILogger<MockCloudFlowProvider> logger)
    {
        _logger = logger;
    }

    public Task<string> TriggerAsync(string flowUrlOrId, string payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(flowUrlOrId))
        {
            throw new ArgumentException("CloudFlow URL or ID must not be empty.", nameof(flowUrlOrId));
        }

        _logger.LogInformation(
            "MockCloudFlowProvider triggered flow '{FlowUrlOrId}' with payload length {Length}.",
            flowUrlOrId,
            payload?.Length ?? 0);

        var response = JsonSerializer.Serialize(new
        {
            status = "Succeeded",
            flow = flowUrlOrId,
            mocked = true,
            receivedPayload = payload
        });

        return Task.FromResult(response);
    }
}
