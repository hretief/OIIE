using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimHost.Application.Classification;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Domain.Eng;
using SimHost.Infrastructure.Sql;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SimHost.Application.Classification;

// --- Fixture shape ----------------------------------------------------------

public sealed class ClassFixture
{
    public List<PropertyDefinitionFixture> PropertyDefinitions { get; set; } = [];
    public List<ClassFixtureEntry> Classes { get; set; } = [];
    public List<RelationshipTypeFixture> RelationshipTypes { get; set; } = [];
}

/// <summary>
/// The kinds of design relationship a participant recognises. Reference data like
/// the classes above, and asymmetric for the same reason: a participant that has
/// never been told what "Supplies" means should behave like one, not silently
/// acquire the vocabulary.
/// </summary>
public sealed class RelationshipTypeFixture
{
    public string Key { get; set; } = string.Empty;
    public string ForwardRole { get; set; } = string.Empty;
    public string InverseRole { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class PropertyDefinitionFixture
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DataType { get; set; } = "Character";
    public string? UnitOfMeasure { get; set; }
    public string? CodeListId { get; set; }
}

public sealed class ClassFixtureEntry
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Parent { get; set; }
    public string AppliesTo { get; set; } = "Segment";
    public string Kind { get; set; } = "Taxonomy";
    public List<ClassPropertyFixture> Properties { get; set; } = [];
}

public sealed class ClassPropertyFixture
{
    public string Definition { get; set; } = string.Empty;
    public string Requirement { get; set; } = "Optional";
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public string? DefaultUom { get; set; }
    public string? CodeListId { get; set; }
    public string? DisplayGroup { get; set; }
    public int? DisplayOrder { get; set; }
}

// --- Loader -----------------------------------------------------------------

public sealed record FixtureLoadResult(
    string ParticipantId, int Classes, int Definitions, int ClassProperties, string? Error = null);

