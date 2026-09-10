namespace Oiie.Cir.Client;

/// <summary>
/// The slice of the CIR model the engines exchange over HTTP.
///
/// Declared here rather than taken by ProjectReference from CirProvider for the
/// reason the REG-LOCATION contracts are: CIR is reached as a peer system, and
/// referencing it would let its storage concerns compile into every engine.
/// This library is the wire contract, nothing more.
/// </summary>
public sealed record CirPropertyValue(string? Key, string Value, string? UnitOfMeasure = null);

public sealed record CirProperty(string Id, string? DataType, IReadOnlyList<CirPropertyValue> PropertyValue);

public sealed record CirLocalizedText(string Value, string? LanguageId = null);

public sealed record CirEntry(
    string IdInSource,
    string SourceId,
    Guid? Cirid,
    string? SourceOwnerId,
    string? Name,
    CirLocalizedText? Description,
    IReadOnlyList<CirProperty> Properties);

public sealed record CirCategory(
    string Id,
    string SourceId,
    IReadOnlyList<CirLocalizedText> Description,
    IReadOnlyList<CirEntry> Entries);

public sealed record CirRegistry(
    string Id,
    IReadOnlyList<CirLocalizedText> Description,
    IReadOnlyList<CirCategory> Categories);

public sealed record CreateRegistryRequest(IReadOnlyList<CirRegistry> Registry, bool CreateCirid);

/// <summary>
/// Names one CIR entry by the five-part composite key CIR matches on.
/// </summary>
/// <remarks>
/// Every part is required. Getting one wrong does not match a different entry,
/// it matches none, so a cancel built from the wrong field silently removes
/// nothing.
///
/// The trap is <paramref name="EntryIdInSource"/>: for the entries REG-LOCATION
/// writes it is the REG-LOCATION integer id -- the scopeId or itemId -- not the
/// federation GUID. The GUID lives in the entry's Cirid.
/// </remarks>
public sealed record CirEntryIdentifier(
    string RegistryId,
    string CategoryId,
    string CategorySourceId,
    string EntryIdInSource,
    string EntrySourceId);

/// <summary>
/// One condition set in a <c>GetRegistry</c> query -- ws-CIR §3.2.1.
/// </summary>
/// <remarks>
/// Filter types within one <see cref="CirFilter"/> AND together; multiple
/// filters of the same type OR together. A null member means "unfiltered", so
/// an empty filter matches everything -- which is why a lookup must set enough
/// members to name exactly one entry rather than relying on the first result.
/// </remarks>
public sealed record CirFilter
{
    public CirRegistryFilter? RegistryFilter { get; init; }
    public CirCategoryFilter? CategoryFilter { get; init; }
    public CirEntryFilter? EntryFilter { get; init; }
}

public sealed record CirRegistryFilter
{
    public string? Id { get; init; }
}

public sealed record CirCategoryFilter
{
    public string? Id { get; init; }
    public string? SourceId { get; init; }
}

public sealed record CirEntryFilter
{
    public string? IdInSource { get; init; }
    public string? SourceId { get; init; }
    public string? SourceOwnerId { get; init; }
    public string? Name { get; init; }
    public Guid? Cirid { get; init; }
    public bool? Inactive { get; init; }
}

/// <summary>
/// A <c>GetRegistry</c> query: the CIR half of the GetRegistry/ShowRegistry BOD
/// pair, expressed over the provider's REST route.
/// </summary>
public sealed record GetRegistryRequest(IReadOnlyList<CirFilter> Filter);

/// <summary>
/// What <c>ShowRegistry</c> carries back: the matched registries, nested down to
/// the entries that satisfied the filter.
/// </summary>
public sealed record GetRegistryResponse(IReadOnlyList<CirRegistry> Registry);
