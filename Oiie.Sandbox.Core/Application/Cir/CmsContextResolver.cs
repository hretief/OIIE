using Microsoft.EntityFrameworkCore;
using SimHost.Application.Participants;
using SimHost.Domain.Cms;
using SimHost.Infrastructure.Sql;
using SimHost.Personalities.Cms;

namespace SimHost.Application.Cir;

/// <summary>
/// Answers "which CMS owner code corresponds to this foreign context?" for read paths.
///
/// A caller holding an iTwin GUID cannot query CMS directly — CMS has no such column
/// and never will. This resolves the GUID through the registry to a CIRID, then to
/// the CMS owner that shares it, which is the only legitimate route between the two
/// key spaces.
///
/// Resolution happens on read rather than being baked into rows on ingest. A CIRID
/// stamped at ingest is a snapshot of what the registry said at that moment, and a
/// later equivalence correction would silently leave it wrong; resolving on read
/// means a correction takes effect immediately. The identity map inside
/// <see cref="CirClient"/> keeps the cost of that to one round trip per cache TTL.
/// </summary>
public sealed class CmsContextResolver(
    CirClient cir,
    ParticipantRegistry registry,
    IParticipantDbContextFactory factory)
{
    /// <summary>
    /// The CMS owner code related to a foreign context identifier, or null when the
    /// registry knows of no such relation.
    ///
    /// Null is a meaningful answer and callers must not treat it as "no filter":
    /// showing every record because a context could not be resolved would present
    /// another district's assets as though they belonged to the one asked for.
    /// </summary>
    public async Task<string?> ResolveOwnerCodeAsync(
        string foreignIdInSource,
        CancellationToken ct = default)
    {
        var participant = registry.Get(CmsService.ParticipantId);

        await using var db = factory.Create(participant.ParticipantId);

        // Try the local relation first. Once a steward has related an owner, its CIRID
        // is on the row and no registry call is needed at all.
        var known = await db.Set<ContextOwnerRecord>()
            .AsNoTracking()
            .Where(o => o.Cirid != null)
            .Select(o => new { o.OwnerCode, o.Cirid })
            .ToListAsync(ct);

        if (known.Count == 0)
        {
            return null;
        }

        // The source of a twin identifier is ENG. Resolving without naming a source
        // would ask the registry to match an identifier across every participant,
        // which is how one system's key accidentally matches another's.
        var resolution = await cir.ResolveAsync(
            participant, EngSourceId, foreignIdInSource, ct);

        if (resolution.Cirid is not { } cirid)
        {
            return null;
        }

        return known.FirstOrDefault(o => o.Cirid == cirid)?.OwnerCode;
    }

    /// <summary>
    /// The source a twin identifier belongs to. ENG is the participant that owns
    /// iTwin GUIDs; no other participant may assert one.
    /// </summary>
    private const string EngSourceId = "ENG";
}
