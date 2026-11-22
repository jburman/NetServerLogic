namespace NetServerLogic.Validation;

/// <summary>
/// Severity level for a static validation diagnostic.
/// </summary>
public enum ScriptValidationSeverity
{
    Warning,
    Error
}

/// <summary>
/// Represents a single pre-flight validation finding with source location.
/// </summary>
public sealed record ScriptValidationDiagnostic(
    string RuleId,
    ScriptValidationSeverity Severity,
    string Message,
    int Line,
    int Column);

/// <summary>
/// Aggregated pre-flight validation result for a Server Logic script.
/// </summary>
public sealed class ScriptValidationResult
{
    public IReadOnlyList<ScriptValidationDiagnostic> Diagnostics { get; }

    public IEnumerable<ScriptValidationDiagnostic> Errors =>
        Diagnostics.Where(static d => d.Severity == ScriptValidationSeverity.Error);

    public IEnumerable<ScriptValidationDiagnostic> Warnings =>
        Diagnostics.Where(static d => d.Severity == ScriptValidationSeverity.Warning);

    public bool HasErrors => Diagnostics.Any(static d => d.Severity == ScriptValidationSeverity.Error);

    public ScriptValidationResult(IReadOnlyList<ScriptValidationDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics ?? Array.Empty<ScriptValidationDiagnostic>();
    }

    /// <summary>
    /// Returns <c>true</c> when execution may proceed given <paramref name="strictValidatorMode"/>.
    /// </summary>
    public bool IsValid(bool strictValidatorMode)
    {
        if (HasErrors)
        {
            return false;
        }

        return !strictValidatorMode || Diagnostics.Count == 0;
    }
}
