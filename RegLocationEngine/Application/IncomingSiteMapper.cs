using Microsoft.Extensions.Options;
using Oiie.Ccom.Types;
using RegLocationEngine.Infrastructure.RegLocation;

namespace RegLocationEngine.Application;

/// <summary>
/// Why a site could not be registered.
/// </summary>
public enum SiteRejection
{
    None = 0,

    /// <summary>No federation GUID, so the site would have no shared identity.</summary>
    NoFederationId,

    /// <summary>Nothing usable as a name.</summary>
    NoName,

    /// <summary>No site type, so there is no item for the serial to hang off.</summary>
    NoType
}

/// <summary>
/// The outcome of translating one site.
///
/// Three requests rather than one, because a site is three rows in
/// REG-LOCATION. They are returned together and applied separately: the item
/// and serial both need a scope id that only exists once the scope has been
/// created, so the mapper cannot fill those in and the service must.
/// </summary>
public sealed record SiteMappingResult(
    SiteRejection Rejection,
    Guid SiteGuid,
    Guid TypeGuid,
    string Name,
    string? Description,
    string TypeCode,
    string? TypeDescription)
{
    public bool IsMapped => Rejection == SiteRejection.None;

    public static SiteMappingResult Rejected(SiteRejection rejection) =>
        new(rejection, Guid.Empty, Guid.Empty, string.Empty, null, string.Empty, null);
}

/// <summary>
/// Translates an incoming CCOM Site into what REG-LOCATION needs to hold it.
///
/// Pure and synchronous, like <see cref="IncomingSegmentMapper"/>: every
/// judgement about what an arriving site means is made here where it can be
/// tested without a broker or a database, and the service decides what to do
/// with the answer.
///
/// One site becomes three rows, and the reason is worth stating because it is
/// not obvious. A Scope is the container everything about the site is later
/// registered in -- it is what SyncSegments needs to exist. An Item is the site
/// *type*, shared by every site of that type. A Serial is this particular site,
/// an instance of that type. Collapsing any two of them would either give every
/// Highway project a separate notion of Highway, or leave the site with no
/// container of its own.
/// </summary>
public sealed class IncomingSiteMapper(IOptions<RegLocationEngineOptions> options)
{
    private readonly RegLocationEngineOptions _options = options.Value;

    public SiteMappingResult Map(Site site)
    {
        // The federated identity, which this leg cannot invent for the same
        // reason the segment mapper cannot: a minted GUID would look like it
        // worked, and the same site would exist twice across the federation
        // with nothing to show they were ever one thing.
        if (site.UUID == Guid.Empty)
        {
            return SiteMappingResult.Rejected(SiteRejection.NoFederationId);
        }

        var name = FirstNonBlank(site.ShortName, site.FullName, site.IDInInfoSource);
        if (name is null)
        {
            return SiteMappingResult.Rejected(SiteRejection.NoName);
        }

        // The type is not optional, unlike a segment's class -- which falls back
        // to a configured default. There is no equivalent fallback here, and
        // there should not be: an unmapped class files a location under a
        // slightly wrong heading, whereas a fabricated site type would become
        // the type every later site of that kind is reconciled against.
        var type = site.Type;
        if (type is null || type.UUID == Guid.Empty)
        {
            return SiteMappingResult.Rejected(SiteRejection.NoType);
        }

        var typeCode = FirstNonBlank(type.ShortName, type.IDInInfoSource);
        if (typeCode is null)
        {
            return SiteMappingResult.Rejected(SiteRejection.NoType);
        }

        return new SiteMappingResult(
            SiteRejection.None,
            SiteGuid: site.UUID,
            TypeGuid: type.UUID,
            Name: name,
            Description: FirstNonBlank(site.Description, site.FullName),
            TypeCode: typeCode,
            TypeDescription: FirstNonBlank(type.FullName, type.ShortName));
    }

    /// <summary>
    /// The scope a mapped site becomes.
    ///
    /// No context object yet: the serial it will point at does not exist when
    /// the scope is created. The service links them afterwards.
    /// </summary>
    public CreateScopeRequest ToScopeRequest(SiteMappingResult mapped) =>
        new(Name: mapped.Name,
            NamespaceId: _options.SiteNamespaceId,
            ParentId: null,
            ContextObjectId: null,
            ContextObjectType: null,
            Guid: mapped.SiteGuid);

    /// <summary>
    /// The item a mapped site's type becomes.
    ///
    /// Carries the type's own GUID rather than the site's, which is what makes
    /// the type reusable: the next site of this type finds this item by that
    /// GUID instead of creating a second one.
    /// </summary>
    public CreateItemRequest ToItemRequest(SiteMappingResult mapped, int scopeId) =>
        new(NamespaceId: _options.SiteNamespaceId,
            UnitId: _options.SiteUnitId,
            TrnId: _options.SiteTrnId,
            ScopeId: scopeId,
            Guid: mapped.TypeGuid,
            Code: mapped.TypeCode,
            Description: mapped.TypeDescription,

            // A constant, not the type string: item_type says what kind of item
            // the row is, and every site type is the same kind. The type string
            // is the item's identity and lives in Code.
            ItemType: _options.SiteItemType);

    /// <summary>
    /// The serial this particular site becomes.
    ///
    /// Shares the site's GUID with the scope, deliberately. They are two facets
    /// of one iTwin -- the thing and the context it provides -- and a receiver
    /// resolving the federation id should reach both. It is safe only because
    /// every lookup filters on object type; a query that did not would find a
    /// scope where it expected a serial.
    /// </summary>
    public CreateSerialRequest ToSerialRequest(SiteMappingResult mapped, int itemId, int scopeId) =>
        new(ItemId: itemId,
            Name: mapped.Name,
            ScopeId: scopeId,
            Description: mapped.Description,
            Guid: mapped.SiteGuid);

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
}
