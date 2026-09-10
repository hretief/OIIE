using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Types;
using RegLocationEngine.Infrastructure.RegLocation;

namespace RegLocationEngine.Application;

/// <summary>
/// Why a segment could not become a tag proposal.
/// </summary>
public enum SegmentRejection
{
    None = 0,

    /// <summary>No federation GUID, so the proposal would have no shared identity.</summary>
    NoFederationId,

    /// <summary>Nothing usable as a tag code.</summary>
    NoCode,

    /// <summary>
    /// The segment named a registration site REG-LOCATION holds no scope for.
    ///
    /// Distinct from the two above because it is not a defect in the message:
    /// the segment is well-formed and the site is probably real, it just arrived
    /// before the SyncSites that establishes the scope. Recoverable by ingesting
    /// the site, which is why it is not filed into a fallback scope in the
    /// meantime.
    /// </summary>
    UnknownSite
}

/// <summary>The outcome of translating one segment.</summary>
/// <param name="SiteGuid">
/// The registration site the sender named, or null when it named none. The scope
/// on <see cref="Request"/> is provisional until the service resolves this.
/// </param>
/// <param name="ClassIdentity">
/// The identity the sender put on the segment's type, carried through so the
/// class cross-reference can be checked in CIR. Not used to classify the tag --
/// that is <see cref="CreateTagRequest.ClassId"/>'s job, resolved from the key.
/// </param>
/// <param name="ClassKey">The governed key the segment was typed with, if any.</param>
public sealed record SegmentMappingResult(
    CreateTagRequest? Request,
    SegmentRejection Rejection,
    bool ClassWasMapped,
    Guid? SiteGuid = null,
    Guid? ClassIdentity = null,
    string? ClassKey = null)
{
    public bool IsMapped => Request is not null;
}

/// <summary>
/// Translates an incoming CCOM Segment into a REG-LOCATION tag proposal.
///
/// Pure and synchronous on purpose: every judgement this leg makes about what
/// arriving data means is made here, where it can be tested without a broker or
/// a database. The service around it decides what to do with the answer.
///
/// Nothing here approves anything. Every request it produces is Proposed, which
/// is the whole point of the inbound leg: arrival is not acceptance, and a
/// registry that admitted whatever was published to it would be a relay wearing
/// a registry's name.
/// </summary>
public sealed class IncomingSegmentMapper(
    IOptions<RegLocationEngineOptions> options,
    ILogger<IncomingSegmentMapper> logger)
{
    private readonly RegLocationEngineOptions _options = options.Value;

    public SegmentMappingResult Map(Segment segment)
    {
        // The federated identity is the one thing this leg cannot invent.
        //
        // Minting a GUID for an unidentified segment would look like it worked:
        // the tag would appear, a steward could approve it, and it would be
        // published under an identity ENG has never heard of -- so the same
        // location would exist twice across the federation with nothing to show
        // they were ever one thing. Refusing is the recoverable failure.
        if (segment.UUID == Guid.Empty)
        {
            return new SegmentMappingResult(null, SegmentRejection.NoFederationId, false);
        }

        // ShortName is the code a person reads off a plate, and it is what ENG
        // puts its CodeValue in -- Element.CodeValue equates to Tag.Code, which
        // is the correspondence this whole scenario rests on. IDInInfoSource is
        // the fallback because it at least identifies the thing in the sender,
        // whereas FullName is a label and may not be unique.
        var code = FirstNonBlank(segment.ShortName, segment.IDInInfoSource);
        if (code is null)
        {
            return new SegmentMappingResult(null, SegmentRejection.NoCode, false);
        }

        // The human-readable name, falling back to the code so the column is
        // never empty. A steward choosing what to admit needs something to read.
        var name = FirstNonBlank(segment.FullName, segment.Description, segment.ShortName) ?? code;

        // ShortName first, IDInInfoSource second. The governed RDL key travels
        // in ShortName; IDInInfoSource holds the sender's own id for its class
        // -- ENG puts its ECClassId there -- which is meaningless to this map.
        // The fallback is retained because a publisher that has not moved over
        // still puts its key in IDInInfoSource, and reading only ShortName would
        // silently file all of its segments under the fallback class.
        var senderClassName = FirstNonBlank(segment.Type?.ShortName, segment.Type?.IDInInfoSource);
        var classId = _options.ResolveClassId(senderClassName, out var mapped);

        if (!mapped)
        {
            logger.LogWarning(
                "Segment {Code} carried class '{ClassName}', which is not in InboundClassMap; " +
                "filed under fallback class {ClassId}.",
                code,
                senderClassName ?? "(none)",
                classId);
        }

        // The iTwin the sender registered this location against. REG-LOCATION
        // scopes by site and has no concept of an iModel -- it takes locations
        // from every model under a twin equally, and which model produced this
        // one is recorded in InfoSource for whoever needs to go back and ask.
        //
        // Only the identity is read. Resolving it to a scope means asking
        // REG-LOCATION, which this mapper stays out of so its judgements remain
        // testable without a database; the service does that with the guid.
        var siteGuid = segment.RegistrationSite?.UUID;

        return new SegmentMappingResult(
            new CreateTagRequest(
                ItemId: _options.InboundItemId,
                ClassId: classId,
                Code: code,
                Revision: _options.InboundRevision,
                Name: name,

                // Provisional. The sender's site decides the real scope, and the
                // service overwrites this once it has resolved one; this value
                // stands only for a segment that named no site at all.
                ScopeId: _options.InboundScopeId,

                // The sender's identity, carried through untouched. The steward
                // decides whether to admit the location, not what it is.
                Guid: segment.UUID,

                // Explicit, and not left to the provider's default -- which is
                // currently Approved. Omitting it here would walk every inbound
                // segment straight past the gate this leg exists to feed.
                State: RegTagStates.Proposed),
            SegmentRejection.None,
            mapped,
            siteGuid,

            // The sender's answer to "which RDL class is this", passed on for
            // the registrar to verify. Deliberately not consulted when choosing
            // ClassId above: this leg classifies from the governed key, and
            // taking the id from a UUID the sender may have derived would let a
            // publisher's fallback decide what a location is.
            segment.Type?.UUID,
            senderClassName);
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
}
