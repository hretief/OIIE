namespace SimHost.Domain.Common;

/// <summary>Every BOD in or out, with its ISBM envelope metadata (spec §6.3).</summary>
public class MessageRecord
{
    public Guid MessageId { get; set; } = Guid.NewGuid();

    public MessageDirection Direction { get; set; }
    public MessagePattern Pattern { get; set; }

    public string ChannelUri { get; set; } = string.Empty;
    public string? Topic { get; set; }

    public string Verb { get; set; } = string.Empty;
    public string Noun { get; set; } = string.Empty;

    public string BodId { get; set; } = string.Empty;
    public string? CorrelationBodId { get; set; }

    public string? IsbmMessageId { get; set; }
    public string? IsbmSessionId { get; set; }
    public string? IsbmRequestId { get; set; }

    public Guid? ScenarioRunId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;

    public string ContentRef { get; set; } = string.Empty;
    public int ContentBytes { get; set; }

    public string ValidationStatus { get; set; } = nameof(Oiie.Ccom.BodValidationStatus.NotValidated);
    public string? ValidationDetail { get; set; }

    public ProcessingStatus ProcessingStatus { get; set; } = ProcessingStatus.Pending;
    public string? ProcessingDetail { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Append-only ledger linking domain changes to messages. Deliberately not a
/// CreatedByMessageId column on domain rows: one BOD touches many rows, and one
/// row is touched by many BODs across a scenario (spec §6.3).
/// </summary>
public class ProvenanceEntry
{
    public long Id { get; set; }

    public Guid? MessageId { get; set; }

    public string EntityType { get; set; } = string.Empty;
    public string EntityKey { get; set; } = string.Empty;

    public ProvenanceAction Action { get; set; }

    /// <summary>User id, "system", or a scenario step id.</summary>
    public string Actor { get; set; } = "system";

    /// <summary>JSON, field-level before/after.</summary>
    public string? ChangeSummary { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One CIR request/response round trip, kept verbatim.
///
/// Deliberately a table rather than a field on <see cref="MessageRecord"/>: these
/// exchanges are the evidence handed to the CIR provider's owner when a request is
/// consumed without an answer, and they have to outlive the process that made them.
/// An in-memory copy is lost to an App Service recycle between the registration and
/// the moment anyone asks what was sent — which is precisely when it is wanted.
/// </summary>
public class CirExchangeRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ParticipantId { get; set; } = string.Empty;

    /// <summary>Root element of the request, e.g. ProcessRegistry.</summary>
    public string Bod { get; set; } = string.Empty;

    /// <summary>Travels in ApplicationArea/BODID, so both sides can search on it.</summary>
    public string CorrelationId { get; set; } = string.Empty;

    public string ChannelUri { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;

    public string RequestXml { get; set; } = string.Empty;

    public string? RequestMessageId { get; set; }

    /// <summary>
    /// The consumer session the request was posted on, and the one the response is
    /// awaited on. If a response is written but never readable, this and the
    /// provider's session id are what the two sides need to compare.
    /// </summary>
    public string? ConsumerSessionId { get; set; }

    /// <summary>How long the response was waited for before giving up.</summary>
    public int? WaitedSeconds { get; set; }

    public string? ResponseXml { get; set; }
    public string? ResponseVerb { get; set; }

    /// <summary>JSON array. Faults arrive inside a well-formed acknowledgement.</summary>
    public string? FaultsJson { get; set; }

    /// <summary>Answered, Faulted or NoResponse.</summary>
    public string? Outcome { get; set; }

    public DateTimeOffset SentUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AnsweredUtc { get; set; }
}
