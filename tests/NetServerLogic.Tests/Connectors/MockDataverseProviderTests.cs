using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NetServerLogic.Connectors;
using NetServerLogic.Connectors.OData;
using NetServerLogic.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace NetServerLogic.Tests.Connectors;

public sealed class MockDataverseProviderTests : IDisposable
{
    private readonly string _tempRoot;

    public MockDataverseProviderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"NetServerLogic_MockDv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private MockDataverseProvider CreateProvider(ServerLogicEmulatorOptions? options = null)
    {
        var opts = options ?? new ServerLogicEmulatorOptions { MockDataPath = _tempRoot };
        if (string.IsNullOrWhiteSpace(opts.MockDataPath))
        {
            opts.MockDataPath = _tempRoot;
        }

        return new MockDataverseProvider(
            OptionsFactory.Create(opts),
            new SimpleODataQueryEvaluator(NullLogger<SimpleODataQueryEvaluator>.Instance),
            hostEnvironment: null,
            NullLogger<MockDataverseProvider>.Instance);
    }

    [Fact]
    public void SeedLoadingAndCrudLifecycle_WorksEndToEnd()
    {
        File.WriteAllText(
            Path.Combine(_tempRoot, "accounts.json"),
            """
            [
              {
                "accountid": "11111111-1111-1111-1111-111111111111",
                "name": "Contoso Ltd",
                "revenue": 1500000,
                "statecode": 0
              }
            ]
            """);

        var provider = CreateProvider();

        // Retrieve seeded record with curly braces normalized
        var seededRaw = provider.RetrieveRecord("accounts", "{11111111-1111-1111-1111-111111111111}", "$select=name");
        using (var seededDoc = JsonDocument.Parse(seededRaw))
        {
            Assert.Equal("Contoso Ltd", seededDoc.RootElement.GetProperty("name").GetString());
        }

        // Create
        var createdRaw = provider.CreateRecord("accounts", """{"name":"Fabrikam","revenue":500000,"statecode":0}""");
        string createdId;
        using (var createdDoc = JsonDocument.Parse(createdRaw))
        {
            createdId = createdDoc.RootElement.GetProperty("id").GetString()!;
            Assert.True(Guid.TryParse(createdId, out _));
        }

        // Update
        var updateResult = provider.UpdateRecord("accounts", createdId, """{"revenue":900000}""");
        Assert.Equal(string.Empty, updateResult);

        var updatedRaw = provider.RetrieveRecord("accounts", createdId);
        using (var updatedDoc = JsonDocument.Parse(updatedRaw))
        {
            Assert.Equal("Fabrikam", updatedDoc.RootElement.GetProperty("name").GetString());
            Assert.Equal(900000, updatedDoc.RootElement.GetProperty("revenue").GetInt32());
        }

        // Delete
        var deleteResult = provider.DeleteRecord("accounts", createdId);
        Assert.Equal(string.Empty, deleteResult);

        Assert.Throws<KeyNotFoundException>(() => provider.RetrieveRecord("accounts", createdId));
    }

    [Fact]
    public void InvokeCustomApi_SupportsRegisteredDelegatesAndFixtureFiles()
    {
        var customApisDir = Path.Combine(_tempRoot, "custom-apis");
        Directory.CreateDirectory(customApisDir);
        File.WriteAllText(Path.Combine(customApisDir, "cr123_FixtureAction.json"), """{"fromFixture":true}""");

        var options = new ServerLogicEmulatorOptions
        {
            MockDataPath = _tempRoot,
            CustomApiMocks =
            {
                ["POST cr123_CalculateDiscount"] = (payload, _) => $$"""{"discount":15,"payload":{{payload ?? "null"}}}"""
            }
        };

        var provider = CreateProvider(options);

        var delegateResp = provider.InvokeCustomApi("POST", "cr123_CalculateDiscount", """{"amount":100}""");
        using (var doc = JsonDocument.Parse(delegateResp))
        {
            Assert.Equal(15, doc.RootElement.GetProperty("discount").GetInt32());
            Assert.Equal(100, doc.RootElement.GetProperty("payload").GetProperty("amount").GetInt32());
        }

        var fixtureResp = provider.InvokeCustomApi("GET", "/cr123_FixtureAction");
        using (var fixtureDoc = JsonDocument.Parse(fixtureResp))
        {
            Assert.True(fixtureDoc.RootElement.GetProperty("fromFixture").GetBoolean());
        }
    }

    [Fact]
    public void CreateRecord_FullRepresentationMode_ReturnsEntireEntity()
    {
        var options = new ServerLogicEmulatorOptions
        {
            MockDataPath = _tempRoot,
            Dataverse = new DataverseOptions { CreateRecordReturnMode = CreateRecordReturnMode.FullRepresentation }
        };
        var provider = CreateProvider(options);

        var raw = provider.CreateRecord("contacts", """{"fullname":"Ada Lovelace"}""");
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("Ada Lovelace", doc.RootElement.GetProperty("fullname").GetString());
        Assert.True(doc.RootElement.TryGetProperty("contactid", out _));
    }
}
