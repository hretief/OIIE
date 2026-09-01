namespace MmsProvider.Application;

/// <summary>
/// Configuration for the MMS emulation.
/// </summary>
public sealed class MmsOptions
{
    public string SqlConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Applies schema.sql at startup. Left on for dev and demo; a real customer
    /// system would have its schema managed elsewhere.
    /// </summary>
    public bool AutoCreateSchema { get; set; } = true;
}

/// <summary>
/// The MMS asset store.
///
/// This is the contract a caller codes against. It is deliberately expressed in
/// MMS's own vocabulary — Site, Asset, AssetNumber — and knows nothing of CCOM,
/// Segments, BODs or functional locations. A customer system does not know it is
/// being integrated with, and translation is the integrator's job, not MMS's.
///
/// Note what is absent: no connection string, no schema detail, no transaction
/// handle, no initialisation member. Callers cannot reach past this boundary.
/// </summary>
public interface IMmsAssetStore
{
    /// <summary>
    /// Day zero: drops every table and recreates the schema, leaving MMS empty.
    ///
    /// Destructive and unconditional. Present on the store rather than beside
    /// the schema initialiser because a caller resetting MMS is asking the
    /// store to forget, not asking for DDL to be applied -- the recreate is an
    /// implementation detail of leaving the system usable afterwards.
    /// </summary>
    Task ResetAsync(CancellationToken ct);

    Task<IReadOnlyList<MmsSite>> GetSitesAsync(CancellationToken ct);

    Task<MmsSite?> FindSiteByCodeAsync(string siteCode, CancellationToken ct);

    Task<MmsSite?> FindSiteAsync(Guid siteId, CancellationToken ct);

    /// <summary>
    /// Creates sites, or updates those already present.
    ///
    /// The caller supplies SiteID, and it is matched on directly. This customer
    /// system happens to store the originator's identifier in its primary key, so
    /// there is no key to allocate and nothing to hand back that the caller did
    /// not already know.
    ///
    /// That convenience is local to this schema and must not be relied upon. A
    /// caller still registers the mapping in CIR afterwards, because the next
    /// customer system will keep its identity in a side column or not at all, and
    /// a consumer resolving a MMS site cannot be expected to know which.
    /// </summary>
    Task<SiteUpsertResult> UpsertSitesAsync(
        IReadOnlyList<SiteUpsert> sites,
        CancellationToken ct);

    Task<MmsAsset?> FindAssetAsync(Guid assetId, CancellationToken ct);

    /// <summary>
    /// Finds an asset by the number it carries within a site.
    ///
    /// This is the lookup for a caller holding only what a person would read off
    /// the equipment. AssetNumber is unique per site, not globally, so the site
    /// has to be named: the same number means different things in two plants.
    /// </summary>
    Task<MmsAsset?> FindAssetByNumberAsync(Guid siteId, string assetNumber, CancellationToken ct);

    Task<IReadOnlyList<MmsAsset>> GetAssetsAsync(Guid? siteId, CancellationToken ct);

    /// <summary>
    /// The asset types MMS recognises.
    ///
    /// Exposed because a caller cannot otherwise state one. AssetTypeID is NOT
    /// NULL behind a foreign key, and an integrator has no way to discover the
    /// customer's taxonomy except by being told it.
    /// </summary>
    Task<IReadOnlyList<MmsAssetType>> GetAssetTypesAsync(CancellationToken ct);

    Task<MmsAssetType?> FindAssetTypeByCodeAsync(string assetTypeCode, CancellationToken ct);

    /// <summary>
    /// Creates assets, or updates those already present.
    ///
    /// Matched on AssetID, the identifier the originating system minted. That is a
    /// stronger match than any business key: an asset renumbered at the plant is
    /// still the same asset and updates the same row, where matching on
    /// AssetNumber would silently create a second one.
    ///
    /// Upsert rather than separate create and update calls because the caller
    /// cannot know which it needs without asking first, and asking would race.
    /// </summary>
    Task<AssetUpsertResult> UpsertAssetsAsync(
        IReadOnlyList<AssetUpsert> assets,
        CancellationToken ct);
}

