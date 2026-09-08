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

    /// <summary>
    /// Applies refdata.sql after schema.sql. Seeds the TAMS lookup tables
    /// (asset status, owner, jurisdiction, and so on) that the asset tables'
    /// foreign keys require before any real asset can be inserted.
    /// </summary>
    public bool AutoSeedRefData { get; set; } = true;
}

/// <summary>
/// The MMS asset store, over the MnDOT TAMS lighting domain.
///
/// This is the contract a caller codes against. It is deliberately expressed in
/// TAMS's own vocabulary -- Light System, Light Unit -- and knows nothing of
/// CCOM, Segments, BODs or functional locations. A customer system does not
/// know it is being integrated with, and translation is the integrator's job,
/// not MMS's.
///
/// Only a working subset of TAMS's roughly forty asset columns is surfaced
/// here: identity, classification, ownership, condition and the columns an
/// integrator would plausibly assert. The remainder (GIS geometry, linear
/// referencing, inspection detail) exists in schema.sql because the real
/// customer schema carries it, but nothing in this solution populates it, and
/// adding it to the contract before a caller needs it would be surface area
/// with nothing behind it.
///
/// Note what is absent: no connection string, no schema detail, no transaction
/// handle, no initialisation member. Callers cannot reach past this boundary.
/// </summary>
public interface IMmsAssetStore
{
    /// <summary>
    /// Day zero: drops every table and recreates the schema and reference data,
    /// leaving MMS in its seeded, asset-free state.
    ///
    /// Destructive and unconditional. Present on the store rather than beside
    /// the schema initialiser because a caller resetting MMS is asking the
    /// store to forget, not asking for DDL to be applied -- the recreate is an
    /// implementation detail of leaving the system usable afterwards.
    /// </summary>
    Task ResetAsync(CancellationToken ct);

    Task<IReadOnlyList<MmsLightSystem>> GetLightSystemsAsync(CancellationToken ct);

    Task<MmsLightSystem?> FindLightSystemAsync(long lightSystemId, CancellationToken ct);

    /// <summary>
    /// Finds a light system by the federation identifier its originating
    /// system carries, stored in EXT_ASSET_ID.
    ///
    /// TAMS itself has no notion of federation; EXT_ASSET_ID is the free-text
    /// column the real schema already carries on this table, repurposed as the
    /// bridge a caller uses to avoid creating the same system twice.
    /// </summary>
    Task<MmsLightSystem?> FindLightSystemByExtAssetIdAsync(Guid extAssetId, CancellationToken ct);

    /// <summary>
    /// Creates light systems, or updates those already present.
    ///
    /// Matched on ExtAssetId, not on TAMS's own identity column: the caller
    /// does not know the TAMS-minted LightSystemId until after the first
    /// upsert, so the federation id is the only key it can supply up front.
    /// That id still has to be registered in CIR afterwards -- storing it in
    /// EXT_ASSET_ID is local convenience, not a substitute for the registry.
    /// </summary>
    Task<LightSystemUpsertResult> UpsertLightSystemsAsync(
        IReadOnlyList<LightSystemUpsert> systems,
        CancellationToken ct);

    Task<IReadOnlyList<MmsLightUnit>> GetLightUnitsAsync(long? lightSystemId, CancellationToken ct);

    Task<MmsLightUnit?> FindLightUnitAsync(long lightUnitId, CancellationToken ct);

    Task<MmsLightUnit?> FindLightUnitByExtAssetIdAsync(Guid extAssetId, CancellationToken ct);

    /// <summary>
    /// Creates light units, or updates those already present. See
    /// UpsertLightSystemsAsync for why matching is on ExtAssetId.
    /// </summary>
    Task<LightUnitUpsertResult> UpsertLightUnitsAsync(
        IReadOnlyList<LightUnitUpsert> units,
        CancellationToken ct);

    // ---- Lookups ------------------------------------------------------
    //
    // Exposed because a caller cannot otherwise state one: these are TAMS's
    // own code tables, behind NOT NULL or nullable foreign keys on the asset
    // tables, and an integrator has no way to discover the customer's values
    // except by being told them.

    Task<IReadOnlyList<MmsLookup>> GetLightSystemClassCodesAsync(CancellationToken ct);

    Task<IReadOnlyList<MmsLookup>> GetAssetStatusesAsync(CancellationToken ct);

    Task<IReadOnlyList<MmsLookup>> GetOwnersAsync(CancellationToken ct);

    Task<IReadOnlyList<MmsLookup>> GetCountiesAsync(CancellationToken ct);

    Task<IReadOnlyList<MmsLookup>> GetJurisdictionCodesAsync(CancellationToken ct);

    // ---- Owners -------------------------------------------------------
    //
    // SETUP_OWNER is a lookup table to every other path in this store, but it
    // is the target of the sites leg: a CCOM Site is a context owner, not a
    // lighting installation, so this is the one lookup a caller may write to.

