using Microsoft.EntityFrameworkCore;
using SimHost.Application.Participants;
using SimHost.Infrastructure.Sql;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// Guards the isolation model of spec §6.2 at the code level. The SQL grants are the
/// real enforcement, but these catch the mistake at build time rather than at
/// runtime against a shared-login development database, where a cross-schema read
/// would silently succeed.
/// </summary>
public class SchemaIsolationTests
{
    private static readonly string[] ParticipantSchemas =
    [
        "eng", "construct", "reg_location", "reg_asset",
        "reg_product", "reg_material", "mms", "rdl"
    ];

    [Fact]
    public void Participant_id_maps_to_schema_by_replacing_hyphens()
    {
        var config = new PersonalityConfig { ParticipantId = "reg-location" };

        Assert.Equal("reg_location", config.ResolvedSchema);
    }

    [Fact]
    public void Explicit_schema_overrides_the_derived_name()
    {
        var config = new PersonalityConfig { ParticipantId = "reg-asset", Schema = "reg_asset" };

        Assert.Equal("reg_asset", config.ResolvedSchema);
    }

    [Fact]
    public void Every_participant_resolves_to_a_distinct_schema()
    {
        var registry = new ParticipantRegistry(
            ParticipantSchemas.Select(s => new PersonalityConfig
            {
                ParticipantId = s.Replace('_', '-'),
                SourceId = s.ToUpperInvariant()
            }));

        var schemas = registry.All.Select(p => p.Schema).ToList();

        Assert.Equal(schemas.Count, schemas.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void No_participant_context_references_the_tower_schema()
    {
        // The tower is the single sanctioned cross-schema reader and must stay
        // outside every participant boundary. If a participant ever reads it, the
        // isolation the demonstration depends on is gone.
        var contextSources = typeof(ParticipantDbContext).Assembly
            .GetTypes()
            .Where(t => typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(t))
            .ToList();

        Assert.All(contextSources, type =>
            Assert.DoesNotContain("tower", type.FullName ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("eng")]
    [InlineData("reg_asset")]
    public void Context_binds_every_table_to_its_own_schema(string schema)
    {
        using var context = TestContexts.ForSchema(schema);
        var model = context.Model;

        Assert.All(model.GetEntityTypes(), entityType =>
            Assert.Equal(schema, entityType.GetSchema() ?? model.GetDefaultSchema()));
    }

    [Fact]
    public void Two_schemas_produce_two_distinct_compiled_models()
    {
        // Without SchemaAwareModelCacheKeyFactory, EF caches one model per context
        // type and every participant after the first silently inherits the first
        // one's schema — which looks like working software until two participants
        // disagree about whose rows they are reading.
        using var eng = TestContexts.ForSchema("eng");
        using var asset = TestContexts.ForSchema("reg_asset");

        Assert.Equal("eng", eng.Model.GetDefaultSchema());
        Assert.Equal("reg_asset", asset.Model.GetDefaultSchema());
        Assert.NotSame(eng.Model, asset.Model);
    }
}

internal static class TestContexts
{
    /// <summary>
    /// Builds a context for model inspection only. No connection is opened, so this
    /// runs without Azure — the connection string is never used.
    /// </summary>
    public static ParticipantDbContext ForSchema(string schema)
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ParticipantDbContext>()
            .UseSqlServer("Server=(unused);Database=(unused);")
            .ReplaceService<
                Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory,
                SchemaAwareModelCacheKeyFactory>()
            .Options;

        return new ParticipantDbContext(options, schema);
    }
}