public sealed record MmsSite(
    Guid SiteId,
    string SiteCode,
    string SiteName,
    string? Description,
    string? SiteType,
    Guid? ParentSiteId,
    string? Country,
    string? Region,
    string? Status,
    DateTime CreatedDate,
    DateTime? UpdatedDate);

/// <summary>
/// A site a caller wants to exist.
///
/// SiteID is required and is the caller's own identifier for the site. MMS mints
/// nothing here: a site arriving without an identity would be a site no other
/// system could ever refer to.
/// </summary>
public sealed record SiteUpsert(
    Guid SiteId,
    string SiteCode,
    string SiteName,
    string? Description,
    string? SiteType,
    Guid? ParentSiteId,
    string? Country,
    string? Region,
    string? Status);

public sealed record SiteUpsertResult(
    IReadOnlyList<UpsertedSite> Sites,
    IReadOnlyList<UpsertRejection> Rejections)
{
    public int Created => Sites.Count(s => s.Created);
    public int Updated => Sites.Count(s => !s.Created);
}

/// <summary>
/// One persisted site. Created distinguishes a new row from an update, which is
/// the only thing the caller could not already work out.
/// </summary>
public sealed record UpsertedSite(Guid SiteId, string SiteCode, bool Created);

public sealed record MmsAssetType(
    Guid AssetTypeId,
    string AssetTypeCode,
    string AssetTypeName,
    string? Description,
    Guid? ParentAssetTypeId,
    string? CriticalityClass,
    int? ExpectedLifeYears);

public sealed record MmsAsset(
    Guid AssetId,
    Guid SiteId,
    Guid AssetTypeId,
    string AssetNumber,
    string AssetName,
    string? Description,
    string? Manufacturer,
    string? ModelNumber,
    string? SerialNumber,
    DateOnly? CommissionDate,
    DateOnly? RetirementDate,
    decimal? CriticalityScore,
    string? RiskRanking,
    string? Status,
    Guid? ParentAssetId,
    DateTime CreatedDate,
    DateTime? UpdatedDate);

/// <summary>
/// An asset a caller wants to exist.
///
/// AssetTypeID is optional even though the column is NOT NULL: a sender
/// describing a segment rarely knows the customer's type taxonomy, and MMS falls
/// back to UNCLASSIFIED rather than refusing the asset.
///
/// Criticality, risk ranking, serial and manufacturer are absent. Those are set
/// by a planner once the asset is surveyed, and a caller asserting them would be
/// claiming knowledge MMS has no reason to trust.
/// </summary>
public sealed record AssetUpsert(
    Guid AssetId,
    Guid SiteId,
    string AssetNumber,
    string AssetName,
    string? Description,
    string? Status,
    Guid? AssetTypeId,
    Guid? ParentAssetId);

public sealed record AssetUpsertResult(
    IReadOnlyList<UpsertedAsset> Assets,
    IReadOnlyList<UpsertRejection> Rejections)
{
    public int Created => Assets.Count(a => a.Created);
    public int Updated => Assets.Count(a => !a.Created);
}

/// <summary>
/// One persisted asset. AssetTypeId is reported back because MMS may have
/// substituted UNCLASSIFIED for a type the caller did not state, and the caller
/// should be able to see that happened.
/// </summary>
public sealed record UpsertedAsset(Guid AssetId, Guid AssetTypeId, bool Created);

/// <summary>
/// Something MMS declined to store, and why. Shared by the site and asset paths:
/// the reason a customer system says no does not depend on what was asked.
/// </summary>
/// <param name="Key">
/// The identifier that was refused, so a caller can tell which item in a batch
/// this refers to.
/// </param>
/// <param name="Transient">
/// True only for causes a later identical attempt could clear: deadlock, lock
/// timeout, connection failure. A missing site or an absent required value is a
/// fact about the request and will never clear on retry.
/// </param>
public sealed record UpsertRejection(string Key, string Reason, bool Transient);
