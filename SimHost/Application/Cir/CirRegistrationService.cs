using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Cir;
using SimHost.Application.Participants;
using SimHost.Domain.Eng;
using SimHost.Domain.Mms;
using SimHost.Domain.RegLocation;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Cir;

public sealed record CirSyncResult(
    string ParticipantId,
    int Registered,
    int EquivalencesAsserted,
    IReadOnlyList<string> Faults,
    string? Note = null)
{
    /// <summary>
    /// Nothing sent and nothing wrong are different outcomes, and zero counts with an
    /// empty fault list reads like the first when it may be the second. The note says
    /// which.
    /// </summary>
    public static CirSyncResult NothingToDo(string participantId, string reason) =>
        new(participantId, 0, 0, [], reason);
}

/// <summary>
/// Builds CIR entries from each participant's own tables and registers them.
///
/// Registration is per participant because only the owner knows what it owns and
/// what it calls it. What travels is the identity — SourceID plus IDInSource — and a
/// handful of discriminating properties, not the participant's data. The ws-CIR
/// Property set is a linking aid, and treating it as a property master would make
/// the registry a second copy of everything.
/// </summary>
public sealed class CirRegistrationService(
    CirClient cir,
    ParticipantRegistry registry,
    IParticipantDbContextFactory factory,
    ILogger<CirRegistrationService> logger)
{
    /// <summary>
    /// Category for functional locations. Shared across participants deliberately:
    /// entries only become comparable if they are filed under the same category.
    /// </summary>
    private const string SegmentCategory = "Segment";

    public async Task<CirSyncResult> SyncAsync(string participantId, CancellationToken ct = default)
    {
        var participant = registry.Get(participantId);

        return participantId switch
        {
            "eng" => await SyncEngAsync(participant, ct),
            "reg-location" => await SyncRegLocationAsync(participant, ct),
            "mms" => await SyncMmsAsync(participant, ct),
            _ => new CirSyncResult(participantId, 0, 0, ["No registration mapping for this participant."])
        };
    }

    /// <summary>
    /// ENG registers what it originated. Nothing here asserts equivalence: ENG has
    /// only ever seen its own identifiers, so it has nothing to compare them to.
    /// </summary>
    private async Task<CirSyncResult> SyncEngAsync(ParticipantContext participant, CancellationToken ct)
    {
        await using var db = factory.Create(participant.ParticipantId);

        var tags = await db.Set<Tag>()
            .Where(t => t.Maturity == TagMaturity.Published)
            .ToListAsync(ct);

        if (tags.Count == 0)
        {
            return CirSyncResult.NothingToDo(participant.ParticipantId,
                "No published tags. Add a tag and promote a named version first — nothing was sent.");
        }

        var entries = tags.Select(tag =>
        {
            var entry = new Entry
            {
                IDInSource = tag.TagNumber,
                SourceID = participant.Config.SourceId,
                SourceOwnerID = participant.Config.SourceOwnerId,
                Name = tag.ServiceDescription ?? tag.TagNumber
            };

            // Discriminating values only — enough for a steward to judge whether two
            // entries are the same thing, not a copy of the tag.
            if (tag.ClassKey is { Length: > 0 })
            {
                entry.Property.Add(Property.Simple("ClassKey", tag.ClassKey));
            }

            if (tag.UnitNumber is { Length: > 0 })
            {
                entry.Property.Add(Property.Simple("UnitNumber", tag.UnitNumber));
            }

            return entry;
        }).ToList();

        var result = await cir.RegisterAsync(participant, SegmentCategory, entries, ct);

        return new CirSyncResult(
            participant.ParticipantId,
            result.Succeeded ? entries.Count : 0,
            0,
            result.Faults.Select(f => $"{f.Kind}: {f.Detail}").ToList());
    }

    /// <summary>
    /// REG-LOCATION is the only participant that can link the chain.
    ///
    /// It received ENG:TIC-106 and issued LOC-000001, so it alone knows they denote
    /// the same pump. Registering LOC-000001 on its own would create a second
    /// identity — precisely the duplicate the registry exists to prevent — so it
    /// asserts equivalence instead.
    /// </summary>
    private async Task<CirSyncResult> SyncRegLocationAsync(
        ParticipantContext participant, CancellationToken ct)
    {
        await using var db = factory.Create(participant.ParticipantId);

        var locations = await db.Set<Location>().ToListAsync(ct);

        if (locations.Count == 0)
        {
            return CirSyncResult.NothingToDo(participant.ParticipantId,
                "No approved locations. Approve the stewardship queue first — nothing was sent.");
        }

        var faults = new List<string>();
        var registered = 0;
        var asserted = 0;

        var equivalences = new List<EquivalentEntry>();
        var standalone = new List<Entry>();

        foreach (var location in locations)
        {
            var entry = new Entry
            {
                IDInSource = location.LocationCode,
                SourceID = participant.Config.SourceId,
                SourceOwnerID = participant.Config.SourceOwnerId,
                Name = location.Name ?? location.LocationCode
            };

            if (location.ClassKey is { Length: > 0 })
            {
                entry.Property.Add(Property.Simple("ClassKey", location.ClassKey));
            }

            if (location.SourceParticipant is { Length: > 0 } && location.SourceIdentifier is { Length: > 0 })
            {
                equivalences.Add(new EquivalentEntry
                {
                    ExistingIDInSource = location.SourceIdentifier,
                    ExistingSourceID = location.SourceParticipant,
                    RegistryID = participant.Config.Cir.RegistryId,
                    CategoryID = SegmentCategory,
                    CategorySourceID = "OIIE-SANDBOX",
                    Entry = entry
                });
            }
            else
            {
                // No known origin, so nothing to be equivalent to. Registering it
                // standalone is correct rather than a fallback.
                standalone.Add(entry);
            }
        }

        if (standalone.Count > 0)
        {
            var result = await cir.RegisterAsync(participant, SegmentCategory, standalone, ct);
            registered = result.Succeeded ? standalone.Count : 0;
            faults.AddRange(result.Faults.Select(f => $"{f.Kind}: {f.Detail}"));
        }

        if (equivalences.Count > 0)
        {
            var result = await cir.AssertEquivalenceAsync(participant, equivalences, ct);
            asserted = result.Succeeded ? equivalences.Count : 0;
            faults.AddRange(result.Faults.Select(f => $"{f.Kind}: {f.Detail}"));
        }

        return new CirSyncResult(participant.ParticipantId, registered, asserted, faults);
    }

    /// <summary>
    /// MMS registers its own keys and asserts equivalence to whatever it received,
    /// which is how its numeric key joins the identity rather than starting a third.
    /// </summary>
    private async Task<CirSyncResult> SyncMmsAsync(ParticipantContext participant, CancellationToken ct)
    {
        await using var db = factory.Create(participant.ParticipantId);

        var records = await db.Set<FunctionalLocationRecord>().ToListAsync(ct);

        if (records.Count == 0)
        {
            return CirSyncResult.NothingToDo(participant.ParticipantId,
                "No equipment records. MMS has received nothing — nothing was sent.");
        }

        var equivalences = records
            .Where(r => r.ForeignSourceId is { Length: > 0 } && r.ForeignIdInSource is { Length: > 0 })
            .Select(r => new EquivalentEntry
            {
                ExistingIDInSource = r.ForeignIdInSource!,
                ExistingSourceID = r.ForeignSourceId!,
                RegistryID = participant.Config.Cir.RegistryId,
                CategoryID = SegmentCategory,
                CategorySourceID = "OIIE-SANDBOX",
                Entry = new Entry
                {
                    IDInSource = r.EquipmentNumber,
                    SourceID = participant.Config.SourceId,
                    SourceOwnerID = participant.Config.SourceOwnerId,
                    Name = r.Designation ?? r.EquipmentNumber
                }
            })
            .ToList();

        if (equivalences.Count == 0)
        {
            return CirSyncResult.NothingToDo(participant.ParticipantId,
                "No records carry a foreign identifier, so there is nothing to be equivalent to.");
        }

        var result = await cir.AssertEquivalenceAsync(participant, equivalences, ct);

        logger.LogInformation(
            "MMS asserted {Count} equivalence(s); its keys now join the shared identity.",
            equivalences.Count);

        return new CirSyncResult(
            participant.ParticipantId,
            0,
            result.Succeeded ? equivalences.Count : 0,
            result.Faults.Select(f => $"{f.Kind}: {f.Detail}").ToList());
    }
}
