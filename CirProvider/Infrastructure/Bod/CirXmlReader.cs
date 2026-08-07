using System.Xml.Linq;
using CirProvider.Domain;

namespace CirProvider.Infrastructure.Bod;

/// <summary>
/// Namespaces from ws-CIR §1.3 and the OAGIS platform specification.
/// </summary>
public static class CirNs
{
    public static readonly XNamespace Cir = "http://www.openoandm.org/ws-cir/";
    public static readonly XNamespace Oa = "http://www.openapplications.org/oagis/9";
    public static readonly XNamespace Cct = "urn:un:unece:uncefact:documentation:standard:CoreComponentType:2";
}

/// <summary>
/// Reads the ws-CIR logical data model (§2) out of XML.
///
/// Element names follow the §2 attribute names verbatim — ID, IDInSource,
/// SourceID, CIRID, SourceOwnerID — rather than the camelCase used by the JSON
/// binding. The service definition XSDs ship in the specification package and
/// are not reproduced here, so nesting is inferred from §2 and §3.
/// </summary>
public static class CirXmlReader
{
    private static XNamespace N => CirNs.Cir;

    // --- Scalars ------------------------------------------------------------

    private static string? Str(XElement parent, string name) =>
        parent.Element(N + name)?.Value;

    private static string Required(XElement parent, string name) =>
        Str(parent, name) ?? throw new System.Xml.XmlException(
            $"Required element '{name}' is missing from <{parent.Name.LocalName}>.");

    private static Guid? GuidValue(XElement parent, string name)
    {
        var raw = Str(parent, name);
        return Guid.TryParse(raw, out var g) ? g : null;
    }

    private static bool? Bool(XElement parent, string name)
    {
        var raw = Str(parent, name);
        return bool.TryParse(raw, out var b) ? b : null;
    }

    /// <summary>UN/CEFACT TextType: the value plus an optional languageID attribute.</summary>
    private static LocalizedText ToText(XElement element) => new()
    {
        Value = element.Value,
        LanguageId = element.Attribute("languageID")?.Value
                     ?? element.Attribute(CirNs.Cct + "languageID")?.Value
    };

    private static IReadOnlyList<LocalizedText> Texts(XElement parent, string name) =>
        parent.Elements(N + name).Select(ToText).ToList();

    // --- Data model ---------------------------------------------------------

    public static Registry ReadRegistry(XElement element) => new()
    {
        Id = Required(element, "ID"),
        Description = Texts(element, "Description"),
        Categories = element.Elements(N + "Category").Select(ReadCategory).ToList()
    };

    public static Category ReadCategory(XElement element) => new()
    {
        Id = Required(element, "ID"),
        // Category declares CategorySourceID; only Entry uses a bare SourceID.
        // 'SourceID' is accepted as a fallback for clients written against the
        // JSON binding, where the property is camelCase sourceId on both types.
        SourceId = Str(element, "CategorySourceID")
                   ?? Required(element, "SourceID"),
        Description = Texts(element, "Description"),
        Entries = element.Elements(N + "Entry").Select(ReadEntry).ToList()
    };

    public static Entry ReadEntry(XElement element) => new()
    {
        IdInSource = Required(element, "IDInSource"),
        SourceId = Required(element, "SourceID"),
        Cirid = GuidValue(element, "CIRID"),
        SourceOwnerId = Str(element, "SourceOwnerID"),
        Name = Str(element, "Name"),
        Description = element.Element(N + "Description") is { } d ? ToText(d) : null,
        Inactive = Bool(element, "Inactive"),
        Properties = element.Elements(N + "Property").Select(ReadProperty).ToList()
    };

    public static Property ReadProperty(XElement element) => new()
    {
        Id = Required(element, "ID"),
        DataType = Str(element, "DataType"),
        PropertyValue = element.Elements(N + "PropertyValue").Select(ReadPropertyValue).ToList()
    };

    public static PropertyValue ReadPropertyValue(XElement element) => new()
    {
        Key = Str(element, "Key"),
        Value = Str(element, "Value") ?? string.Empty,
        UnitOfMeasure = Str(element, "UnitOfMeasure")
    };

    // --- Identifiers --------------------------------------------------------

    public static CategoryIdentifier ReadCategoryIdentifier(XElement element) => new()
    {
        RegistryId = Required(element, "RegistryID"),
        CategoryId = Required(element, "CategoryID"),
        CategorySourceId = Required(element, "CategorySourceID")
    };

