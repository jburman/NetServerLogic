using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetServerLogic.Connectors.OData;

/// <summary>
/// Evaluates basic OData v4 query options (<c>$select</c>, <c>$filter</c>, <c>$orderby</c>, <c>$top</c>)
/// against in-memory <see cref="JsonObject"/> records for <see cref="MockDataverseProvider"/>.
/// </summary>
public sealed class SimpleODataQueryEvaluator
{
    private readonly ILogger<SimpleODataQueryEvaluator>? _logger;

    public SimpleODataQueryEvaluator(ILogger<SimpleODataQueryEvaluator>? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<JsonObject> Apply(
        IEnumerable<JsonObject> source,
        string? options,
        string? primaryKeyProperty = null)
    {
        var query = ParseQueryOptions(options);
        IEnumerable<JsonObject> current = source;

        if (query.TryGetValue("$filter", out var filterExpr) && !string.IsNullOrWhiteSpace(filterExpr))
        {
            var predicate = ODataFilterParser.Parse(filterExpr);
            current = current.Where(predicate);
        }

        if (query.TryGetValue("$orderby", out var orderByExpr) && !string.IsNullOrWhiteSpace(orderByExpr))
        {
            current = ApplyOrderBy(current, orderByExpr);
        }

        if (query.TryGetValue("$top", out var topExpr) && !string.IsNullOrWhiteSpace(topExpr))
        {
            if (int.TryParse(topExpr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) && top >= 0)
            {
                current = current.Take(top);
            }
            else
            {
                throw new ArgumentException($"Invalid OData $top value: '{topExpr}'.");
            }
        }

        string[]? selectFields = null;
        if (query.TryGetValue("$select", out var selectExpr) && !string.IsNullOrWhiteSpace(selectExpr))
        {
            selectFields = selectExpr
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var result = new List<JsonObject>();
        foreach (var record in current)
        {
            result.Add(ProjectRecord(record, selectFields, primaryKeyProperty));
        }

        return result;
    }

    public JsonObject ApplySingle(
        JsonObject record,
        string? options,
        string? primaryKeyProperty = null)
    {
        var query = ParseQueryOptions(options);
        string[]? selectFields = null;
        if (query.TryGetValue("$select", out var selectExpr) && !string.IsNullOrWhiteSpace(selectExpr))
        {
            selectFields = selectExpr
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return ProjectRecord(record, selectFields, primaryKeyProperty);
    }

    public Dictionary<string, string> ParseQueryOptions(string? options)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(options))
        {
            return parsed;
        }

        var trimmed = options.Trim();
        if (trimmed.StartsWith('?'))
        {
            trimmed = trimmed[1..];
        }

        foreach (var segment in SplitOutsideQuotes(trimmed, '&'))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            var eqIndex = segment.IndexOf('=');
            var rawKey = eqIndex >= 0 ? segment[..eqIndex] : segment;
            var rawValue = eqIndex >= 0 ? segment[(eqIndex + 1)..] : string.Empty;

            var key = UnescapeIfNeeded(rawKey).Trim();
            var value = UnescapeIfNeeded(rawValue).Trim();

            if (!IsSupportedQueryOption(key))
            {
                _logger?.LogWarning(
                    "MockDataverseProvider encountered unsupported OData query option '{Option}' in '{Options}'.",
                    key,
                    options);
                continue;
            }

            parsed[key] = value;
        }

        return parsed;
    }

