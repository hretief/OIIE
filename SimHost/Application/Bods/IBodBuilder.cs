using System.Xml.Linq;
using Oiie.Ccom.Oagis;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Bods;

/// <summary>
/// Builds a BOD from currently persisted state. Returns a typed document rather
/// than a string, so the ApplicationArea, action code and noun set are constructed
/// through the model instead of by assembling XML.
///
/// The BOD is always derived from committed rows. A form never authors one —
/// otherwise the tool is a message generator with a UI, and stops demonstrating
/// that a system of record and an integration layer are separate things.
/// </summary>
public interface IBodBuilder
{
    (string Verb, string Noun) Handles { get; }

    /// <summary>
    /// Participant this builder serves, or null for any.
    ///
    /// Verb and noun alone are not enough to select one: ENG and REG-LOCATION both
    /// emit Sync/Segments and build them from entirely different tables. Matching on
    /// verb and noun only would silently pick whichever registered first.
    /// </summary>
    string? ParticipantId => null;

    Task<XDocument> BuildAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        OutboxItem item,
        CancellationToken ct);
}

/// <summary>
/// Handles an inbound BOD. Registered per verb+noun; a participant receiving a BOD
/// with no registered handler still archives it and shows it in the wire view.
/// </summary>
public interface IBodHandler
{
    (string Verb, string Noun) Handles { get; }

    /// <summary>Participant this handler serves, or null for any.</summary>
    string? ParticipantId => null;

    Task<BodHandlingResult> HandleAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        BodEnvelope envelope,
        Guid messageId,
        CancellationToken ct);
}

public sealed record BodHandlingResult(
    ProcessingStatus Status,
    string? Detail = null,
    int EntitiesAffected = 0,
    int PropertiesMapped = 0,
    int PropertiesUnmapped = 0)
{
    public static BodHandlingResult Applied(int entities, int mapped, int unmapped) =>
        new(ProcessingStatus.Applied, null, entities, mapped, unmapped);

    public static BodHandlingResult Rejected(string reason) =>
        new(ProcessingStatus.Rejected, reason);

    public static BodHandlingResult NoHandler(string verb, string noun) =>
        new(ProcessingStatus.Pending, $"No handler registered for {verb}{noun}.");
}
