using Microsoft.EntityFrameworkCore;
using SimHost.Domain.Eng;
using SimHost.Domain.Mms;
using SimHost.Domain.RegLocation;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Scenarios;

/// <summary>
/// What one participant calls a thing, and how it came to call it that.
/// </summary>
/// <param name="ParticipantId">Owning participant, e.g. eng.</param>
/// <param name="Code">The participant's own code, e.g. P-001 or LOC-000001.</param>
/// <param name="Label">Human-facing description, where the participant holds one.</param>
/// <param name="Minted">
/// True when this participant originated the identity rather than adopting it. At most
/// one participant per identity should be the minter; more than one is a defect.
/// </param>
public sealed record LineageCode(
    string ParticipantId,
    string Code,
    string? Label,
    bool Minted);

/// <summary>
/// One real-world thing, as seen by every participant that holds a code for it.
/// </summary>
/// <param name="FederationId">The shared identity. This is the thing itself.</param>
/// <param name="Codes">One entry per participant, ordered by the flow ENG to MMS.</param>
public sealed record IdentityLineage(Guid FederationId, IReadOnlyList<LineageCode> Codes)
{
    /// <summary>
    /// True when more than one participant claims to have minted this identity, which
    /// means two masters both believe they originated the same thing.
    /// </summary>
    public bool HasCompetingMinters => Codes.Count(c => c.Minted) > 1;

    /// <summary>
    /// True when nothing claims to have minted it. The identity is in use but has no
    /// origin, so nobody owns the decision of what it means.
    /// </summary>
    public bool HasNoMinter => !Codes.Any(c => c.Minted);
}

/// <summary>
/// Assembles the "one identity, many codes" view across participant schemas.
///
/// This has to be a fan-out rather than a query. Each participant owns a separate SQL
/// schema reached through its own context, and there is deliberately no foreign key
/// between them — the whole premise is that these are independent systems that agree
/// on an identity and on nothing else. Joining them in the database would model a
/// coupling that does not exist in the thing being simulated.
///
/// Grouping is on FederationId alone, never on a code. Two participants holding the
/// same code string is a coincidence; holding the same FederationId is the claim under
/// test, and the view exists to show whether that claim survived the trip.
/// </summary>
public sealed class IdentityLineageService(
    IParticipantDbContextFactory factory,
    ILogger<IdentityLineageService> logger)
{
    /// <summary>
    /// Participants in flow order, so a lineage row reads left to right as the thing
    /// actually travelled rather than in whatever order the registry enumerates.
    /// </summary>
    private static readonly string[] FlowOrder = ["eng", "reg-location", "mms"];

    public async Task<IReadOnlyList<IdentityLineage>> GetAsync(CancellationToken ct = default)
    {
        var byIdentity = new Dictionary<Guid, List<LineageCode>>();

        foreach (var participantId in FlowOrder)
        {
            try
            {
                await ReadAsync(participantId, byIdentity, ct);
            }
            catch (Exception ex)
            {
                // A participant whose schema is missing or mid-reset must not blank the
                // whole view. The remaining participants still tell a true story, and an
                // empty page would wrongly suggest nothing had been allocated at all.
                logger.LogWarning(
                    ex, "Lineage skipped {ParticipantId}: its store could not be read.",
                    participantId);
            }
        }

        return byIdentity
            .Select(pair => new IdentityLineage(
                pair.Key,
                [.. pair.Value.OrderBy(c => Array.IndexOf(FlowOrder, c.ParticipantId))]))

            // Newest first. Guid v7 is time-ordered, so this puts the most recently
            // allocated identity at the top without needing a timestamp column.
            .OrderByDescending(l => l.FederationId)
            .ToList();
    }

    private async Task ReadAsync(
        string participantId,
        Dictionary<Guid, List<LineageCode>> byIdentity,
        CancellationToken ct)
    {
        await using var db = factory.Create(participantId);

        // Minted is true only for ENG. REG-LOCATION issues its own LocationCode and MMS
        // holds its own EquipmentNumber, but issuing a code is not minting an identity —
        // conflating the two is precisely the error this view exists to expose.
        var rows = participantId switch
        {
            "eng" => await db.Set<Tag>()
                .AsNoTracking()
                .Select(t => new Row(t.FederationId, t.TagNumber, t.ServiceDescription, true))
                .ToListAsync(ct),

            "reg-location" => await db.Set<Location>()
                .AsNoTracking()
                .Select(l => new Row(l.FederationId, l.LocationCode, l.Description, false))
                .ToListAsync(ct),

            "mms" => await db.Set<FunctionalLocationRecord>()
                .AsNoTracking()
                .Select(f => new Row(f.FederationId, f.EquipmentNumber, f.Designation, false))
                .ToListAsync(ct),

            _ => []
        };

        foreach (var row in rows)
        {
            var code = new LineageCode(participantId, row.Code, row.Label, row.Minted);

            // The empty GUID is not an identity, it is the absence of one. Grouping on it
            // would collect every unidentified row across every participant into a single
            // fictitious "thing" that no part of the system believes in.
            if (row.FederationId == Guid.Empty)
            {
                continue;
            }

            if (!byIdentity.TryGetValue(row.FederationId, out var list))
            {
                byIdentity[row.FederationId] = list = [];
            }

            list.Add(code);
        }
    }

    /// <summary>Shared projection shape, so each participant's query differs only in source.</summary>
    private sealed record Row(Guid FederationId, string Code, string? Label, bool Minted);
}
