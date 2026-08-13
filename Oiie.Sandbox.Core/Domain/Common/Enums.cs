namespace SimHost.Domain.Common;

public enum MessageDirection { Inbound, Outbound }

public enum MessagePattern { Publication, Request, Response }

public enum ProcessingStatus { Pending, Applied, Rejected, Failed }

public enum ProvenanceAction { Created, Updated, Rejected, Ignored, Superseded }

public enum OutboxState { Pending, Building, Posted, Failed, Held }

public enum ChangeKind { Add, Change, Delete }

public enum PendingWorkState { Queued, Accepted, Rejected, Expired }

/// <summary>Provenance of a class or property definition — see spec §6.5.5.</summary>
public enum DefinitionOrigin
{
    /// <summary>Governed, shared, resolvable from the reference data library.</summary>
    Rdl,

    /// <summary>Invented locally by a participant because it needed it.</summary>
    Local,

    /// <summary>Stub created on receipt of a definition the receiver does not hold.</summary>
    Inferred
}

/// <summary>
/// Taxonomy classes are single and inherited; aspect classes are multiple and
/// orthogonal. Modelling everything as single inheritance forces a combinatorial
/// taxonomy no real library has — see spec §6.5.3.
/// </summary>
public enum ClassKind { Taxonomy, Aspect }

public enum PropertyDataType { Numeric, Character, DateTime, Boolean, Blob }

public enum PropertyRequirement { Required, Recommended, Optional }
