namespace SimHost.Domain.Common;

/// <summary>
/// Transactional outbox. Publication intent commits in the same transaction as
/// the domain change, so a BOD is always derived from persisted state and never
/// authored directly by a form (spec §6.3, §7.1).
/// </summary>
public class OutboxItem
{
    public long Id { get; set; }

    /// <summary>NamedVersion, WorkPackage, Ecn, Requisition...</summary>
    public string? ContainerType { get; set; }
    public string? ContainerKey { get; set; }

    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// The twin whose data this publication describes, where the participant scopes
    /// its data that way. Carried on the item because the BOD is built later, by a
    /// background dispatcher that has no other way to know which plant the entity
    /// keys below belong to — and two twins may use the same key.
    /// </summary>
    public Guid ITwinId { get; set; }

    /// <summary>JSON array — a release event typically publishes many nouns in one BOD.</summary>
    public string EntityKeys { get; set; } = "[]";

    public ChangeKind ChangeKind { get; set; }

    public string Verb { get; set; } = string.Empty;
    public string Noun { get; set; } = string.Empty;
    public MessagePattern Pattern { get; set; } = MessagePattern.Publication;

    public string ChannelUri { get; set; } = string.Empty;
    public string? Topic { get; set; }

    public Guid? ScenarioRunId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;

    public OutboxState State { get; set; } = OutboxState.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public Guid? MessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PostedAt { get; set; }
}

/// <summary>
/// Local cache of CIR identity resolution. The cache is a feature, not an
/// optimisation — it is what makes stale-mapping correction demonstrable
/// (spec §9.3, §9.6 M4).
/// </summary>
public class IdentityMapEntry
{
    public long Id { get; set; }

    public string? LocalEntityType { get; set; }
    public string? LocalKey { get; set; }

    public Guid? Cirid { get; set; }

    public string ForeignSourceId { get; set; } = string.Empty;
    public string ForeignIdInSource { get; set; } = string.Empty;
    public string? ForeignName { get; set; }

    public DateTimeOffset ResolvedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StaleAfter { get; set; }

    public bool Invalidated { get; set; }
    public string? InvalidatedReason { get; set; }

    public bool IsLive(DateTimeOffset now) => !Invalidated && StaleAfter > now;
}

/// <summary>
/// Human-in-the-loop queue. Survives a page refresh, which in-memory queue state
/// would not — and the accept/reject decision is what emits the acknowledgement
/// BOD (spec §7.2 REG-ASSET).
/// </summary>
public class PendingWorkItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? MessageId { get; set; }

    public string Kind { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;

    /// <summary>JSON snapshot of the proposed change.</summary>
    public string? Payload { get; set; }

    public PendingWorkState State { get; set; } = PendingWorkState.Queued;

    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? RejectReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
