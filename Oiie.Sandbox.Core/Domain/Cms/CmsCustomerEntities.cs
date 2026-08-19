namespace SimHost.Domain.Cms;

/// <summary>
/// <c>cms.Site</c> — the plant a monitored asset belongs to, populated from the
/// <c>RegistrationSite</c> of an inbound segment.
///
/// This table is unusual among the customer tables, and the difference matters:
/// <c>SiteUUID</c> is a real identity column, so CMS can retain the value the
/// publisher sent rather than discarding it. Everywhere else in this schema — most
/// visibly <see cref="CmsAsset"/>, and in MMS's LIGHT_SYSTEM_INVENTORY — there is no
/// column for a foreign identifier and the correspondence has to live outside in
/// CodeAssignment and ws-CIR.
///
/// Retaining it is not the same as querying on it. The UUID is held because it is
/// the publisher's data, not so foreign systems can join against it: scoping CMS
/// assets by twin goes through the registry by CIRID, exactly as it does for a
/// participant with no such column. A column that happens to make the shortcut
/// possible is not a licence to take it.
/// </summary>
public class CmsSite
{
    public int SiteId { get; set; }

    /// <summary>
    /// The iTwin GUID, taken straight from <c>RegistrationSite.UUID</c> rather than
    /// minted. ENG publishes the twin's own identity here precisely so downstream
    /// systems can recognise the plant; allocating a fresh GUID would discard the
    /// only thing that makes the site recognisable to anyone else.
    /// </summary>
    public Guid SiteUuid { get; set; }

    /// <summary>The twin code, e.g. the plant identifier a person would recognise.</summary>
    public string SiteCode { get; set; } = string.Empty;

    public string SiteName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The CMS customer schema — the tables defined by <c>docs/DDL/CMS.SQL</c> rather
/// than by the sandbox.
///
/// These are named in UPPER_CASE, and that is the convention that separates them
/// from everything else in the schema. A CMS schema holds two quite different kinds
/// of table: the participant spine the runtime gives every participant (Message,
/// Outbox, Provenance, IdentityMap, CodeAssignment …) and the customer's own tables.
/// The spine is the sandbox's to change; these are not. Casing makes that ownership
/// visible at every call site, so nobody has to remember which is which.
///
/// Only the LOCATION AND ASSET block is modelled, and within it only the asset side.
/// <c>cms.Location</c> is deliberately absent: it is CMS's own plant-structure
/// hierarchy and has nothing to do with the functional locations arriving from
/// REG-LOCATION, so mapping a segment onto it would assert a correspondence that
/// does not exist. Sensor, MeasurementPoint and the monitoring tables are out of
/// scope for this step.
/// </summary>
public class CmsAsset
{
    /// <summary>
    /// <c>AssetID</c>. IDENTITY in the DDL, so the database allocates it and the
    /// sandbox never supplies one — unlike MMS, where LIGHT_SYSTEM_ID had to be
    /// allocated by hand because that column is not an identity.
    /// </summary>
    public int AssetId { get; set; }

    /// <summary>
    /// <c>SiteID</c>. NOT NULL in the DDL, so an asset cannot exist without a site —
    /// which means a segment whose RegistrationSite is missing or unresolvable cannot
    /// be stored at all. That is a real constraint rather than an inconvenience: CMS
    /// is asserting that it does not monitor things it cannot place at a plant.
    /// </summary>
    public int SiteId { get; set; }

    /// <summary>
    /// <c>ParentAssetID</c>. Self-referencing in the DDL. Nothing sets it yet: a
    /// segment arrives as a standalone placeholder, and assembling a parent/child
    /// breakdown is work CONSTRUCT and REG-ASSET inform later.
    /// </summary>
    public int? ParentAssetId { get; set; }

    /// <summary>
    /// <c>AssetClassID</c>. Nullable in the DDL and left null on arrival, because the
    /// sender's classification is not CMS's classification. Guessing one would assert
    /// a taxonomy decision nobody made; a planner sets it once the asset is surveyed.
    /// </summary>
    public int? AssetClassId { get; set; }

    /// <summary>
    /// <c>AssetTag</c> — UNIQUE in the DDL, and therefore the only thing CMS can match
    /// a re-received segment on.
    ///
    /// This is the same constraint MMS lives under and for the same reason: the
    /// asset table has no column for a foreign identifier, a FederationId or a
    /// CIRID, and none may be added. So the sender's identity is not retained on this
    /// row — it survives in CodeAssignment, provenance and ws-CIR, and indirectly
    /// through the site, which does have a UUID. Matching by
    /// tag is weaker than matching by key and will fail if the sender renames
    /// something, which is the honest consequence of the constraint rather than a
    /// defect to paper over.
    /// </summary>
    public string AssetTag { get; set; } = string.Empty;

    public string AssetName { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// Null on arrival. A functional location has no serial number — the physical
    /// unit that will carry one has not been fitted yet, and CONSTRUCT supplies it
    /// through REG-ASSET when it is.
    /// </summary>
    public string? SerialNumber { get; set; }

    public string? Manufacturer { get; set; }

    public string? Model { get; set; }

    public DateTime? CommissionDate { get; set; }

    /// <summary>
    /// <c>OperationalStatus</c>. Set to a placeholder marker on arrival so the row is
    /// visibly incomplete rather than looking like a commissioned asset that happens
    /// to be missing its details.
    /// </summary>
    public string? OperationalStatus { get; set; }

    public string? CriticalityLevel { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// <c>cms.AssetClass</c>. Customer reference data: the sandbox reads it and never
/// writes it, exactly as it treats MMS's LIGHT_SYSTEM_CLASS_CODE.
/// </summary>
public class CmsAssetClass
{
    public int AssetClassId { get; set; }

    public string ClassCode { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    public string? Description { get; set; }
}