    private static bool IsSupportedQueryOption(string key) =>
        key.Equals("$select", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("$filter", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("$orderby", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("$top", StringComparison.OrdinalIgnoreCase);

    private static string UnescapeIfNeeded(string input) =>
        input.Contains('%', StringComparison.Ordinal)
            ? Uri.UnescapeDataString(input)
            : input;

    private static IEnumerable<string> SplitOutsideQuotes(string text, char separator)
    {
        var start = 0;
        var inSingleQuote = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\'')
            {
                if (inSingleQuote && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                inSingleQuote = !inSingleQuote;
            }
            else if (ch == separator && !inSingleQuote)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }

    private static JsonObject ProjectRecord(
        JsonObject record,
        string[]? selectFields,
        string? primaryKeyProperty)
    {
        if (selectFields is null || selectFields.Length == 0)
        {
            return (JsonObject)record.DeepClone();
        }

        var projected = new JsonObject();
        var allowed = new HashSet<string>(selectFields, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(primaryKeyProperty))
        {
            allowed.Add(primaryKeyProperty);
        }

        foreach (var kvp in record)
        {
            if (allowed.Contains(kvp.Key))
            {
                projected[kvp.Key] = kvp.Value?.DeepClone();
            }
        }

        return projected;
    }

    private static IEnumerable<JsonObject> ApplyOrderBy(IEnumerable<JsonObject> source, string orderByExpr)
    {
        var clauses = orderByExpr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IOrderedEnumerable<JsonObject>? ordered = null;

        foreach (var clause in clauses)
        {
            var parts = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var field = parts[0];
            var descending = parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);

            if (ordered is null)
            {
                ordered = descending
                    ? source.OrderByDescending(r => ExtractFieldValue(r, field), ODataValueComparer.Instance)
                    : source.OrderBy(r => ExtractFieldValue(r, field), ODataValueComparer.Instance);
            }
            else
            {
                ordered = descending
                    ? ordered.ThenByDescending(r => ExtractFieldValue(r, field), ODataValueComparer.Instance)
                    : ordered.ThenBy(r => ExtractFieldValue(r, field), ODataValueComparer.Instance);
            }
        }

        return ordered ?? source;
    }

    internal static object? ExtractFieldValue(JsonObject record, string fieldName)
    {
        foreach (var kvp in record)
        {
            if (kvp.Key.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return UnwrapJsonNode(kvp.Value);
            }
        }

        return null;
    }

    internal static object? UnwrapJsonNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue jv)
        {
            switch (jv.GetValueKind())
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    if (jv.TryGetValue<decimal>(out var dec)) return dec;
                    if (jv.TryGetValue<int>(out var i)) return (decimal)i;
                    if (jv.TryGetValue<long>(out var l)) return (decimal)l;
                    if (jv.TryGetValue<double>(out var dbl)) return (decimal)dbl;
                    if (jv.TryGetValue<float>(out var flt)) return (decimal)flt;
                    if (decimal.TryParse(jv.ToJsonString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                    {
                        return parsed;
                    }
                    break;
                case JsonValueKind.String:
                    if (jv.TryGetValue<string>(out var str)) return str;
                    break;
            }

            return jv.ToString();
        }

        return node.ToJsonString();
    }

    private sealed class ODataValueComparer : IComparer<object?>
    {
        public static readonly ODataValueComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            if (TryAsDecimal(x, out var dx) && TryAsDecimal(y, out var dy))
            {
                return dx.CompareTo(dy);
            }

            if (x is bool bx && y is bool by)
            {
                return bx.CompareTo(by);
            }

            if (x is string sx && y is string sy)
            {
                if (DateTimeOffset.TryParse(sx, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dtoX) &&
                    DateTimeOffset.TryParse(sy, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dtoY))
                {
                    return dtoX.CompareTo(dtoY);
                }

                return string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase);
            }

            return string.Compare(
                Convert.ToString(x, CultureInfo.InvariantCulture),
                Convert.ToString(y, CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryAsDecimal(object value, out decimal result)
        {
            switch (value)
            {
                case decimal dec:
                    result = dec;
                    return true;
                case int i:
                    result = i;
                    return true;
                case long l:
                    result = l;
                    return true;
                case double d:
                    result = (decimal)d;
                    return true;
                default:
                    result = 0m;
                    return false;
            }
        }
    }

    private static class ODataFilterParser
    {
        private enum TokenKind
        {
            Identifier,
            String,
            Number,
            Boolean,
            Null,
            LeftParen,
            RightParen,
            Comma,
            End
        }

        private readonly record struct Token(TokenKind Kind, string Text, object? Value);

        public static Func<JsonObject, bool> Parse(string expression)
        {
            var tokens = Tokenize(expression);
            var index = 0;
            var predicate = ParseOr(tokens, ref index);

            if (tokens[index].Kind != TokenKind.End)
            {
                throw new ArgumentException(
                    $"Unexpected token '{tokens[index].Text}' in OData $filter expression '{expression}'.");
            }

            return predicate;
        }

        private static Func<JsonObject, bool> ParseOr(List<Token> tokens, ref int index)
        {
            var left = ParseAnd(tokens, ref index);
            while (IsKeyword(tokens[index], "or"))
            {
                index++;
                var right = ParseAnd(tokens, ref index);
                var prev = left;
                left = record => prev(record) || right(record);
            }

            return left;
        }

        private static Func<JsonObject, bool> ParseAnd(List<Token> tokens, ref int index)
        {
            var left = ParsePrimary(tokens, ref index);
            while (IsKeyword(tokens[index], "and"))
            {
                index++;
                var right = ParsePrimary(tokens, ref index);
                var prev = left;
                left = record => prev(record) && right(record);
            }

            return left;
        }

        private static Func<JsonObject, bool> ParsePrimary(List<Token> tokens, ref int index)
        {
            if (tokens[index].Kind == TokenKind.LeftParen)
            {
                index++;
                var inner = ParseOr(tokens, ref index);
                if (tokens[index].Kind != TokenKind.RightParen)
                {
                    throw new ArgumentException("Missing closing ')' in OData $filter expression.");
                }

                index++;
                return inner;
            }

            if (tokens[index].Kind == TokenKind.Identifier &&
                index + 1 < tokens.Count &&
                tokens[index + 1].Kind == TokenKind.LeftParen)
            {
                return ParseStringFunction(tokens, ref index);
            }

            return ParseComparison(tokens, ref index);
        }

        private static Func<JsonObject, bool> ParseStringFunction(List<Token> tokens, ref int index)
        {
            var funcName = tokens[index].Text.ToLowerInvariant();
            index += 2; // identifier + '('

            if (tokens[index].Kind != TokenKind.Identifier)
            {
                throw new ArgumentException($"Expected field name in OData function '{funcName}(...)'.");
            }

            var fieldName = tokens[index].Text;
            index++;

            if (tokens[index].Kind != TokenKind.Comma)
            {
                throw new ArgumentException($"Expected ',' in OData function '{funcName}(...)'.");
            }

            index++;

            if (tokens[index].Kind != TokenKind.String)
            {
                throw new ArgumentException($"Expected string literal in OData function '{funcName}(...)'.");
            }

            var needle = (string)tokens[index].Value!;
            index++;

            if (tokens[index].Kind != TokenKind.RightParen)
            {
                throw new ArgumentException($"Expected ')' closing OData function '{funcName}(...)'.");
            }

            index++;

            return funcName switch
            {
                "contains" => record =>
                {
                    var val = ExtractFieldValue(record, fieldName)?.ToString();
                    return val is not null && val.Contains(needle, StringComparison.OrdinalIgnoreCase);
                },
                "startswith" => record =>
                {
                    var val = ExtractFieldValue(record, fieldName)?.ToString();
                    return val is not null && val.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
                },
                "endswith" => record =>
                {
                    var val = ExtractFieldValue(record, fieldName)?.ToString();
                    return val is not null && val.EndsWith(needle, StringComparison.OrdinalIgnoreCase);
                },
                _ => throw new NotSupportedException($"Unsupported OData $filter function '{funcName}'.")
            };
        }

        private static Func<JsonObject, bool> ParseComparison(List<Token> tokens, ref int index)
        {
            if (tokens[index].Kind != TokenKind.Identifier)
            {
                throw new ArgumentException($"Expected property identifier in OData $filter near '{tokens[index].Text}'.");
            }

            var fieldName = tokens[index].Text;
            index++;

            if (tokens[index].Kind != TokenKind.Identifier)
            {
                throw new ArgumentException($"Expected comparison operator after '{fieldName}' in OData $filter.");
            }

            var op = tokens[index].Text.ToLowerInvariant();
            index++;

            var literalToken = tokens[index];
            if (literalToken.Kind is not (TokenKind.String or TokenKind.Number or TokenKind.Boolean or TokenKind.Null or TokenKind.Identifier))
            {
                throw new ArgumentException($"Expected literal value after '{fieldName} {op}' in OData $filter.");
            }

            index++;
            var targetValue = literalToken.Kind == TokenKind.Identifier
                ? literalToken.Text
                : literalToken.Value;

            return record => EvaluateComparison(ExtractFieldValue(record, fieldName), op, targetValue);
        }

        private static bool EvaluateComparison(object? actual, string op, object? expected)
        {
            if (actual is null || expected is null)
            {
                return op switch
                {
                    "eq" => actual is null && expected is null,
                    "ne" => !(actual is null && expected is null),
                    _ => false
                };
            }

            if (ODataValueComparer.TryAsDecimal(actual, out var actualNum) &&
                TryConvertToDecimal(expected, out var expectedNum))
            {
                var cmp = actualNum.CompareTo(expectedNum);
                return CompareResult(cmp, op);
            }

            if (actual is bool actualBool && TryConvertToBool(expected, out var expectedBool))
            {
                return op switch
                {
                    "eq" => actualBool == expectedBool,
                    "ne" => actualBool != expectedBool,
                    _ => false
                };
            }

            var actualStr = Convert.ToString(actual, CultureInfo.InvariantCulture) ?? string.Empty;
            var expectedStr = Convert.ToString(expected, CultureInfo.InvariantCulture) ?? string.Empty;

            if (Guid.TryParse(actualStr, out var actualGuid) && Guid.TryParse(expectedStr, out var expectedGuid))
            {
                return op switch
                {
                    "eq" => actualGuid == expectedGuid,
                    "ne" => actualGuid != expectedGuid,
                    _ => CompareResult(actualGuid.CompareTo(expectedGuid), op)
                };
            }

            if (DateTimeOffset.TryParse(actualStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var actualDt) &&
                DateTimeOffset.TryParse(expectedStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expectedDt))
            {
                return CompareResult(actualDt.CompareTo(expectedDt), op);
            }

            var strCmp = string.Compare(actualStr, expectedStr, StringComparison.OrdinalIgnoreCase);
            return CompareResult(strCmp, op);
        }

        private static bool CompareResult(int cmp, string op) =>
            op switch
            {
                "eq" => cmp == 0,
                "ne" => cmp != 0,
                "gt" => cmp > 0,
                "ge" => cmp >= 0,
                "lt" => cmp < 0,
                "le" => cmp <= 0,
                _ => throw new NotSupportedException($"Unsupported OData comparison operator '{op}'.")
            };

        private static bool TryConvertToDecimal(object value, out decimal number)
        {
            if (ODataValueComparer.TryAsDecimal(value, out number))
            {
                return true;
            }

            return decimal.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out number);
        }

        private static bool TryConvertToBool(object value, out bool boolean)
        {
            if (value is bool b)
            {
                boolean = b;
                return true;
            }

            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out boolean);
        }

        private static bool IsKeyword(Token token, string keyword) =>
            token.Kind == TokenKind.Identifier &&
            token.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

        private static List<Token> Tokenize(string input)
        {
            var tokens = new List<Token>();
            var i = 0;

            while (i < input.Length)
            {
                var ch = input[i];
                if (char.IsWhiteSpace(ch))
                {
                    i++;
                    continue;
                }

                switch (ch)
                {
                    case '(':
                        tokens.Add(new Token(TokenKind.LeftParen, "(", null));
                        i++;
                        continue;
                    case ')':
                        tokens.Add(new Token(TokenKind.RightParen, ")", null));
                        i++;
                        continue;
                    case ',':
                        tokens.Add(new Token(TokenKind.Comma, ",", null));
                        i++;
                        continue;
                    case '\'':
                    {
                        i++;
                        var sb = new System.Text.StringBuilder();
                        var closed = false;
                        while (i < input.Length)
                        {
                            if (input[i] == '\'')
                            {
                                if (i + 1 < input.Length && input[i + 1] == '\'')
                                {
                                    sb.Append('\'');
                                    i += 2;
                                    continue;
                                }

                                i++;
                                closed = true;
                                break;
                            }

                            sb.Append(input[i]);
                            i++;
                        }

                        if (!closed)
                        {
                            throw new ArgumentException("Unterminated string literal in OData $filter.");
                        }

                        tokens.Add(new Token(TokenKind.String, sb.ToString(), sb.ToString()));
                        continue;
                    }
                }

                if (char.IsDigit(ch) || (ch == '-' && i + 1 < input.Length && char.IsDigit(input[i + 1])))
                {
                    var start = i;
                    i++;
                    while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.'))
                    {
                        i++;
                    }

                    // Handle unquoted GUID literals starting with digits (e.g. 11111111-1111-1111-1111-111111111111)
                    if (i < input.Length && input[i] == '-')
                    {
                        while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '-'))
                        {
                            i++;
                        }

                        var guidText = input[start..i];
                        tokens.Add(new Token(TokenKind.Identifier, guidText, guidText));
                        continue;
                    }

                    var numText = input[start..i];
                    if (!decimal.TryParse(numText, NumberStyles.Number, CultureInfo.InvariantCulture, out var num))
                    {
                        throw new ArgumentException($"Invalid numeric literal '{numText}' in OData $filter.");
                    }

                    tokens.Add(new Token(TokenKind.Number, numText, num));
                    continue;
                }

                if (char.IsLetter(ch) || ch == '_' || ch == '$')
                {
                    var start = i;
                    i++;
                    while (i < input.Length &&
                           (char.IsLetterOrDigit(input[i]) || input[i] == '_' || input[i] == '-' || input[i] == '/'))
                    {
                        i++;
                    }

                    var ident = input[start..i];
                    if (ident.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        tokens.Add(new Token(TokenKind.Boolean, ident, true));
                    }
                    else if (ident.Equals("false", StringComparison.OrdinalIgnoreCase))
                    {
                        tokens.Add(new Token(TokenKind.Boolean, ident, false));
                    }
                    else if (ident.Equals("null", StringComparison.OrdinalIgnoreCase))
                    {
                        tokens.Add(new Token(TokenKind.Null, ident, null));
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.Identifier, ident, ident));
                    }

                    continue;
                }

                throw new ArgumentException($"Unexpected character '{ch}' in OData $filter expression.");
            }

            tokens.Add(new Token(TokenKind.End, string.Empty, null));
            return tokens;
        }
    }
}
