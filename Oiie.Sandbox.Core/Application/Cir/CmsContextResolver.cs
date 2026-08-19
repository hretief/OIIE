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
    /// The CMS site codes related to a foreign context identifier, or an empty list
    /// when the registry knows of no such relation.
    ///
    /// CMS does hold the publisher's site UUID, and filtering on it directly would
    /// work — which is exactly why it is not done. Matching an iTwin GUID against a
    /// CMS column teaches a condition monitoring system to speak iTwin, and the
    /// interoperability claim is that it does not have to. The UUID is retained
    /// because it is the publisher's data and CMS was told it; it is not retained as
    /// a join key for foreign systems to query on.
    ///
    /// So the route is the same one every other participant uses: resolve the twin to
    /// a CIRID through the registry, then find the CMS entries sharing it. A site
    /// becomes filterable only once it has been related through RelateCmsSiteAsync,
    /// which is the point — the relation is an assertion someone made, not a
    /// coincidence of two columns holding equal bytes.
    ///
    /// Returns every matching code rather than one. Nothing forbids two CMS sites
    /// being related to the same context, and silently taking the first would hide
    /// half the assets.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveSiteCodesAsync(
        string foreignIdInSource,
        CancellationToken ct = default)
    {
        var participant = registry.Get(CmsService.ParticipantId);

        var resolution = await cir.ResolveAsync(
            participant, EngSourceId, foreignIdInSource, ct);

        if (resolution.Cirid is null)
        {
            return [];
        }

        // The equivalents are entries from other sources that share the CIRID. Only
        // CMS's own are of interest: another participant's code for the same plant is
        // meaningless as a filter against a CMS column.
        return resolution.Equivalents
            .Where(e => string.Equals(
                e.SourceID, participant.Config.SourceId, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.IDInSource)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The source a twin identifier belongs to. ENG is the participant that owns
    /// iTwin GUIDs; no other participant may assert one.
    /// </summary>
    private const string EngSourceId = "ENG";
}
