using NetServerLogic.Abstractions;
using YamlDotNet.RepresentationModel;

namespace NetServerLogic.Metadata;

/// <summary>
/// Parses Power Pages <c>.serverlogic.yml</c> files and companion <c>web-roles</c> YAML metadata
/// to determine allowed Web Role names for a script.
/// </summary>
public sealed class ServerLogicYmlParser : IServerLogicRoleProvider
{
    private static readonly string[] RoleListKeys =
    [
        "adx_serverlogic_webrole",
        "webroles",
        "web_roles",
        "roles"
    ];

    public async Task<IReadOnlyCollection<string>?> GetAllowedRolesAsync(
        string scriptName,
        string? ymlFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ymlFilePath) || !File.Exists(ymlFilePath))
        {
            return null;
        }

        var yamlContent = await File.ReadAllTextAsync(ymlFilePath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(yamlContent))
        {
            return Array.Empty<string>();
        }

        var yaml = new YamlStream();
        using var reader = new StringReader(yamlContent);
        yaml.Load(reader);

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            return Array.Empty<string>();
        }

        var rawRoles = ExtractRawRoles(root);
        if (rawRoles.Count == 0)
        {
            return Array.Empty<string>();
        }

        Dictionary<Guid, string>? webRoleIdMap = null;
        var resolved = new List<string>(rawRoles.Count);

        foreach (var entry in rawRoles)
        {
            if (Guid.TryParse(entry, out var roleId))
            {
                webRoleIdMap ??= LoadSiblingWebRoleIdMap(ymlFilePath);
                resolved.Add(webRoleIdMap.TryGetValue(roleId, out var mappedName)
                    ? mappedName
                    : entry);
            }
            else
            {
                resolved.Add(entry);
            }
        }

        return resolved
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static List<string> ExtractRawRoles(YamlMappingNode root)
    {
        var results = new List<string>();

        foreach (var key in RoleListKeys)
        {
            if (!TryGetMappingValueIgnoreCase(root, key, out var node))
            {
                continue;
            }

            switch (node)
            {
                case YamlSequenceNode sequence:
                    foreach (var item in sequence.Children)
                    {
                        AddRoleItem(item, results);
                    }
                    break;
                case YamlScalarNode scalar when !string.IsNullOrWhiteSpace(scalar.Value):
                    results.Add(scalar.Value.Trim());
                    break;
            }
        }

        return results;
    }

    private static void AddRoleItem(YamlNode item, List<string> results)
    {
        switch (item)
        {
            case YamlScalarNode scalar when !string.IsNullOrWhiteSpace(scalar.Value):
                results.Add(scalar.Value.Trim());
                break;

            case YamlMappingNode mapping:
                if (TryGetScalarIgnoreCase(mapping, "adx_name", out var adxName))
                {
                    results.Add(adxName);
                }
                else if (TryGetScalarIgnoreCase(mapping, "name", out var name))
                {
                    results.Add(name);
                }
                else if (TryGetScalarIgnoreCase(mapping, "adx_webroleid", out var roleId))
                {
                    results.Add(roleId);
                }
                break;
        }
    }

    private static Dictionary<Guid, string> LoadSiblingWebRoleIdMap(string ymlFilePath)
    {
        var map = new Dictionary<Guid, string>();
        var scriptDir = Path.GetDirectoryName(ymlFilePath);
        if (string.IsNullOrEmpty(scriptDir))
        {
            return map;
        }

        // Typical layout: .powerpages-site/server-logic/{name}/{name}.serverlogic.yml
        // Sibling web roles live under: .powerpages-site/web-roles/*.webrole.yml
        var serverLogicDir = Directory.GetParent(scriptDir);
        var siteDir = serverLogicDir?.Parent;
        if (siteDir is null)
        {
            return map;
        }

        var webRolesDir = Path.Combine(siteDir.FullName, "web-roles");
        if (!Directory.Exists(webRolesDir))
        {
            return map;
        }

        foreach (var file in Directory.EnumerateFiles(webRolesDir, "*.webrole.yml", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                var stream = new YamlStream();
                using var reader = new StringReader(content);
                stream.Load(reader);
                if (stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode mapping)
                {
                    if (TryGetScalarIgnoreCase(mapping, "adx_webroleid", out var idStr) &&
                        Guid.TryParse(idStr, out var id) &&
                        TryGetScalarIgnoreCase(mapping, "adx_name", out var roleName))
                    {
                        map[id] = roleName;
                    }
                }
            }
            catch
            {
                // Ignore malformed unrelated YAML files in web-roles directory.
            }
        }

        return map;
    }

    private static bool TryGetMappingValueIgnoreCase(YamlMappingNode mapping, string targetKey, out YamlNode? value)
    {
        foreach (var entry in mapping.Children)
        {
            if (entry.Key is YamlScalarNode scalarKey &&
                string.Equals(scalarKey.Value, targetKey, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryGetScalarIgnoreCase(YamlMappingNode mapping, string targetKey, out string value)
    {
        if (TryGetMappingValueIgnoreCase(mapping, targetKey, out var node) &&
            node is YamlScalarNode scalar &&
            !string.IsNullOrWhiteSpace(scalar.Value))
        {
            value = scalar.Value.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }
}
