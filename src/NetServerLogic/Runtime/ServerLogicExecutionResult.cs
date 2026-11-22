using NetServerLogic.Validation;

namespace NetServerLogic.Runtime;

/// <summary>
/// Represents the outcome of executing a Server Logic script inside <see cref="ServerLogicExecutionEngine"/>.
/// </summary>
public sealed class ServerLogicExecutionResult
{
    public int StatusCode { get; init; }

    public string? Output { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<ScriptValidationDiagnostic>? ValidationDiagnostics { get; init; }

    public IReadOnlyList<string>? AllowedMethods { get; init; }

    public bool IsSuccess => StatusCode is >= 200 and < 300;
}
