using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NetServerLogic.Connectors.OData;

namespace NetServerLogic.Tests.Connectors;

public sealed class SimpleODataQueryEvaluatorTests
{
    private static List<JsonObject> CreateSampleRecords() =>
    [
        new JsonObject
        {
            ["accountid"] = "11111111-1111-1111-1111-111111111111",
            ["name"] = "Contoso Ltd",
            ["revenue"] = 1500000,
            ["statecode"] = 0,
            ["createdon"] = "2025-02-01T10:00:00Z",
            ["active"] = true
        },
        new JsonObject
        {
            ["accountid"] = "22222222-2222-2222-2222-222222222222",
            ["name"] = "Fabrikam Inc",
            ["revenue"] = 750000,
            ["statecode"] = 0,
            ["createdon"] = "2025-01-15T10:00:00Z",
            ["active"] = true
        },
        new JsonObject
        {
            ["accountid"] = "33333333-3333-3333-3333-333333333333",
            ["name"] = "Northwind Traders",
            ["revenue"] = 3000000,
            ["statecode"] = 1,
            ["createdon"] = "2025-03-10T10:00:00Z",
            ["active"] = false
        }
    ];

    [Fact]
    public void Apply_SelectOrderByTop_ProjectsAndSortsProperly()
    {
        var evaluator = new SimpleODataQueryEvaluator();
        var records = CreateSampleRecords();

        var result = evaluator.Apply(
            records,
            "?$select=name,revenue&$orderby=revenue desc&$top=2",
            "accountid");

        Assert.Equal(2, result.Count);
        Assert.Equal("Northwind Traders", result[0]["name"]!.GetValue<string>());
        Assert.Equal("Contoso Ltd", result[1]["name"]!.GetValue<string>());
        Assert.Null(result[0]["statecode"]);
        Assert.NotNull(result[0]["accountid"]);
    }

    [Fact]
    public void Apply_CompoundFilterExpressions_EvaluateAccurately()
    {
        var evaluator = new SimpleODataQueryEvaluator();
        var records = CreateSampleRecords();

        var filtered = evaluator.Apply(
            records,
            "$filter=(statecode eq 0 and revenue ge 750000) or endswith(name,'Traders')");

        Assert.Equal(3, filtered.Count);

        var onlyActiveHigh = evaluator.Apply(
            records,
            "$filter=active eq true and (contains(name,'toso') or startswith(name,'Fab')) and revenue lt 2000000 and revenue gt 700000");

        Assert.Equal(2, onlyActiveHigh.Count);

        var excludeOne = evaluator.Apply(
            records,
            "$filter=name ne 'Fabrikam Inc' and revenue le 1500000");

        Assert.Single(excludeOne);
        Assert.Equal("Contoso Ltd", excludeOne[0]["name"]!.GetValue<string>());
    }

    [Fact]
    public void Apply_UnsupportedQueryOption_LogsWarning()
    {
        var logger = new RecordingLogger();
        var evaluator = new SimpleODataQueryEvaluator(logger);

        var result = evaluator.Apply(CreateSampleRecords(), "$select=name&$expand=primarycontactid");

        Assert.Equal(3, result.Count);
        Assert.Contains(logger.Warnings, w => w.Contains("$expand", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingLogger : ILogger<SimpleODataQueryEvaluator>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
