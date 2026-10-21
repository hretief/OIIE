using System.Text.Json;
using CirProvider.Application;
using CirProvider.Domain;
using CirProvider.Infrastructure.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace SimHost.Tests;

public sealed class CirEquivalentEntryTests
{
    [Fact]
    public void Existing_requests_default_to_the_anchor_category()
    {
        var destination = Request().Destination();
        Assert.Equal("FunctionalLocation", destination.CategoryId);
        Assert.Equal("REG-LOCATION", destination.CategorySourceId);
        Assert.Equal("test", destination.RegistryId);
    }

    [Fact]
    public void Explicit_destination_does_not_modify_the_anchor_identifiers()
    {
        var request = CrossCategory();
        Assert.Equal("ASSET", request.Destination().CategoryId);
        Assert.Equal("MMS", request.Destination().CategorySourceId);
        Assert.Equal("FunctionalLocation", request.CategoryId);
        Assert.Equal("REG-LOCATION", request.CategorySourceId);
    }

    [Theory]
    [InlineData(null, "MMS")]
    [InlineData("ASSET", null)]
    [InlineData("", "MMS")]
    [InlineData("ASSET", " ")]
    public void Partial_or_empty_destination_is_an_invalid_request(string? category, string? source)
    {
        var request = Request() with
        {
            DestinationCategoryId = category,
            DestinationCategorySourceId = source
        };
        Assert.Throws<JsonException>(() => request.Destination());
    }

    [CirSqlFact]
    public async Task SQL_equivalence_preserves_merge_semantics_and_supports_a_new_destination_category()
    {
        var options = Options.Create(new CirOptions
        {
            SqlConnectionString = Environment.GetEnvironmentVariable(CirSqlFactAttribute.Variable)!,
            AllowNewRegistries = true,
            AllowNewCategories = true
        });
        var store = new SqlCirStore(options, NullLogger<SqlCirStore>.Instance);
        var registry = $"test-mms-equivalent-{Guid.NewGuid():N}";
        var identity = Guid.NewGuid();
        try
        {
            await store.CreateRegistryAsync(new CreateRegistryRequest
            {
                Registry = [new Registry
                {
                    Id = registry, Categories = [new Category
                    {
                        Id = "FunctionalLocation", SourceId = "REG-LOCATION",
                        Entries =
                        [
                            new Entry { IdInSource = "7", SourceId = "REG-LOCATION", Cirid = identity },
                            new Entry { IdInSource = "8", SourceId = "REG-LOCATION" }
                        ]
                    }]
                }]
            });
            // Supplied destination CIRID must never displace an existing anchor identity.
            await store.CreateEquivalentEntriesAsync(
                [CrossCategory() with { RegistryId = registry, Entry = Request().Entry with { Cirid = Guid.NewGuid() } }]);
            // A legacy request still inserts in the original category.
            await store.CreateEquivalentEntriesAsync(
                [Request() with { RegistryId = registry, Entry = Request().Entry with { IdInSource = "legacy" } }]);
            // With no anchor identity, the supplied CIRID propagates back to the anchor.
            var propagated = Guid.NewGuid();
            await store.CreateEquivalentEntriesAsync(
                [CrossCategory() with
                {
                    RegistryId = registry, ExistingIdInSource = "8",
                    Entry = Request().Entry with { IdInSource = "102", Cirid = propagated }
                }]);
            var graph = await store.GetRegistryAsync(
                [new CirFilter { RegistryFilter = new RegistryFilter { Id = registry } }]);
            var categories = Assert.Single(graph).Categories;
            var asset = Assert.Single(categories, c => c.Id == "ASSET" && c.SourceId == "MMS");
            Assert.Equal(identity, Assert.Single(asset.Entries, e => e.IdInSource == "101").Cirid);
            Assert.Equal(propagated, Assert.Single(asset.Entries, e => e.IdInSource == "102").Cirid);
            var locations = Assert.Single(categories, c => c.Id == "FunctionalLocation");
            Assert.Equal(identity, Assert.Single(locations.Entries, e => e.IdInSource == "legacy").Cirid);
            Assert.Equal(propagated, Assert.Single(locations.Entries, e => e.IdInSource == "8").Cirid);

            var duplicate = await Assert.ThrowsAsync<CirFaultException>(() =>
                store.CreateEquivalentEntriesAsync([CrossCategory() with { RegistryId = registry }]));
            Assert.Equal(CirFaultCode.DuplicateEntryFault, duplicate.Faults[0].Code);
        }
        finally
        {
            await store.DeleteRegistryAsync(registry);
        }
    }

    private static EquivalentEntryRequest Request() => new()
    {
        RegistryId = "test", CategoryId = "FunctionalLocation", CategorySourceId = "REG-LOCATION",
        ExistingIdInSource = "7", ExistingSourceId = "REG-LOCATION",
        Entry = new Entry { IdInSource = "101", SourceId = "MMS" }
    };
    private static EquivalentEntryRequest CrossCategory() => Request() with
    {
        DestinationCategoryId = "ASSET", DestinationCategorySourceId = "MMS"
    };
}

public sealed class CirSqlFactAttribute : FactAttribute
{
    public const string Variable = "OIIE_CIR_TEST_SQL_CONNECTION_STRING";
    public CirSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to a dedicated, schema-initialized CIR test database.";
    }
}
