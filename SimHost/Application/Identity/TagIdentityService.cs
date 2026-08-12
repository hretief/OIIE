using Microsoft.EntityFrameworkCore;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Identity;

/// <summary>
/// Stands in for the tag identity service that CIR will eventually provide.
///
/// The model it emulates has two parts, and keeping them apart is the whole point:
///
/// FederationId is the identity. It is opaque, it is minted once at the moment the
/// entity first exists — conceptual design — and it does not change through early
/// design, detailed design, construction, commissioning or operations. Machines use
/// it. Nothing about the physical thing, and nothing anyone calls it, participates in
/// its value.
///
/// Code (CodeValue) is a label for humans. It is optional, because not every entity
/// needs one, and it is plural over time and across systems: the same pump is TIC-106
/// in the design tool, LOC-000412 in the registry and 234443 in maintenance. A code
/// is how people consume the identity; it is not the identity.
///
/// Only a master mints — the design tool, or REG-LOCATION. Every other participant
/// already holds legacy data under its own codes, and its job is to register those
/// codes against the FederationId it was given, never to invent a second one. That is
/// what CIR cross-references: codes, converging on one identity.
///
/// The minted value is deliberately opaque rather than derived. An earlier version of
/// this file hashed the source and the code together, which was wrong in a way worth
/// recording: it made a re-coded tag a different entity, so the mutable label became
/// the master. Identity cannot be a function of anything that can change.
/// </summary>
/// <summary>
/// What the identity service hands back when a designer asks for a new identity:
/// the identity itself, and the code by which people will refer to it.
///
/// Returned together because a designer asking for "the next valve" wants both, and
/// obtaining them separately invites the two to disagree.
/// </summary>
public sealed record AllocatedIdentity(Guid FederationId, string Code, CodeAssignment Assignment);

public interface ITagIdentityService
{
    /// <summary>
    /// Mints a new FederationId. Only a master of identity may call this: the design
    /// tool when a tag is first drawn, or REG-LOCATION for a location it originates.
    /// A participant that received the entity from somewhere else must adopt what it
    /// was sent instead.
    /// </summary>
    Guid Mint();

    /// <summary>
    /// Allocates the next identity and code in <paramref name="prefix"/>'s series for
    /// <paramref name="participantId"/> — the greenfield case, where a designer needs
    /// a valve and asks what the next one is called.
    ///
    /// The code is allocated rather than supplied because "next available" is a claim
    /// only something holding the series can make. A designer typing P-101 into a form
    /// is guessing, and two designers guessing concurrently both guess the same.
    ///
    /// The series belongs to one twin. Two projects each running a P- series are
    /// numbering different pumps, so one must not decide where the other starts.
    /// </summary>
    Task<AllocatedIdentity> AllocateAsync(
        ParticipantDbContext db, string participantId, string prefix,
        Guid twinId = default, CancellationToken ct = default);

    /// <summary>
    /// Records that <paramref name="participantId"/> knows the entity identified by
    /// <paramref name="federationId"/> as <paramref name="code"/>.
    ///
    /// For codes that already exist — legacy data, or a designer naming something
    /// explicitly. Additive rather than replacing: a re-coded tag keeps its earlier
    /// codes, since data recorded against the old code is only findable while
    /// something still relates the two. Returns the assignment so callers can persist
    /// it.
    /// </summary>
    CodeAssignment RegisterCode(Guid federationId, string participantId, string code, Guid twinId = default);
}

/// <inheritdoc cref="ITagIdentityService"/>
public sealed class EmulatedTagIdentityService : ITagIdentityService
{
    /// <summary>
    /// Width of the allocated ordinal, so P-101 rather than P-000101. Three digits
    /// matches the sandbox's scale; a real series would take this from configuration.
    /// </summary>
    private const int SequenceWidth = 3;

    public Guid Mint() =>
        // Version 7: opaque, but time-ordered, so identities minted in sequence sort
        // in the order they were created and index without page fragmentation.
        Guid.CreateVersion7();

    public async Task<AllocatedIdentity> AllocateAsync(
        ParticipantDbContext db, string participantId, string prefix,
        Guid twinId = default, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        // The high-water mark for this series, read from what has actually been
        // issued. Deliberately not a count: codes are never deleted, but a retired
        // assignment must not free its number for reissue — the whole point of
        // retiring rather than deleting is that the old code still resolves.
        //
        // Filtered by twin as well as participant, so a second project's series starts
        // at 001 rather than continuing the first project's numbering.
        var highest = await db.Codes
            .Where(c => c.ParticipantId == participantId
                && c.ITwinId == twinId
                && c.CodePrefix == prefix)
            .Select(c => c.CodeSequence)
            .MaxAsync(ct) ?? 0;

        var next = highest + 1;
        var code = $"{prefix}{next.ToString().PadLeft(SequenceWidth, '0')}";

        var federationId = Mint();

        var assignment = new CodeAssignment
        {
            FederationId = federationId,
            ParticipantId = participantId,
            ITwinId = twinId,
            Code = code,
            CodePrefix = prefix,
            CodeSequence = next,
            AssignedAt = DateTimeOffset.UtcNow
        };

        return new AllocatedIdentity(federationId, code, assignment);
    }

    public CodeAssignment RegisterCode(
        Guid federationId, string participantId, string code, Guid twinId = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (federationId == Guid.Empty)
        {
            // An empty identity means the caller adopted nothing and minted nothing.
            // Registering a code against it would file the entity under a
            // FederationId shared with every other such mistake.
            throw new ArgumentException(
                "Cannot register a code against an empty FederationId.", nameof(federationId));
        }

        return new CodeAssignment
        {
            FederationId = federationId,
            ParticipantId = participantId,
            ITwinId = twinId,
            Code = code.Trim(),
            AssignedAt = DateTimeOffset.UtcNow
        };
    }
}
