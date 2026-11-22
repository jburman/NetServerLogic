using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetServerLogic.Abstractions;
using NetServerLogic.Connectors.OData;
using NetServerLogic.Options;

namespace NetServerLogic.Connectors;

/// <summary>
/// Thread-safe JSON-seeded in-memory implementation of <see cref="IServerLogicDataverseProvider"/>.
/// </summary>
public sealed class MockDataverseProvider : IServerLogicDataverseProvider
{
    private readonly ServerLogicEmulatorOptions _options;
    private readonly SimpleODataQueryEvaluator _queryEvaluator;
    private readonly ILogger<MockDataverseProvider>? _logger;
    private readonly string _mockDataRoot;
    private readonly ConcurrentDictionary<string, EntitySetStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    public MockDataverseProvider(
        IOptions<ServerLogicEmulatorOptions> options,
        SimpleODataQueryEvaluator? queryEvaluator = null,
        IHostEnvironment? hostEnvironment = null,
        ILogger<MockDataverseProvider>? logger = null)
    {
        _options = options.Value;
        _queryEvaluator = queryEvaluator ?? new SimpleODataQueryEvaluator();
        _logger = logger;

        var contentRoot = string.IsNullOrWhiteSpace(hostEnvironment?.ContentRootPath)
            ? Directory.GetCurrentDirectory()
            : hostEnvironment.ContentRootPath;

        var configuredPath = string.IsNullOrWhiteSpace(_options.MockDataPath)
            ? ".serverlogic-mocks"
            : _options.MockDataPath;

        _mockDataRoot = Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(contentRoot, configuredPath));
    }

    public string RetrieveRecord(string entitySetName, string id, string? options = null, bool skipCache = false)
    {
        ValidateEntitySetName(entitySetName);
        var normalizedId = NormalizeGuid(id);
        var store = GetOrLoadStore(entitySetName);

        lock (store.SyncRoot)
        {
            var pk = store.PrimaryKeyProperty;
            var match = FindById(store.Records, pk, normalizedId);
            if (match is null)
            {
                throw new KeyNotFoundException(
                    $"Dataverse record '{id}' was not found in entity set '{entitySetName}' (HTTP 404).");
            }

            var projected = _queryEvaluator.ApplySingle(match, options, pk);
            return projected.ToJsonString();
        }
    }

    public string RetrieveMultipleRecords(string entitySetName, string? options = null, bool skipCache = false)
    {
        ValidateEntitySetName(entitySetName);
        var store = GetOrLoadStore(entitySetName);

        IReadOnlyList<JsonObject> projected;
        lock (store.SyncRoot)
        {
            projected = _queryEvaluator.Apply(store.Records, options, store.PrimaryKeyProperty);
        }

        var valueArray = new JsonArray();
        foreach (var item in projected)
        {
            valueArray.Add(item);
        }

        var envelope = new JsonObject
        {
            ["@odata.context"] = $"https://mock.dataverse.local/api/data/v9.2/$metadata#{entitySetName}",
            ["value"] = valueArray
        };

        return envelope.ToJsonString();
    }

    public string CreateRecord(string entitySetName, string jsonPayload)
    {
        ValidateEntitySetName(entitySetName);
        var payloadObj = ParseJsonObject(jsonPayload, nameof(jsonPayload));
        var store = GetOrLoadStore(entitySetName);

        JsonObject cloned = (JsonObject)payloadObj.DeepClone();
        string assignedId;

        lock (store.SyncRoot)
        {
            var pk = store.PrimaryKeyProperty;
            if (TryGetRecordId(cloned, pk, out var existingId) && !string.IsNullOrWhiteSpace(existingId))
            {
                assignedId = NormalizeGuid(existingId);
                SetPropertyCaseInsensitive(cloned, pk, assignedId);
            }
            else
            {
                assignedId = Guid.NewGuid().ToString("D");
                cloned[pk] = assignedId;
            }

            store.Records.Add(cloned);
        }

        return _options.Dataverse.CreateRecordReturnMode switch
        {
            CreateRecordReturnMode.FullRepresentation => cloned.ToJsonString(),
            CreateRecordReturnMode.Empty => string.Empty,
            _ => new JsonObject { ["id"] = assignedId }.ToJsonString()
        };
    }

    public string UpdateRecord(string entitySetName, string id, string jsonPayload)
    {
        ValidateEntitySetName(entitySetName);
        var normalizedId = NormalizeGuid(id);
        var patchObj = ParseJsonObject(jsonPayload, nameof(jsonPayload));
        var store = GetOrLoadStore(entitySetName);

        lock (store.SyncRoot)
        {
            var existing = FindById(store.Records, store.PrimaryKeyProperty, normalizedId);
            if (existing is null)
            {
                throw new KeyNotFoundException(
                    $"Dataverse record '{id}' was not found in entity set '{entitySetName}' (HTTP 404).");
            }

            foreach (var kvp in patchObj)
            {
                SetPropertyCaseInsensitive(existing, kvp.Key, kvp.Value?.DeepClone());
            }
        }

        return string.Empty;
    }

    public string DeleteRecord(string entitySetName, string id)
    {
        ValidateEntitySetName(entitySetName);
        var normalizedId = NormalizeGuid(id);
        var store = GetOrLoadStore(entitySetName);

        lock (store.SyncRoot)
        {
            var existing = FindById(store.Records, store.PrimaryKeyProperty, normalizedId);
            if (existing is null)
            {
                throw new KeyNotFoundException(
                    $"Dataverse record '{id}' was not found in entity set '{entitySetName}' (HTTP 404).");
            }

            store.Records.Remove(existing);
        }

        return string.Empty;
    }

    public string InvokeCustomApi(string httpMethod, string url, string? payload = null)
    {
        var method = string.IsNullOrWhiteSpace(httpMethod) ? "POST" : httpMethod.Trim().ToUpperInvariant();
        var trimmedUrl = (url ?? string.Empty).Trim();
        var normalizedUrl = trimmedUrl.TrimStart('/');

        var candidateKeys = new[]
        {
            $"{method} {trimmedUrl}",
            $"{method} {normalizedUrl}",
            $"{method} /{normalizedUrl}"
        };

        foreach (var key in candidateKeys)
        {
            if (_options.CustomApiMocks.TryGetValue(key, out var handler))
            {
                return handler(payload, CancellationToken.None);
            }
        }

        var sanitizedName = SanitizeCustomApiFileName(normalizedUrl);
        var fixturePath = Path.Combine(_mockDataRoot, "custom-apis", $"{sanitizedName}.json");
        if (File.Exists(fixturePath))
        {
            return File.ReadAllText(fixturePath);
        }

        throw new KeyNotFoundException(
            $"No mock handler or fixture was registered for Dataverse custom API '{method} {trimmedUrl}'.");
    }

    private EntitySetStore GetOrLoadStore(string entitySetName)
    {
        return _stores.GetOrAdd(entitySetName, name => LoadStoreFromDisk(name));
    }

    private EntitySetStore LoadStoreFromDisk(string entitySetName)
    {
        var records = new List<JsonObject>();
        var seedFile = Path.Combine(_mockDataRoot, $"{entitySetName}.json");

        if (File.Exists(seedFile))
        {
            var json = File.ReadAllText(seedFile);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var node = JsonNode.Parse(json);
                if (node is JsonArray array)
                {
                    foreach (var item in array)
                    {
                        if (item is JsonObject obj)
                        {
                            records.Add(obj);
                        }
                    }
                }
                else
                {
                    _logger?.LogWarning("Seed file '{SeedFile}' did not contain a JSON array.", seedFile);
                }
            }
        }

        var primaryKey = ResolvePrimaryKeyProperty(entitySetName, records);
        return new EntitySetStore(primaryKey, records);
    }

    private string ResolvePrimaryKeyProperty(string entitySetName, List<JsonObject> records)
    {
        if (_options.Dataverse.PrimaryKeyOverrides.TryGetValue(entitySetName, out var overrideKey) &&
            !string.IsNullOrWhiteSpace(overrideKey))
        {
            return overrideKey;
        }

        var singular = SingularizeEntitySet(entitySetName);
        var conventionKey = $"{singular}id";

        if (records.Count > 0)
        {
            var sample = records[0];

            foreach (var kvp in sample)
            {
                if (kvp.Key.Equals(conventionKey, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Key;
                }
            }

            foreach (var kvp in sample)
            {
                if (kvp.Key.Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Key;
                }
            }

            foreach (var kvp in sample)
            {
                if (kvp.Key.EndsWith("id", StringComparison.OrdinalIgnoreCase) &&
                    kvp.Value is JsonValue val &&
                    val.TryGetValue<string>(out var text) &&
                    Guid.TryParse(text, out _))
                {
                    return kvp.Key;
                }
            }
        }

        return conventionKey;
    }

    private static string SingularizeEntitySet(string entitySetName)
    {
        if (entitySetName.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && entitySetName.Length > 3)
        {
            return string.Concat(entitySetName.AsSpan(0, entitySetName.Length - 3), "y");
        }

        if (entitySetName.EndsWith("ses", StringComparison.OrdinalIgnoreCase) && entitySetName.Length > 3)
        {
            return entitySetName[..^2];
        }

        if (entitySetName.EndsWith('s') && entitySetName.Length > 1)
        {
            return entitySetName[..^1];
        }

        return entitySetName;
    }

    private static JsonObject? FindById(List<JsonObject> records, string pkProperty, string normalizedId)
    {
        foreach (var record in records)
        {
            if (TryGetRecordId(record, pkProperty, out var candidate) &&
                string.Equals(NormalizeGuid(candidate), normalizedId, StringComparison.OrdinalIgnoreCase))
            {
                return record;
            }
        }

        return null;
    }

    private static bool TryGetRecordId(JsonObject record, string pkProperty, out string id)
    {
        foreach (var kvp in record)
        {
            if (kvp.Key.Equals(pkProperty, StringComparison.OrdinalIgnoreCase) &&
                kvp.Value is JsonValue jv &&
                jv.TryGetValue<string>(out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                id = value;
                return true;
            }
        }

        if (!pkProperty.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kvp in record)
            {
                if (kvp.Key.Equals("id", StringComparison.OrdinalIgnoreCase) &&
                    kvp.Value is JsonValue jv &&
                    jv.TryGetValue<string>(out var value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    id = value;
                    return true;
                }
            }
        }

        id = string.Empty;
        return false;
    }

    private static void SetPropertyCaseInsensitive(JsonObject target, string key, JsonNode? value)
    {
        string? existingKey = null;
        foreach (var kvp in target)
        {
            if (kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                existingKey = kvp.Key;
                break;
            }
        }

        target[existingKey ?? key] = value;
    }

    private static void SetPropertyCaseInsensitive(JsonObject target, string key, string value)
    {
        SetPropertyCaseInsensitive(target, key, JsonValue.Create(value));
    }

    private static JsonObject ParseJsonObject(string jsonPayload, string paramName)
    {
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            throw new ArgumentException("JSON payload must not be empty.", paramName);
        }

        var node = JsonNode.Parse(jsonPayload);
        if (node is not JsonObject obj)
        {
            throw new ArgumentException("Payload must be a valid JSON object.", paramName);
        }

        return obj;
    }

    private static string NormalizeGuid(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Record ID must not be empty.", nameof(id));
        }

        return id.Trim().Trim('{', '}');
    }

    private static void ValidateEntitySetName(string entitySetName)
    {
        if (string.IsNullOrWhiteSpace(entitySetName) ||
            entitySetName.Contains("..", StringComparison.Ordinal) ||
            entitySetName.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
        {
            throw new ArgumentException($"Invalid Dataverse entity set name '{entitySetName}'.", nameof(entitySetName));
        }
    }

    private static string SanitizeCustomApiFileName(string url)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var buffer = new char[url.Length];
        for (var i = 0; i < url.Length; i++)
        {
            var ch = url[i];
            buffer[i] = ch is '/' or '\\' or '?' or '&' or '=' || invalidChars.Contains(ch)
                ? '_'
                : ch;
        }

        return new string(buffer).Trim('_');
    }

    private sealed class EntitySetStore
    {
        public object SyncRoot { get; } = new();
        public string PrimaryKeyProperty { get; }
        public List<JsonObject> Records { get; }

        public EntitySetStore(string primaryKeyProperty, List<JsonObject> records)
        {
            PrimaryKeyProperty = primaryKeyProperty;
            Records = records;
        }
    }
}
