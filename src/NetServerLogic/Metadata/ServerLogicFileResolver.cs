using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetServerLogic.Options;

namespace NetServerLogic.Metadata;

public enum ServerLogicResolutionStatus
{
    Found,
    NotFound,
    InvalidName
}

public sealed record ServerLogicResolutionResult(
    ServerLogicResolutionStatus Status,
    string ScriptName,
    string? ScriptPath,
    string? YmlPath,
    string? ErrorMessage = null);

/// <summary>
/// Resolves Power Pages Server Logic <c>.js</c> and <c>.serverlogic.yml</c> files safely on disk.
/// </summary>
public sealed partial class ServerLogicFileResolver
{
    private readonly ServerLogicEmulatorOptions _options;
    private readonly string _contentRootPath;

    public ServerLogicFileResolver(
        IOptions<ServerLogicEmulatorOptions> options,
        IHostEnvironment? hostEnvironment = null)
    {
        _options = options.Value;
        _contentRootPath = string.IsNullOrWhiteSpace(hostEnvironment?.ContentRootPath)
            ? Directory.GetCurrentDirectory()
            : hostEnvironment.ContentRootPath;
    }

    /// <summary>
    /// Gets the resolved full path of the configured <see cref="ServerLogicEmulatorOptions.ServerLogicRootPath"/>.
    /// </summary>
    public string GetResolvedRootPath()
    {
        var configured = string.IsNullOrWhiteSpace(_options.ServerLogicRootPath)
            ? ".powerpages-site/server-logic"
            : _options.ServerLogicRootPath;

        var combined = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(_contentRootPath, configured);

        return Path.GetFullPath(combined);
    }

    /// <summary>
    /// Resolves <c>{root}/{name}/{name}.js</c> and optional <c>{root}/{name}/{name}.serverlogic.yml</c>,
    /// rejecting path traversal or malformed script names.
    /// </summary>
    public ServerLogicResolutionResult Resolve(string? scriptName)
    {
        if (!IsValidScriptName(scriptName))
        {
            return new ServerLogicResolutionResult(
                ServerLogicResolutionStatus.InvalidName,
                scriptName ?? string.Empty,
                null,
                null,
                $"Invalid Server Logic script name '{scriptName}'.");
        }

        var safeName = scriptName!;
        var rootFullPath = EnsureTrailingSeparator(GetResolvedRootPath());
        var scriptDirFullPath = Path.GetFullPath(Path.Combine(rootFullPath, safeName));
        var scriptPath = Path.GetFullPath(Path.Combine(scriptDirFullPath, $"{safeName}.js"));
        var ymlPath = Path.GetFullPath(Path.Combine(scriptDirFullPath, $"{safeName}.serverlogic.yml"));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!scriptPath.StartsWith(rootFullPath, comparison) ||
            !ymlPath.StartsWith(rootFullPath, comparison))
        {
            return new ServerLogicResolutionResult(
                ServerLogicResolutionStatus.InvalidName,
                safeName,
                null,
                null,
                $"Invalid Server Logic script path for '{safeName}'.");
        }

        if (!File.Exists(scriptPath))
        {
            return new ServerLogicResolutionResult(
                ServerLogicResolutionStatus.NotFound,
                safeName,
                scriptPath,
                File.Exists(ymlPath) ? ymlPath : null,
                $"Server Logic script '{safeName}' was not found.");
        }

        return new ServerLogicResolutionResult(
            ServerLogicResolutionStatus.Found,
            safeName,
            scriptPath,
            File.Exists(ymlPath) ? ymlPath : null);
    }

    /// <summary>
    /// Reads the script file fresh from disk without caching.
    /// </summary>
    public Task<string> ReadScriptAsync(string scriptPath, CancellationToken cancellationToken = default)
        => File.ReadAllTextAsync(scriptPath, cancellationToken);

    private static bool IsValidScriptName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name.Contains("..", StringComparison.Ordinal) ||
            name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        return SafeScriptNameRegex().IsMatch(name);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed + Path.DirectorySeparatorChar;
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeScriptNameRegex();
}
