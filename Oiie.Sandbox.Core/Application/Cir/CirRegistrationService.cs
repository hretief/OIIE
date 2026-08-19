using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Cir;
using SimHost.Application.Participants;
using SimHost.Domain.Cms;
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

    /// <summary>
    /// Category for organisational context — the owner, district or plant a record
    /// belongs to.
    ///
    /// Separate from <see cref="SegmentCategory"/> because these are different kinds
    /// of thing and entries only become comparable within a category. Filing a
    /// district alongside a functional location would invite a steward to relate a
    /// pump to a district and the registry would accept it.
    ///
    /// This category is what makes cross-schema context resolution possible at all:
    /// ENG files an iTwin GUID here, MMS files OWNER_ID 7, CMS files OWN-07, and one
    /// CIRID relates them. No participant needs a column for another's key.
    /// </summary>
    private const string ContextOwnerCategory = "ContextOwner";

    public async Task<CirSyncResult> SyncAsync(string participantId, CancellationToken ct = default)
    {
        var participant = registry.Get(participantId);

        return participantId switch
        {
            "eng" => await SyncEngAsync(participant, ct),
            "reg-location" => await SyncRegLocationAsync(participant, ct),
            "mms" => await SyncMmsAsync(participant, ct),
            "cms" => await SyncCmsAsync(participant, ct),
            _ => new CirSyncResult(participantId, 0, 0, ["No registration mapping for this participant."])
        };
    }

    /// <summary>
    /// CMS registers its context-owner domain, its sites and its assets.
    ///
    /// Everything CMS holds is registered, whether or not the identity arrived in the
    /// message. Registering an entry and asserting a CIRID are different acts: the
    /// entry says "CMS knows a thing by this code", which is always true and always
    /// safe, while equivalence says "this and that are the same thing", which can be
    /// false. Only the first is done here. A site whose SiteUUID happens to be an
    /// iTwin GUID is registered exactly like one whose is not — conditioning on that
    /// would make the rule unpredictable from the schema, and CIR's whole purpose is
    /// to cross-reference native keys rather than to require them to agree.
    ///
    /// Equivalence remains a steward's act, through RelateOwnerAsync.
    /// </summary>
    private async Task<CirSyncResult> SyncCmsAsync(ParticipantContext participant, CancellationToken ct)
    {
        await using var db = factory.Create(participant.ParticipantId);

        var owners = await db.Set<ContextOwnerRecord>().ToListAsync(ct);

        var sites = await db.Set<CmsSite>().AsNoTracking().ToListAsync(ct);

        var assets = await db.Set<CmsAsset>().AsNoTracking().ToListAsync(ct);

        if (owners.Count == 0 && sites.Count == 0 && assets.Count == 0)
        {
            return CirSyncResult.NothingToDo(participant.ParticipantId,
                "No context owners, sites or assets. CMS holds nothing — nothing was sent.");
        }

        var faults = new List<string>();
        var registered = 0;

        if (owners.Count > 0)
        {
            var ownerEntries = owners.Select(owner => new Entry
            {
                IDInSource = owner.OwnerCode,
                SourceID = participant.Config.SourceId,
                SourceOwnerID = participant.Config.SourceOwnerId,

                // The name is the only thing a steward can judge equivalence on here. Two
                // systems' codes for a district have nothing in common, so the human-
                // readable name is what carries the meaning across.
                Name = owner.OwnerName
            }).ToList();

            registered += await RegisterNewAsync(
                participant, ContextOwnerCategory, ownerEntries, faults, ct);
        }

        // Sites are context owners, not segments. A CMS site is CMS's context key in
        // the same sense OWNER_ID is MMS's and the iTwin is ENG's, so it belongs in
        // the category those are filed under — that is what lets a steward relate a
        // site to a twin at all. Filing it beside assets would instead invite someone
        // to relate a plant to a pump, and the registry would accept it.
        if (sites.Count > 0)
        {
            var siteEntries = sites.Select(site =>
            {
                var entry = new Entry
                {
                    // SiteCode, not SiteID. The surrogate is an IDENTITY column that
                    // means nothing outside this database and is reallocated by every
                    // day-zero reset, so a CIRID keyed on it would silently come to
                    // denote a different site. SiteCode is UNIQUE and stable.
                    IDInSource = site.SiteCode,
                    SourceID = participant.Config.SourceId,
                    SourceOwnerID = participant.Config.SourceOwnerId,
                    Name = site.SiteName
                };

                // Stated as a property, not used as the identifier. Where the publisher
                // sent a twin GUID this is the value that makes equivalence obvious to
                // a steward; where it did not, the column still holds something and no
                // special case is needed to decide whether to send it.
                entry.Property.Add(Property.Simple("SiteUUID", site.SiteUuid.ToString()));

                return entry;
            }).ToList();

            registered += await RegisterNewAsync(
                participant, ContextOwnerCategory, siteEntries, faults, ct);
        }

        if (assets.Count > 0)
        {
            var siteCodes = sites.ToDictionary(s => s.SiteId, s => s.SiteCode);

            var assetEntries = assets
                .Select(asset => BuildCmsAssetEntry(participant, asset, siteCodes))
                .ToList();

            registered += await RegisterNewAsync(
                participant, SegmentCategory, assetEntries, faults, ct);
        }

        logger.LogInformation(
            "CMS registered {Count} entr(ies): {Owners} owner(s), {Sites} site(s) and {Assets} asset(s).",
            registered, owners.Count, sites.Count, assets.Count);

        return new CirSyncResult(participant.ParticipantId, registered, 0, faults);
    }

    /// <summary>
    /// A CMS asset as a registry entry.
    ///
    /// AssetTag is the identifier rather than AssetID for the same reason SiteCode is
    /// preferred over SiteID: AssetID is an IDENTITY column, reallocated on reset,
    /// and a CIRID bound to it would come to denote a different asset. AssetTag is the
    /// UNIQUE business key and is what the publisher sent.
    ///
    /// Nothing here states a FederationId. The CMS asset table has no column for one
    /// and mints nothing, so the registry assigns the CIRID — which is the point.
    /// </summary>
    private static Entry BuildCmsAssetEntry(
        ParticipantContext participant,
        CmsAsset asset,
        IReadOnlyDictionary<int, string> siteCodes)
    {
        var entry = new Entry
        {
            IDInSource = asset.AssetTag,
            SourceID = participant.Config.SourceId,
            SourceOwnerID = participant.Config.SourceOwnerId,
            Name = asset.AssetName
        };

        // The site as its code, not its surrogate key. A steward comparing two entries
        // needs a value that means something outside this database.
        if (siteCodes.TryGetValue(asset.SiteId, out var siteCode))
        {
            entry.Property.Add(Property.Simple("Site", siteCode));
        }

        if (asset.OperationalStatus is { Length: > 0 } status)
        {
            entry.Property.Add(Property.Simple("OperationalStatus", status));
        }

        // Stated where present. Absent on a placeholder, and left absent rather than
        // defaulted: "not yet fitted" and "serial number unknown" are different, and a
        // placeholder value would merge them.
        if (asset.SerialNumber is { Length: > 0 } serial)
        {
            entry.Property.Add(Property.Simple("SerialNumber", serial));
        }

        return entry;
    }

    /// <summary>
    /// Registers only those entries the registry has not already seen.
    ///
    /// RegisterAsync sends one batch and the provider rejects the whole thing on the
    /// first DuplicateEntryFault, so a single previously-registered row would block
    /// every new one behind it. CMS re-syncs after every ingest, so unlike a one-shot
    /// seed this path is guaranteed to encounter existing entries and filtering is
    /// not optional.
    /// </summary>
    private async Task<int> RegisterNewAsync(
        ParticipantContext participant,
        string categoryId,
        IReadOnlyList<Entry> entries,
        List<string> faults,
        CancellationToken ct)
    {
        var unregistered = new List<Entry>();

        foreach (var entry in entries)
        {
            var existing = await cir.ResolveAsync(
                participant, participant.Config.SourceId, entry.IDInSource, ct);

            if (existing.Cirid is null)
            {
                unregistered.Add(entry);
            }
        }

        if (unregistered.Count == 0)
        {
            return 0;
        }

        var result = await cir.RegisterAsync(participant, categoryId, unregistered, ct);

        faults.AddRange(result.Faults.Select(f => $"{f.Kind}: {f.Detail}"));

        return result.Succeeded ? unregistered.Count : 0;
    }

    /// <summary>
    /// Relates a CMS owner code to a context another system already registered.
    ///
    /// This is the steward's act, and it is deliberately explicit rather than
    /// automatic. Nothing in the data says OWN-07 and a given iTwin GUID are the same
    /// district — the codes have no common structure and the names are only similar
    /// by convention. Inferring the relation from a string comparison would be a
    /// guess presented as a fact, so a human asserts it and the registry records who.
    ///
    /// Once asserted, the CIRID is written onto the CMS owner row and cached in the
    /// identity map, which is what lets subsequently ingested events resolve their
    /// context without a round trip per event.
    ///
    /// See <see cref="RelateOwnerAsync"/> for why the registry operation used
    /// depends on which entries already exist.
    /// </summary>
    public async Task<CirSyncResult> RelateCmsOwnerAsync(
        string ownerCode,
        string foreignSourceId,
        string foreignIdInSource,
        CancellationToken ct = default)
    {
        var participant = registry.Get("cms");
        await using var db = factory.Create(participant.ParticipantId);

        var owner = await db.Set<ContextOwnerRecord>()
            .FirstOrDefaultAsync(o => o.OwnerCode == ownerCode, ct);

        if (owner is null)
        {
            return new CirSyncResult(participant.ParticipantId, 0, 0,
                [$"CMS holds no context owner {ownerCode}."]);
        }

        // The write-back is the one thing CMS does that MMS cannot: it owns its
        // ContextOwnerRecord table, so it can cache the CIRID and spare later
        // ingest a resolution per event.
        return await RelateOwnerAsync(
            participant,
            localIdInSource: owner.OwnerCode,
            localName: owner.OwnerName,
            foreignSourceId: foreignSourceId,
            foreignIdInSource: foreignIdInSource,
            onResolved: async cirid =>
            {
                owner.Cirid = cirid;
                owner.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            },
            ct: ct);
    }

    /// <summary>
    /// Relates a CMS site to a context another system already registered.
    ///
    /// This is the operation that makes twin-scoped reads possible at all. CMS holds
    /// the publisher's SiteUUID, and matching it against an iTwin GUID would appear to
    /// work — which is exactly why the relation is asserted instead. Two columns
    /// holding equal bytes is a coincidence the schema does not guarantee; an
    /// equivalence is a claim somebody made and the registry records.
    ///
    /// The indirection is the product being demonstrated. MMS files a district as the
    /// integer 1, CMS files the same plant as site 9100, and ENG knows it as a GUID.
    /// Once each is related, all three converge on one CIRID and any of them can reach
    /// the others without holding a column for a foreign key space.
    ///
    /// Unlike RelateCmsOwnerAsync there is no write-back: cms.Site has no CIRID column
    /// and may not be given one, so the registry is the sole record of the relation and
    /// every scoped read pays a resolution through
    /// <see cref="CmsContextResolver.ResolveSiteCodesAsync"/>.
    ///
    /// Sites are registered into the ContextOwner category by SyncCmsAsync, which is
    /// what allows this to reuse <see cref="RelateOwnerAsync"/> unchanged — and what
    /// prevents anyone relating a plant to a pump, since a segment entry is not in
    /// that category to be found.
    /// </summary>
    public async Task<CirSyncResult> RelateCmsSiteAsync(
        string siteCode,
        string foreignSourceId,
        string foreignIdInSource,
        CancellationToken ct = default)
    {
        var participant = registry.Get("cms");
        await using var db = factory.Create(participant.ParticipantId);

        var site = await db.Set<CmsSite>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SiteCode == siteCode, ct);

        if (site is null)
        {
            return new CirSyncResult(participant.ParticipantId, 0, 0,
                [$"CMS holds no site {siteCode}. Provision the site before relating it."]);
        }

        return await RelateOwnerAsync(
            participant,
            localIdInSource: site.SiteCode,
            localName: site.SiteName,
            foreignSourceId: foreignSourceId,
            foreignIdInSource: foreignIdInSource,
            onResolved: null,
            ct: ct);
    }

    /// <summary>
    /// Relates an MMS OWNER_ID to a context another system already registered.
    ///
    /// The steward's act, as for CMS: nothing in the data says OWNER_ID 2 and a
    /// given iTwin are the same district, and inferring it from the name would be a
    /// guess dressed as a fact.
    ///
    /// Two registry operations can express this, and which one applies depends on
    /// what is already registered rather than on what the steward means:
    ///
    ///   MMS owner not yet an entry -> CreateEquivalentEntries (§3.1.2) resolves the
    ///                                 foreign entry and inserts the MMS one beside it
    ///   both already entries       -> ChangeEntryCIRID (§3.1.4) collapses the two
    ///                                 identities onto one
    ///
    /// The second case is the normal one here, not an edge case: SyncMmsAsync
    /// registers every SETUP_OWNER row and ENG registers every twin, both into the
    /// ContextOwner category, so by the time a steward relates them neither side is
    /// new and an insert would fault with DuplicateEntry.
    ///
    /// Unlike the CMS path there is no write-back. CMS caches the CIRID on its own
    /// owner row; MMS has no column to cache it in and may not be given one, so the
    /// registry is the only place this relation exists and every later read pays a
    /// resolution through <see cref="MmsContextResolver"/>.
    /// </summary>
    public async Task<CirSyncResult> RelateMmsOwnerAsync(
        long ownerId,
        string foreignSourceId,
        string foreignIdInSource,
        CancellationToken ct = default)
    {
        var participant = registry.Get("mms");
        await using var db = factory.Create(participant.ParticipantId);

        var owner = await db.Set<SetupOwner>()
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OwnerId == ownerId, ct);

        if (owner is null)
        {
            return new CirSyncResult(participant.ParticipantId, 0, 0,
                [$"MMS holds no owner with OWNER_ID {ownerId}."]);
        }

        return await RelateOwnerAsync(
            participant,
            localIdInSource: owner.OwnerId.ToString(),
            localName: owner.OwnerName,
            foreignSourceId: foreignSourceId,
            foreignIdInSource: foreignIdInSource,
            onResolved: null,
            ct: ct);
    }

    /// <summary>
    /// The shared relate mechanic for CMS and MMS.
    ///
    /// Both participants face the same question — are these two registry entries the
    /// same real district? — and the same constraint that the answer depends on
    /// which entries already exist. Only the write-back differs, so that is the part
    /// passed in.
    /// </summary>
    private async Task<CirSyncResult> RelateOwnerAsync(
        ParticipantContext participant,
        string localIdInSource,
        string localName,
        string foreignSourceId,
        string foreignIdInSource,
        Func<Guid, Task>? onResolved,
        CancellationToken ct)
    {
        // What the registry already holds for each side. This is what decides which
        // operation is legal, so it is read before anything is asserted.
        var foreign = await cir.ResolveAsync(
            participant, foreignSourceId, foreignIdInSource, ct);

        if (foreign.Cirid is not { } foreignCirid)
        {
            return new CirSyncResult(participant.ParticipantId, 0, 0,
                [$"{foreignSourceId}:{foreignIdInSource} is not registered, so there is " +
                 "nothing to relate to. Register it first."]);
        }

        var local = await cir.ResolveAsync(
            participant, participant.Config.SourceId, localIdInSource, ct);

        if (local.Cirid is { } localCirid)
        {
            // Both sides exist. Inserting would fault; collapsing the two identities
            // onto the foreign one is the operation that means "these are the same".
            if (localCirid == foreignCirid)
            {
                logger.LogInformation(
                    "{ParticipantId} {IdInSource} is already related to {SourceId}:{ForeignId} as {Cirid}.",
                    participant.ParticipantId, localIdInSource, foreignSourceId,
                    foreignIdInSource, foreignCirid);

                if (onResolved is not null) await onResolved(foreignCirid);

                return new CirSyncResult(participant.ParticipantId, 0, 0, []);
            }

            var relink = await cir.RelinkCiridAsync(
                participant, [localCirid], foreignCirid, ct);

            if (!relink.Succeeded)
            {
                return new CirSyncResult(participant.ParticipantId, 0, 0,
                    relink.Faults.Select(f => $"{f.Kind}: {f.Detail}").ToList());
            }

            if (onResolved is not null) await onResolved(foreignCirid);

            logger.LogInformation(
                "{ParticipantId} {IdInSource} ({Name}) relinked from {Old} to {New} to match {SourceId}:{ForeignId}.",
                participant.ParticipantId, localIdInSource, localName,
                localCirid, foreignCirid, foreignSourceId, foreignIdInSource);

            return new CirSyncResult(participant.ParticipantId, 0, 1, []);
        }

        // Only the foreign side exists, so the local entry can be inserted against it.
        var equivalence = new EquivalentEntry
        {
            ExistingIDInSource = foreignIdInSource,
            ExistingSourceID = foreignSourceId,
            RegistryID = participant.Config.Cir.RegistryId,
            CategoryID = ContextOwnerCategory,
            CategorySourceID = "OIIE-SANDBOX",
            Entry = new Entry
            {
                IDInSource = localIdInSource,
                SourceID = participant.Config.SourceId,
                SourceOwnerID = participant.Config.SourceOwnerId,
                Name = localName
            }
        };

        var result = await cir.AssertEquivalenceAsync(participant, [equivalence], ct);

        if (!result.Succeeded)
        {
            return new CirSyncResult(participant.ParticipantId, 0, 0,
                result.Faults.Select(f => $"{f.Kind}: {f.Detail}").ToList());
        }

        // Read back rather than assume. The registry decides which CIRID the two
        // entries converge on — it may be one that already existed — and writing a
        // locally invented value would put the participant out of step with it.
        if (onResolved is not null)
        {
            var resolution = await cir.ResolveAsync(
                participant, foreignSourceId, foreignIdInSource, ct);

            if (resolution.Cirid is { } cirid) await onResolved(cirid);
        }

        logger.LogInformation(
            "{ParticipantId} {IdInSource} ({Name}) related to {SourceId}:{ForeignId}.",
            participant.ParticipantId, localIdInSource, localName,
            foreignSourceId, foreignIdInSource);

        return new CirSyncResult(participant.ParticipantId, 0, 1, []);
    }

    /// <summary>
    /// ENG registers what it originated, including the FederationId it minted.
    /// Nothing here asserts equivalence: ENG has only ever seen its own codes, so it
    /// has nothing to compare them to.
    /// </summary>
    private async Task<CirSyncResult> SyncEngAsync(ParticipantContext participant, CancellationToken ct)
    {
        await using var db = factory.Create(participant.ParticipantId);

        var tags = await db.Set<Tag>()
            .Where(t => t.Maturity == TagMaturity.Published)
            .ToListAsync(ct);

        var entries = tags.Select(tag =>
        {
            var entry = new Entry
            {
                IDInSource = tag.TagNumber,
                SourceID = participant.Config.SourceId,
                SourceOwnerID = participant.Config.SourceOwnerId,
                Name = tag.ServiceDescription ?? tag.TagNumber
            };

            // The identity ENG minted, stated alongside the code it uses. A registry
            // entry that carries the FederationId needs no equivalence reasoning to be
            // linked — it has already said what it is.
            if (tag.FederationId != Guid.Empty)
            {
                entry.Property.Add(Property.Simple("FederationId", tag.FederationId.ToString()));
            }

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

        // Segments and context owners are registered independently. ENG having no
        // published tags yet is the normal day-one state, and it must not stop the
        // twins from being registered: a steward needs an ENG-side context owner to
        // relate a district to long before any tag has been promoted.
        var faults = new List<string>();
        var registered = 0;

        if (entries.Count > 0)
        {
            var result = await cir.RegisterAsync(participant, SegmentCategory, entries, ct);

            faults.AddRange(result.Faults.Select(f => $"{f.Kind}: {f.Detail}"));
            registered += result.Succeeded ? entries.Count : 0;
        }

        // ENG also registers its twins as context owners.
        //
        // The twin is ENG's context key in the same sense OWNER_ID is MMS's, so it
        // belongs in the same category. Registering it here is what gives a steward
        // something to relate a district code to — without it there is no ENG-side
        // entry for an equivalence assertion to reference.
        var twins = await db.ITwins.ToListAsync(ct);

        // Only twins the registry has never seen. RegisterAsync sends one batch and
        // the provider rejects the whole thing on the first DuplicateEntryFault, so
        // a single previously-registered twin would otherwise block every new one
        // behind it. Registration has to stay callable repeatedly as twins are added.
        var unregistered = new List<ITwin>();

        foreach (var twin in twins)
        {
            var existing = await cir.ResolveAsync(
                participant, participant.Config.SourceId, twin.Id.ToString(), ct);

            if (existing.Cirid is null)
            {
                unregistered.Add(twin);
            }
        }

        if (unregistered.Count > 0)
        {
            var owners = unregistered.Select(twin => new Entry
            {
                IDInSource = twin.Id.ToString(),
                SourceID = participant.Config.SourceId,
                SourceOwnerID = participant.Config.SourceOwnerId,
                Name = twin.Name
            }).ToList();

            var ownerResult = await cir.RegisterAsync(
                participant, ContextOwnerCategory, owners, ct);

            if (ownerResult.Succeeded)
            {
                registered += owners.Count;
            }

            faults.AddRange(ownerResult.Faults.Select(f => $"{f.Kind}: {f.Detail}"));
        }

        return new CirSyncResult(
            participant.ParticipantId,
            registered,
            0,
            faults,
            entries.Count == 0
                ? "No published tags, so no segments were registered. Any iTwins were still registered as context owners."
                : null);
    }

    /// <summary>
    /// REG-LOCATION relates its own code to the one it received.
    ///
    /// It adopted ENG's FederationId and issued LOC-000001 as a second code for the
    /// same entity, so the two codes already converge on one identity. The
    /// equivalence assertion remains because a legacy consumer may only ever see
    /// codes, and it must still be able to get from ENG:TIC-106 to LOC-000001 without
    /// understanding federation at all.
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

            if (location.FederationId != Guid.Empty)
            {
                entry.Property.Add(
                    Property.Simple("FederationId", location.FederationId.ToString()));
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
    /// MMS registers what it actually has: owners and light systems, keyed by the
    /// customer's own identifiers.
    ///
    /// No equivalence is asserted here, and that is a change from how MMS used to
    /// behave. It previously held a foreign identifier on every row and could pair
    /// it with its own key unaided. The real schema has no such column, so MMS can
    /// no longer know what any of its rows correspond to elsewhere. Registration is
    /// therefore all it can do alone; relating an owner to an iTwin is a steward's
    /// judgement, made through RelateMmsOwnerAsync.
    /// </summary>
    private async Task<CirSyncResult> SyncMmsAsync(ParticipantContext participant, CancellationToken ct)
    {
        await using var db = factory.Create(participant.ParticipantId);

        var owners = await db.Set<SetupOwner>()
            .AsNoTracking()
            .Where(o => o.ActiveFlag)
            .ToListAsync(ct);

        var inventory = await db.Set<LightSystemInventory>()
            .AsNoTracking()
            .ToListAsync(ct);

        if (owners.Count == 0 && inventory.Count == 0)
        {
            return CirSyncResult.NothingToDo(participant.ParticipantId,
                "No owners or light systems. MMS holds nothing — nothing was sent.");
        }

        var faults = new List<string>();
        var registered = 0;

        // Owners first. An inventory row is only interesting in context, and the
        // context entry has to exist before a steward can relate a twin to it.
        if (owners.Count > 0)
        {
            var ownerEntries = owners.Select(owner => new Entry
            {
                IDInSource = owner.OwnerId.ToString(),
                SourceID = participant.Config.SourceId,
                SourceOwnerID = participant.Config.SourceOwnerId,
                Name = owner.OwnerName
            }).ToList();

            var ownerResult = await cir.RegisterAsync(
                participant, ContextOwnerCategory, ownerEntries, ct);

            if (ownerResult.Succeeded) registered += ownerEntries.Count;
            faults.AddRange(ownerResult.Faults.Select(f => $"{f.Kind}: {f.Detail}"));
        }

        if (inventory.Count > 0)
        {
            // Reference data, resolved to names so a steward comparing two entries
            // sees "Interchange" rather than 4. The codes mean nothing outside MMS.
            var classCodes = await db.Set<LightSystemClassCode>()
                .AsNoTracking().ToDictionaryAsync(c => c.LightSystemClassCodeId, c => c.LightSystemClassCodeName, ct);

            var statuses = await db.Set<SetupAssetStatus>()
                .AsNoTracking().ToDictionaryAsync(s => s.AssetStatusId, s => s.AssetStatusName, ct);

            var ownerNames = owners.ToDictionary(o => o.OwnerId, o => o.OwnerName);

            var entries = inventory
                .Select(row => BuildMmsEntry(participant, row, classCodes, statuses, ownerNames))
                .ToList();

            var result = await cir.RegisterAsync(participant, SegmentCategory, entries, ct);

            if (result.Succeeded) registered += entries.Count;
            faults.AddRange(result.Faults.Select(f => $"{f.Kind}: {f.Detail}"));
        }

        logger.LogInformation(
            "MMS registered {Count} entr(ies): {Owners} owner(s) and {Inventory} light system(s).",
            registered, owners.Count, inventory.Count);

        return new CirSyncResult(participant.ParticipantId, registered, 0, faults);
    }

    /// <summary>
    /// A light system as a registry entry.
    ///
    /// LIGHT_SYSTEM_ID is the identifier because it is the primary key, but the
    /// name carries the meaning, so it goes on as a property rather than being
    /// discarded. The class code, status and owner are the discriminating values a
    /// steward needs to judge whether two entries are the same physical thing.
    ///
    /// Nothing here states a FederationId: MMS has no column for one and mints
    /// nothing. The registry assigns the CIRID, which is the whole point.
    /// </summary>
    private static Entry BuildMmsEntry(
        ParticipantContext participant,
        LightSystemInventory row,
        IReadOnlyDictionary<long, string> classCodes,
        IReadOnlyDictionary<long, string> statuses,
        IReadOnlyDictionary<long, string> ownerNames)
    {
        var entry = new Entry
        {
            IDInSource = row.LightSystemId.ToString(),
            SourceID = participant.Config.SourceId,
            SourceOwnerID = participant.Config.SourceOwnerId,
            Name = row.LightSystemName
        };

        // The alternate key. Registering it explicitly means a steward can match on
        // the name a human would recognise, not just an opaque number.
        entry.Property.Add(Property.Simple("LightSystemName", row.LightSystemName));

        if (classCodes.TryGetValue(row.LightSystemClassCodeId, out var className))
        {
            entry.Property.Add(Property.Simple("ClassCode", className));
        }

        if (row.LightSystemStatusId is { } statusId && statuses.TryGetValue(statusId, out var statusName))
        {
            entry.Property.Add(Property.Simple("Status", statusName));
        }

        // Owner is stated where present. A null OWNER_ID is left absent rather than
        // defaulted, because "no owner" and "owner unknown to us" are different and
        // a placeholder would merge them.
        if (row.OwnerId is { } ownerId && ownerNames.TryGetValue(ownerId, out var ownerName))
        {
            entry.Property.Add(Property.Simple("Owner", ownerName));
        }

        return entry;
    }
}