/// <summary>
/// Loads reference data from each personality's Fixtures/classes.yaml.
///
/// Fixtures are files in git rather than rows, so a scenario is reproducible from a
/// commit hash. They are deliberately asymmetric across participants: a library
/// everyone held completely would make graceful degradation untestable, and that
/// behaviour is the argument for governed reference data.
/// </summary>
public sealed class ClassFixtureLoader(
    IParticipantDbContextFactory factory,
    ILogger<ClassFixtureLoader> logger)
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<FixtureLoadResult> LoadAsync(
        ParticipantContext participant, string personalitiesRoot, CancellationToken ct = default)
    {
        var path = Path.Combine(personalitiesRoot, participant.ParticipantId, "Fixtures", "classes.yaml");

        if (!File.Exists(path))
        {
            return new FixtureLoadResult(participant.ParticipantId, 0, 0, 0, "No fixture file.");
        }

        ClassFixture fixture;
        try
        {
            fixture = Deserializer.Deserialize<ClassFixture>(await File.ReadAllTextAsync(path, ct))
                      ?? new ClassFixture();
        }
        catch (Exception ex)
        {
            return new FixtureLoadResult(participant.ParticipantId, 0, 0, 0, ex.Message);
        }

        await using var db = factory.Create(participant.ParticipantId);

        var definitions = new Dictionary<string, PropertyDefinition>(StringComparer.Ordinal);

        foreach (var entry in fixture.PropertyDefinitions)
        {
            var existing = await db.PropertyDefinitions
                .FirstOrDefaultAsync(d => d.DefinitionKey == entry.Key, ct);

            var definition = existing ?? new PropertyDefinition { Id = StableId(entry.Key) };

            definition.DefinitionKey = entry.Key;
            definition.Name = entry.Name;
            definition.Description = entry.Description;
            definition.Origin = DefinitionOrigin.Rdl;
            definition.RdlSourceId = "MIMOSA-RDL";
            definition.DataType = Enum.Parse<PropertyDataType>(entry.DataType, ignoreCase: true);
            definition.UnitOfMeasure = entry.UnitOfMeasure;
            definition.CodeListId = entry.CodeListId;

            if (existing is null) db.PropertyDefinitions.Add(definition);
            definitions[entry.Key] = definition;
        }

        var classes = new Dictionary<string, ClassDefinition>(StringComparer.Ordinal);

        foreach (var entry in fixture.Classes)
        {
            var existing = await db.Classes.FirstOrDefaultAsync(c => c.ClassKey == entry.Key, ct);
            var definition = existing ?? new ClassDefinition { Id = StableId(entry.Key) };

            definition.ClassKey = entry.Key;
            definition.Name = entry.Name;
            definition.Description = entry.Description;
            definition.AppliesTo = entry.AppliesTo;
            definition.Kind = Enum.Parse<ClassKind>(entry.Kind, ignoreCase: true);
            definition.Origin = DefinitionOrigin.Rdl;
            definition.RdlSourceId = "MIMOSA-RDL";
            definition.Version = "1";

            if (existing is null) db.Classes.Add(definition);
            classes[entry.Key] = definition;
        }

        // Parents in a second pass: a fixture file may list a child before its
        // parent, and requiring a particular order would make the files brittle.
        foreach (var entry in fixture.Classes.Where(e => e.Parent is { Length: > 0 }))
        {
            if (classes.TryGetValue(entry.Parent!, out var parent))
            {
                classes[entry.Key].ParentClassId = parent.Id;
            }
            else
            {
                logger.LogWarning(
                    "{ParticipantId}: class {Key} names parent {Parent}, which the fixture does not define.",
                    participant.ParticipantId, entry.Key, entry.Parent);
            }
        }

        var classPropertyCount = 0;

        foreach (var entry in fixture.Classes)
        {
            var owner = classes[entry.Key];

            foreach (var property in entry.Properties)
            {
                if (!definitions.TryGetValue(property.Definition, out var definition))
                {
                    logger.LogWarning(
                        "{ParticipantId}: class {Key} references undefined property {Definition}.",
                        participant.ParticipantId, entry.Key, property.Definition);
                    continue;
                }

                var existing = await db.ClassProperties.FirstOrDefaultAsync(
                    p => p.ClassId == owner.Id && p.DefinitionId == definition.Id, ct);

                var classProperty = existing ?? new ClassProperty
                {
                    ClassId = owner.Id,
                    DefinitionId = definition.Id
                };

                classProperty.Requirement = Enum.Parse<PropertyRequirement>(
                    property.Requirement, ignoreCase: true);
                classProperty.MinValue = property.MinValue;
                classProperty.MaxValue = property.MaxValue;
                classProperty.DefaultUom = property.DefaultUom ?? definition.UnitOfMeasure;
                classProperty.CodeListId = property.CodeListId;
                classProperty.DisplayGroup = property.DisplayGroup ?? entry.Name;
                classProperty.DisplayOrder = property.DisplayOrder;

                if (existing is null) db.ClassProperties.Add(classProperty);
                classPropertyCount++;
            }
        }

        foreach (var entry in fixture.RelationshipTypes)
        {
            var existing = await db.Set<TagRelationshipType>()
                .FirstOrDefaultAsync(t => t.Key == entry.Key, ct);

            var type = existing ?? new TagRelationshipType { Key = entry.Key };

            type.ForwardRole = entry.ForwardRole;
            type.InverseRole = entry.InverseRole;
            type.Description = entry.Description;

            if (existing is null) db.Set<TagRelationshipType>().Add(type);
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{ParticipantId}: loaded {Classes} class(es), {Definitions} definition(s)",
            participant.ParticipantId, classes.Count, definitions.Count);

        return new FixtureLoadResult(
            participant.ParticipantId, classes.Count, definitions.Count, classPropertyCount);
    }

    /// <summary>
    /// Deterministic id from the key, so the same class carries the same id in every
    /// participant that holds it and across resets. Keys are the shared identity in
    /// reference data; random ids would obscure that in every diagnostic query.
    /// </summary>
    private static Guid StableId(string key) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}

/// <summary>
/// Rebuilds each participant's in-memory classification snapshot from its schema.
///
/// Resolution walks chains and unions property sets on every ingest, so it reads
/// from a snapshot rather than issuing a query per property. The snapshot is
/// refreshed after fixtures load and, later, when definitions arrive over the bus —
/// which is what lets a class published by the RDL take effect without a restart.
/// </summary>
public sealed class ClassificationRefresher(
    IParticipantDbContextFactory factory,
    ParticipantRegistry registry,
    ILogger<ClassificationRefresher> logger)
{
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        foreach (var participant in registry.All)
        {
            try
            {
                await RefreshAsync(participant, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Could not refresh classification for {ParticipantId}", participant.ParticipantId);
            }
        }
    }

    public async Task RefreshAsync(ParticipantContext participant, CancellationToken ct = default)
    {
        await using var db = factory.Create(participant.ParticipantId);

        var classes = await db.Classes.AsNoTracking().ToListAsync(ct);
        var definitions = await db.PropertyDefinitions.AsNoTracking().ToListAsync(ct);
        var classProperties = await db.ClassProperties.AsNoTracking().ToListAsync(ct);
        var classifications = await db.Classifications.AsNoTracking().ToListAsync(ct);

        participant.RefreshClassification(
            new InMemoryClassificationSource(classes, definitions, classProperties, classifications));

        logger.LogDebug(
            "{ParticipantId}: classification snapshot refreshed ({Classes} classes)",
            participant.ParticipantId, classes.Count);
    }
}
