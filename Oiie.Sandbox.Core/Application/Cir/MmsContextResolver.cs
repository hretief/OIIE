using Microsoft.EntityFrameworkCore;
using SimHost.Application.Participants;
using SimHost.Domain.Mms;
using SimHost.Infrastructure.Sql;
using SimHost.Personalities.Mms;

namespace SimHost.Application.Cir;

/// <summary>
/// Answers "which OWNER_ID corresponds to this iTwin?" for MMS read paths.
///
/// MMS has no iTwin column and cannot be given one: dbo.SETUP_OWNER is the
/// customer's table and OWNER_ID is its only context key. So the join between the
/// two key spaces cannot live in either schema, and lives in ws-CIR instead.
///
/// This differs from <see cref="CmsContextResolver"/> in one important way. CMS
/// owns its ContextOwnerRecord table and can cache a CIRID on the row; MMS cannot
/// store one anywhere, so the mapping has to be read back out of the registry's
/// equivalence set on every resolution. That is the direct consequence of the
/// no-new-columns constraint rather than an oversight.
/// </summary>
public sealed class MmsContextResolver(
    CirClient cir,
    ParticipantRegistry registry,
    IParticipantDbContextFactory factory)
{
    /// <summary>
    /// The MMS OWNER_ID related to an iTwin identifier, or an explanation of why
    /// no relation could be established.
    ///
    /// An unresolved context is never reported as "no filter". Returning every row
    /// because a twin could not be resolved would show one district's inventory to
    /// another, which is worse than showing nothing and saying so.
    /// </summary>
    public async Task<MmsContextResolution> ResolveOwnerIdAsync(
        string twinId,
        CancellationToken ct = default)
    {
        var participant = registry.Get(MmsService.ParticipantId);

        // Ask the registry what shared identity the twin has, and what else is
        // known under it. The equivalents are the answer: MMS keeps no CIRID of
        // its own, so the registry's view is the only place the link exists.
        var resolution = await cir.ResolveAsync(
            participant, EngSourceId, twinId, ct);

        if (resolution.Cirid is not { } cirid)
        {
            var detail = resolution.Detail ?? "The registry knows no shared identity for that iTwin.";

            return resolution.Transient
                ? MmsContextResolution.Unreachable(detail)
                : MmsContextResolution.Unresolved(detail);
        }

        // A CIRID with an unfetched equivalence set is not evidence of anything: the
        // owner is read out of that set, so an empty one here would be reported as
        // "no MMS owner related" when the truth is that nobody answered.
        if (resolution.Transient)
        {
            return MmsContextResolution.Unreachable(
                resolution.Detail ?? "The registry did not respond.");
        }

        // Among the equivalents, take the entry MMS itself registered. Its
        // IDInSource is an OWNER_ID, because that is what SyncMmsAsync registers
        // under the ContextOwner category.
        var ownerEntry = resolution.Equivalents.FirstOrDefault(e =>
            string.Equals(e.SourceID, participant.Config.SourceId, StringComparison.OrdinalIgnoreCase));

        if (ownerEntry is null)
        {
            return MmsContextResolution.Unresolved(
                $"The iTwin resolves to {cirid}, but no MMS owner has been related to it yet.");
        }

        if (!long.TryParse(ownerEntry.IDInSource, out var ownerId))
        {
            return MmsContextResolution.Unresolved(
                $"The registry returned '{ownerEntry.IDInSource}' as the MMS owner, which is not an OWNER_ID.");
        }

        // Confirm the owner actually exists locally. The registry can outlive a
        // row it once knew about, and silently filtering on a stale OWNER_ID would
        // return an empty inventory that looks identical to a district with no
        // assets.
        await using var db = factory.Create(participant.ParticipantId);

        var owner = await db.Set<SetupOwner>()
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OwnerId == ownerId, ct);

        if (owner is null)
        {
            return MmsContextResolution.Unresolved(
                $"The registry relates that iTwin to OWNER_ID {ownerId}, which no longer exists in SETUP_OWNER.");
        }

        return MmsContextResolution.Resolved(ownerId, owner.OwnerName, cirid);
    }

    /// <summary>
    /// The source that owns iTwin GUIDs. Resolving without naming it would let one
    /// participant's key accidentally match another's.
    /// </summary>
    private const string EngSourceId = "ENG";
}

/// <summary>
/// The outcome of resolving an iTwin to an MMS owner.
///
/// Modelled as a result rather than a nullable long so the reason for failure
/// survives to the caller: "not related yet" and "related to a deleted owner" need
/// different responses from a steward, and a null cannot tell them apart.
/// </summary>
/// <summary>
/// The outcome of resolving an iTwin to an MMS owner.
///
/// Modelled as a result rather than a nullable long so the reason for failure
/// survives to the caller: "not related yet" and "related to a deleted owner" need
/// different responses from a steward, and a null cannot tell them apart.
///
/// <paramref name="Transient"/> adds the third case the first two were being
/// confused with: the registry could not be reached at all. That is not a statement
/// about the relation and calls for a retry rather than a steward.
/// </summary>
public sealed record MmsContextResolution(
    bool IsResolved,
    long? OwnerId,
    string? OwnerName,
    Guid? Cirid,
    string? Reason,
    bool Transient = false)
{
    public static MmsContextResolution Resolved(long ownerId, string ownerName, Guid cirid)
        => new(true, ownerId, ownerName, cirid, null);

    public static MmsContextResolution Unresolved(string reason)
        => new(false, null, null, null, reason);

    /// <summary>The registry did not answer; nothing was learned about the relation.</summary>
    public static MmsContextResolution Unreachable(string reason)
        => new(false, null, null, null, reason, Transient: true);
}
