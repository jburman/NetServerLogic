using NetServerLogic.Validation;

namespace NetServerLogic.Abstractions;

/// <summary>
/// Performs static pre-flight validation of Server Logic JavaScript sources.
/// </summary>
public interface IServerLogicScriptValidator
{
    ScriptValidationResult Validate(string scriptSource, string scriptName);
}
