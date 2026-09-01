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
    NoCode
}

/// <summary>The outcome of translating one segment.</summary>
public sealed record SegmentMappingResult(
    CreateTagRequest? Request,
    SegmentRejection Rejection,
    bool ClassWasMapped)
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

        var senderClassName = segment.Type?.IDInInfoSource;
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

        return new SegmentMappingResult(
            new CreateTagRequest(
                ItemId: _options.InboundItemId,
                ClassId: classId,
                Code: code,
                Revision: _options.InboundRevision,
                Name: name,
                ScopeId: _options.InboundScopeId,

                // The sender's identity, carried through untouched. The steward
                // decides whether to admit the location, not what it is.
                Guid: segment.UUID,

                // Explicit, and not left to the provider's default -- which is
                // currently Approved. Omitting it here would walk every inbound
                // segment straight past the gate this leg exists to feed.
                State: RegTagStates.Proposed),
            SegmentRejection.None,
            mapped);
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
}