    /// <summary>
    /// Creates owners, or renames those already present.
    ///
    /// Matched on OWNER_NAME when the caller supplies no OwnerId, because a
    /// caller establishing a site for the first time has no TAMS key to offer
    /// -- OWNER_ID is IDENTITY-assigned and only knowable after the insert.
    /// Callers that already resolved the owner through CIR pass OwnerId, which
    /// makes the operation a rename rather than a match.
    /// </summary>
    Task<OwnerUpsertResult> UpsertOwnersAsync(
        IReadOnlyList<OwnerUpsert> owners,
        CancellationToken ct);
}

/// <summary>
/// An owner a caller wants to exist in SETUP_OWNER.
/// </summary>
/// <param name="OwnerId">
/// The TAMS key when the caller already knows it, which it does only after
/// resolving the site through CIR. Null means "find by name or create".
/// Supplying it makes OwnerName authoritative over the stored value, so an
/// upstream rename lands rather than creating a second owner.
/// </param>
public sealed record OwnerUpsert(string OwnerName, long? OwnerId = null);

/// <summary>
/// One persisted owner. OwnerId is reported back because it is TAMS-minted and
/// is the value the caller must register in CIR.
/// </summary>
public sealed record UpsertedOwner(long OwnerId, string OwnerName, bool Created);

public sealed record OwnerUpsertResult(
    IReadOnlyList<UpsertedOwner> Owners,
    IReadOnlyList<UpsertRejection> Rejections)
{
    public int Created => Owners.Count(o => o.Created);
    public int Updated => Owners.Count(o => !o.Created);
}

/// <summary>One row of a TAMS SETUP_* / *_CLASS_CODE lookup table.</summary>
public sealed record MmsLookup(long Id, string Name, bool ActiveFlag);

public sealed record MmsLightSystem(
    long LightSystemId,
    string LightSystemName,
    long LightSystemClassCodeId,
    long? LightSystemStatusId,
    long? OwnerId,
    long? SglElecJurOwnerId,
    long? CountyId,
    string? ExtAssetId,
    DateTime? DateUpdate);

/// <summary>
/// A light system a caller wants to exist.
///
/// ExtAssetId is required and is the caller's own federation identifier for
/// the system. TAMS mints its own LightSystemId regardless: a system arriving
/// with no federation id would be a system no other system could ever refer
/// to again.
/// </summary>
public sealed record LightSystemUpsert(
    Guid ExtAssetId,
    string LightSystemName,
    long LightSystemClassCodeId,
    long? LightSystemStatusId,
    long? OwnerId,
    long? SglElecJurOwnerId,
    long? CountyId);

public sealed record LightSystemUpsertResult(
    IReadOnlyList<UpsertedLightSystem> Systems,
    IReadOnlyList<UpsertRejection> Rejections)
{
    public int Created => Systems.Count(s => s.Created);
    public int Updated => Systems.Count(s => !s.Created);
}

/// <summary>
/// One persisted light system. LightSystemId is reported back because it is
/// TAMS-minted: the caller supplied ExtAssetId and has no other way to learn
/// the key it can now use to attach units.
/// </summary>
public sealed record UpsertedLightSystem(long LightSystemId, Guid ExtAssetId, bool Created);

public sealed record MmsLightUnit(
    long LightUnitId,
    string LightUnitName,
    long? LightSystemId,
    long? LightUnitClassCodeId,
    long? LightUnitStatusId,
    long? OwnerId,
    string? ExtAssetId,
    string? MndotAssetNumber,
    DateTime? DateUpdate);

/// <summary>
/// A light unit a caller wants to exist, within a light system that must
/// already have been upserted -- LIGHT_UNIT_INVENTORY.LIGHT_SYSTEM_ID has no
/// NOT NULL constraint in the source schema, but a unit with no system is not
/// a usable one, so this contract requires it.
/// </summary>
public sealed record LightUnitUpsert(
    Guid ExtAssetId,
    string LightUnitName,
    long LightSystemId,
    long? LightUnitClassCodeId,
    long? LightUnitStatusId,
    long? OwnerId,
    string? MndotAssetNumber);

public sealed record LightUnitUpsertResult(
    IReadOnlyList<UpsertedLightUnit> Units,
    IReadOnlyList<UpsertRejection> Rejections)
{
    public int Created => Units.Count(u => u.Created);
    public int Updated => Units.Count(u => !u.Created);
}

public sealed record UpsertedLightUnit(long LightUnitId, Guid ExtAssetId, bool Created);

/// <summary>
/// Something MMS declined to store, and why. Shared by the system and unit
/// paths: the reason a customer system says no does not depend on what was
/// asked.
/// </summary>
/// <param name="Key">
/// The identifier that was refused, so a caller can tell which item in a batch
/// this refers to.
/// </param>
/// <param name="Transient">
/// True only for causes a later identical attempt could clear: deadlock, lock
/// timeout, connection failure. A missing light system or an absent required
/// value is a fact about the request and will never clear on retry.
/// </param>
public sealed record UpsertRejection(string Key, string Reason, bool Transient);