    public static EntryIdentifier ReadEntryIdentifier(XElement element) => new()
    {
        RegistryId = Required(element, "RegistryID"),
        CategoryId = Required(element, "CategoryID"),
        CategorySourceId = Required(element, "CategorySourceID"),
        EntryIdInSource = Required(element, "EntryIDInSource"),
        EntrySourceId = Required(element, "EntrySourceID")
    };

    public static PropertyIdentifier ReadPropertyIdentifier(XElement element) => new()
    {
        RegistryId = Required(element, "RegistryID"),
        CategoryId = Required(element, "CategoryID"),
        CategorySourceId = Required(element, "CategorySourceID"),
        EntryIdInSource = Required(element, "EntryIDInSource"),
        EntrySourceId = Required(element, "EntrySourceID"),
        PropertyId = Required(element, "PropertyID")
    };

    // --- Filters (§3.2.1) ---------------------------------------------------

    public static CirFilter ReadFilter(XElement element) => new()
    {
        RegistryFilter = element.Element(N + "RegistryFilter") is { } r
            ? new RegistryFilter { Id = Str(r, "ID"), Description = Str(r, "Description") }
            : null,

        CategoryFilter = element.Element(N + "CategoryFilter") is { } c
            ? new CategoryFilter
            {
                Id = Str(c, "ID"),
                SourceId = Str(c, "CategorySourceID") ?? Str(c, "SourceID"),
                Description = Str(c, "Description")
            }
            : null,

        EntryFilter = element.Element(N + "EntryFilter") is { } e
            ? new EntryFilter
            {
                IdInSource = Str(e, "IDInSource"),
                SourceId = Str(e, "SourceID"),
                SourceOwnerId = Str(e, "SourceOwnerID"),
                Name = Str(e, "Name"),
                Description = Str(e, "Description"),
                Cirid = GuidValue(e, "CIRID"),
                Inactive = Bool(e, "Inactive")
            }
            : null,

        PropertyFilter = element.Element(N + "PropertyFilter") is { } p
            ? new PropertyFilter { Id = Str(p, "ID"), Key = Str(p, "Key"), Value = Str(p, "Value") }
            : null
    };

    // --- Noun payloads ------------------------------------------------------

    public static CreateRegistryRequest ReadCreateRegistry(XElement noun) => new()
    {
        Registry = noun.Elements(N + "Registry").Select(ReadRegistry).ToList(),
        CreateCirid = Bool(noun, "CreateCIRID") ?? false
    };

    public static IReadOnlyList<Registry> ReadRegistries(XElement noun) =>
        noun.Elements(N + "Registry").Select(ReadRegistry).ToList();

    public static IReadOnlyList<EquivalentEntryRequest> ReadCreateEquivalentEntries(XElement noun) =>
        noun.Elements(N + "EquivalentEntry").Select(e => new EquivalentEntryRequest
        {
            ExistingIdInSource = Required(e, "ExistingIDInSource"),
            ExistingSourceId = Required(e, "ExistingSourceID"),
            RegistryId = Required(e, "RegistryID"),
            CategoryId = Required(e, "CategoryID"),
            CategorySourceId = Required(e, "CategorySourceID"),
            Entry = ReadEntry(e.Element(N + "Entry")
                ?? throw new System.Xml.XmlException("EquivalentEntry is missing its Entry element."))
        }).ToList();

    public static UpdateEntryCiridRequest ReadUpdateEntryCirid(XElement noun) => new()
    {
        OldCirid = noun.Elements(N + "OldCIRID")
                       .Select(x => Guid.Parse(x.Value))
                       .ToList(),
        NewCirid = Guid.Parse(Required(noun, "NewCIRID"))
    };

    public static IReadOnlyList<CirFilter> ReadFilters(XElement noun) =>
        noun.Elements(N + "Filter").Select(ReadFilter).ToList();

    public static (IReadOnlyList<EntryIdentifier> Ids, IReadOnlyList<string> Targets) ReadGetEquivalentEntries(XElement noun) =>
        (noun.Elements(N + "EntryIdentifier").Select(ReadEntryIdentifier).ToList(),
         noun.Elements(N + "TargetSourceID").Select(x => x.Value).ToList());

    public static (Guid Cirid, IReadOnlyList<string> Targets) ReadGetEntriesByCirid(XElement noun) =>
        (Guid.Parse(Required(noun, "CIRID")),
         noun.Elements(N + "TargetSourceID").Select(x => x.Value).ToList());
}
