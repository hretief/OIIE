namespace SimHost.Application.Identity;

/// <summary>
/// A human-facing code by which one participant knows an entity.
///
/// Separate from the entity itself because the relationship is many-to-one and
/// historic: a thing may be re-coded, and both codes remain meaningful afterwards.
/// Anything recorded against the old code — a work order, a drawing reference, a
/// spreadsheet someone still keeps — is only findable while something relates the
/// two, so the old assignment is retired rather than deleted.
///
/// Held per participant because a code is only unique within the system that issued
/// it. TIC-106 at ENG and TIC-106 elsewhere are different labels that happen to
/// coincide, and merging them would assert an equivalence nobody made.
/// </summary>
public class CodeAssignment
{
    public long Id { get; set; }

    /// <summary>The identity this code refers to. Never null and never Guid.Empty.</summary>
    public Guid FederationId { get; set; }

    /// <summary>The participant that issued and uses this code.</summary>
    public string ParticipantId { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// The prefix and ordinal this code was allocated from, where it came from a
    /// series. P-101 is prefix "P-" and sequence 101.
    ///
    /// Stored rather than parsed back out of Code because parsing would have to guess
    /// where the prefix ends, and a code that merely looks like a series member —
    /// entered by hand, or migrated from elsewhere — would then silently consume a
    /// number the allocator was about to issue. Null means the code was supplied
    /// rather than allocated, which is the normal case for legacy data.
    /// </summary>
    public string? CodePrefix { get; set; }

    public int? CodeSequence { get; set; }

    /// <summary>
    /// False once the participant has re-coded the entity. The row stays so the
    /// former code still resolves; it simply stops being the one to display.
    /// </summary>
    public bool IsCurrent { get; set; } = true;

    /// <summary>
    /// True when the participant adopted the FederationId from an inbound message
    /// rather than minting it. This is the provenance of the identity itself: it
    /// records that this participant took someone else's word for what this thing is,
    /// which is a different claim from having originated it.
    /// </summary>
    public bool AdoptedFromRemote { get; set; }

    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RetiredAt { get; set; }
}
