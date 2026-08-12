namespace SimHost.Domain.Eng;

public enum TagMaturity { WorkInProgress, Shared, Published }

public enum NamedVersionState { Draft, Validated, Published }

/// <summary>
/// A digital twin: the plant a design belongs to.
///
/// ENG is one tool serving several projects, and a tag number is only unique within
/// the plant it names. Two twins may each hold a TIC-106 and mean different
/// instruments, so the twin is what makes the number unambiguous — which is why it
/// scopes the uniqueness rules rather than sitting beside them as a label.
///
/// Held as a row rather than a bare column so a twin has somewhere to record what it
/// is. A UUID appearing in a foreign key with nothing to resolve it against would be
/// unreadable in the store browser and unverifiable on input.
/// </summary>
public class ITwin
{
    /// <summary>
    /// The twin's identity, chosen by whoever created it rather than minted here.
    /// An iTwin generally exists in a system outside this sandbox, so adopting the
    /// identifier is correct where minting a second one would not be.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>Short human key, e.g. ACME-U101.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// ENG's system of record. Deliberately unlike CCOM: columns are named for what an
/// engineering system calls them, and nothing here is a Segment, an IDInSource, or
/// a CodeType. The mapper from Tag to SyncSegments is the interoperability work,
/// and a source table already shaped like the BOD would hide the only genuinely
/// hard part of the problem (spec §5.3).
/// </summary>
public class Tag
{
    public long Id { get; set; }

    /// <summary>
    /// The twin this tag belongs to. Part of what makes <see cref="TagNumber"/>
    /// unique, rather than descriptive metadata: without it, a second project
    /// reusing a tag number would collide with the first or silently overwrite it.
    /// </summary>
    public Guid ITwinId { get; set; }

    /// <summary>
    /// The identity, minted here because ENG is the design tool and the entity comes
    /// into existence at its drawing board. Immutable for the whole lifecycle —
    /// conceptual through operations — and unrelated to TagNumber, which is only what
    /// this participant currently calls it.
    /// </summary>
    public Guid FederationId { get; set; }

    /// <summary>
    /// ENG's code for the tag, e.g. TIC-106. A label, not the identity: it may be
    /// changed, and downstream systems will know the same entity by other codes.
    /// </summary>
    public string TagNumber { get; set; } = string.Empty;

    public string? ServiceDescription { get; set; }

    /// <summary>P&amp;ID drawing reference. Provenance metadata, not the carrier.</summary>
    public string? PidReference { get; set; }

    public string? LineClass { get; set; }

    public string? DisciplineCode { get; set; }

    public string? UnitNumber { get; set; }

    /// <summary>Reference-data class key, e.g. rdl:TemperatureIndicatingController.</summary>
    public string? ClassKey { get; set; }

    /// <summary>
    /// Class-governed engineering values. Unlike TagNumber and ServiceDescription,
    /// these have no CCOM spine equivalent, so they travel as properties and are
    /// checked against the effective property set of the tag's class.
    /// </summary>
    public decimal? RangeMinimum { get; set; }

    public decimal? RangeMaximum { get; set; }

    public string? ControlAction { get; set; }

    /// <summary>Object-level ISO 19650 maturity. Publication is per tag, not per document.</summary>
    public TagMaturity Maturity { get; set; } = TagMaturity.WorkInProgress;

    public long? PublishedInVersionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The release container. Publication is anchored to a named version of the model
/// rather than to a drawing transmittal: ENG extracts tags from the model and
/// publishes them independently of any document process, so a transmittal would
/// model a workflow the tooling exists to replace (spec §7.2).
/// </summary>
public class NamedVersion
{
    public long Id { get; set; }

    /// <summary>
    /// The twin being released. A release is an act about one plant, so promoting in
    /// one twin must not gather up another's work-in-progress tags.
    /// </summary>
    public Guid ITwinId { get; set; }

    /// <summary>e.g. "Rev C — Unit 101 reroute". Travels in the BOD as the sender reference.</summary>
    public string Name { get; set; } = string.Empty;

    public NamedVersionState State { get; set; } = NamedVersionState.Draft;

    public string? Scope { get; set; }

    public string CreatedBy { get; set; } = "system";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? PublishedAt { get; set; }
}

/// <summary>
/// The kind of a design relationship, held as data rather than an enum so that the
/// inverse reading is derived rather than hard-coded: one stored edge is read as
/// "Supplies" from its source end and "Supplied By" from its sink end. Adding a
/// relationship kind is then a row, not a deployment.
/// </summary>
public class TagRelationshipType
{
    public long Id { get; set; }

    /// <summary>Stable key, e.g. eng:Supplies. Carried across the hop as the connection's type.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Reading from source to sink, e.g. "Supplies".</summary>
    public string ForwardRole { get; set; } = string.Empty;

    /// <summary>Reading from sink to source, e.g. "Supplied By".</summary>
    public string InverseRole { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A directed logical relationship asserted at design time between two tags, e.g.
/// power supply BBFQ0032 supplies pump P-101. Stored once, in the forward direction:
/// FromTagId is the source and ToTagId the sink, and the reverse reading comes from
/// the type's inverse role rather than a second row that could disagree with it.
///
/// Deliberately not a CCOM SegmentConnection, and with no notion of a network or a
/// mesh, in keeping with the rest of this model (spec §5.3). CCOM has no envelope for
/// a free-standing connection, so the publisher wraps these edges in a mesh at the
/// boundary; that container is a wire-format artefact and does not belong here.
/// </summary>
public class TagRelationship
{
    public long Id { get; set; }

    /// <summary>
    /// The twin the edge was asserted in. Strictly redundant — both endpoints are
    /// tag ids, which are already twin-specific — but carried so the edge can be
    /// filtered without joining to its ends, which is what the query filter and the
    /// relationship publication both need.
    /// </summary>
    public Guid ITwinId { get; set; }

    /// <summary>
    /// The edge's own identity, minted here because the relationship is itself an
    /// assertion ENG makes and CCOM models a connection as an entity with a lifecycle.
    /// Independent of the identities of the tags at either end.
    /// </summary>
    public Guid FederationId { get; set; }

    /// <summary>Source: the supplier in a Supplies edge.</summary>
    public long FromTagId { get; set; }

    /// <summary>Sink: the supplied in a Supplies edge.</summary>
    public long ToTagId { get; set; }

    /// <summary>References <see cref="TagRelationshipType.Key"/>.</summary>
    public string TypeKey { get; set; } = string.Empty;

    /// <summary>Sequence among sibling edges, where the design gives one. Maps to the connection's Order.</summary>
    public int? Order { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Output of the promotion gate. Blocks publication, and is the demonstrable
/// failure the release workflow needs in order to read as real.
/// </summary>
public class ValidationFinding
{
    public long Id { get; set; }

    public long NamedVersionId { get; set; }

    public string TagNumber { get; set; } = string.Empty;

    /// <summary>Unclassified, MissingRequiredProperty, ValueOutOfRange, LocalDefinition.</summary>
    public string Rule { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public DateTimeOffset RaisedAt { get; set; } = DateTimeOffset.UtcNow;
}
