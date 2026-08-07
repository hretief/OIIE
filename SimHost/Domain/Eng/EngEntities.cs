namespace SimHost.Domain.Eng;

public enum TagMaturity { WorkInProgress, Shared, Published }

public enum NamedVersionState { Draft, Validated, Published }

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

    /// <summary>ISA-5.1 instrument or equipment tag, e.g. TIC-106.</summary>
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

    /// <summary>e.g. "Rev C — Unit 101 reroute". Travels in the BOD as the sender reference.</summary>
    public string Name { get; set; } = string.Empty;

    public NamedVersionState State { get; set; } = NamedVersionState.Draft;

    public string? Scope { get; set; }

    public string CreatedBy { get; set; } = "system";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? PublishedAt { get; set; }
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
