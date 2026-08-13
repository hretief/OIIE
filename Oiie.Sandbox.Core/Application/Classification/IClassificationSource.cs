using SimHost.Domain.Common;

namespace SimHost.Application.Classification;

/// <summary>
/// Read model the resolver walks. Kept behind an interface so the resolution
/// logic is unit-testable without a database — it is pure logic and the piece
/// most worth testing directly.
/// </summary>
public interface IClassificationSource
{
    ClassDefinition? GetClass(Guid classId);

    ClassDefinition? FindClassByKey(string classKey);

    /// <summary>
    /// Every class this participant holds.
    ///
    /// Each repository keeps its own reference data -- ENG works to the full
    /// library because engineering is where classification originates, while
    /// REG-LOCATION deliberately holds less. Enumerating is what lets a caller
    /// offer the classes a participant can actually bind, rather than accepting
    /// a typed key that turns out to be unmappable only after it has been
    /// published.
    /// </summary>
    IReadOnlyList<ClassDefinition> ListClasses();

    PropertyDefinition? GetPropertyDefinition(Guid definitionId);

    PropertyDefinition? FindPropertyDefinitionByKey(string definitionKey);

    IReadOnlyList<ClassProperty> GetClassProperties(Guid classId);

    IReadOnlyList<EntityClassification> GetClassifications(string entityType, string entityKey);
}

/// <summary>
/// Snapshot source. Loaded per participant and refreshed when definitions arrive
/// over the bus, so resolution never issues a query per property.
/// </summary>
public sealed class InMemoryClassificationSource : IClassificationSource
{
    private readonly Dictionary<Guid, ClassDefinition> _classesById;
    private readonly Dictionary<string, ClassDefinition> _classesByKey;
    private readonly Dictionary<Guid, PropertyDefinition> _definitionsById;
    private readonly Dictionary<string, PropertyDefinition> _definitionsByKey;
    private readonly ILookup<Guid, ClassProperty> _classProperties;
    private readonly ILookup<string, EntityClassification> _classifications;

    public InMemoryClassificationSource(
        IEnumerable<ClassDefinition> classes,
        IEnumerable<PropertyDefinition> definitions,
        IEnumerable<ClassProperty> classProperties,
        IEnumerable<EntityClassification> classifications)
    {
        var classList = classes.ToList();
        _classesById = classList.ToDictionary(c => c.Id);
        _classesByKey = classList
            .GroupBy(c => c.ClassKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Version).First(), StringComparer.Ordinal);

        var definitionList = definitions.ToList();
        _definitionsById = definitionList.ToDictionary(d => d.Id);
        _definitionsByKey = definitionList
            .GroupBy(d => d.DefinitionKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        _classProperties = classProperties.ToLookup(p => p.ClassId);
        _classifications = classifications.ToLookup(EntityKeyOf, StringComparer.Ordinal);
    }

    public ClassDefinition? GetClass(Guid classId) =>
        _classesById.TryGetValue(classId, out var value) ? value : null;

    public ClassDefinition? FindClassByKey(string classKey) =>
        _classesByKey.TryGetValue(classKey, out var value) ? value : null;

    // Keyed rather than the raw list: _classesByKey already resolves each key to
    // its highest version, so enumerating it cannot offer a superseded class.
    public IReadOnlyList<ClassDefinition> ListClasses() =>
        _classesByKey.Values.OrderBy(c => c.ClassKey, StringComparer.Ordinal).ToList();

    public PropertyDefinition? GetPropertyDefinition(Guid definitionId) =>
        _definitionsById.TryGetValue(definitionId, out var value) ? value : null;

    public PropertyDefinition? FindPropertyDefinitionByKey(string definitionKey) =>
        _definitionsByKey.TryGetValue(definitionKey, out var value) ? value : null;

    public IReadOnlyList<ClassProperty> GetClassProperties(Guid classId) =>
        _classProperties[classId].ToList();

    public IReadOnlyList<EntityClassification> GetClassifications(string entityType, string entityKey) =>
        _classifications[$"{entityType}|{entityKey}"].ToList();

    private static string EntityKeyOf(EntityClassification classification) =>
        $"{classification.EntityType}|{classification.EntityKey}";
}
