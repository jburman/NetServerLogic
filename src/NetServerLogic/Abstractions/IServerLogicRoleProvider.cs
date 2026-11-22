namespace NetServerLogic.Abstractions;

/// <summary>
/// Resolves allowed Power Pages Web Roles for a Server Logic script.
/// </summary>
public interface IServerLogicRoleProvider
{
    Task<IReadOnlyCollection<string>?> GetAllowedRolesAsync(
        string scriptName,
        string? ymlFilePath,
        CancellationToken cancellationToken = default);
}
