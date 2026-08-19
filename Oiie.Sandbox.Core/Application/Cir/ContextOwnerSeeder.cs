using Microsoft.EntityFrameworkCore;
using SimHost.Domain.Cms;
using SimHost.Domain.Eng;
using SimHost.Domain.Mms;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Cir;

/// <summary>
/// Seeds each participant's own context-owner domain table.
///
/// The organisations are the same eleven districts everywhere — they are one real
/// world — but the key each system files them under is its own. MMS holds
/// <c>dbo.SETUP_OWNER (OWNER_ID, OWNER_NAME)</c> keyed by a local integer; CMS keys
/// the same districts by its own <c>OWN-nn</c> code; ENG knows them as iTwin GUIDs.
///
/// The codes are deliberately different across systems. Seeding CMS with MMS's
/// integers would make a direct join work and the sandbox would prove nothing: the
/// whole point is that no participant can get from its own key to another's without
/// the registry. Divergent keys are the condition ws-CIR exists to resolve.
/// </summary>
public static class ContextOwnerSeeder
{
    /// <summary>
    /// The shared organisational reality, in the order MMS lists it. Each participant
    /// derives its own local key from this; nothing here is an identifier.
    /// </summary>
    public static readonly IReadOnlyList<string> OwnerNames =
    [
        "7000 - Metro District",
        "7200 - Metro Traffic",
        "8300 - Maintenance",
        "9100 - District 1",
        "9200 - District 2",
        "9300 - District 3",
        "9400 - District 4",
        "9600 - District 6",
        "9700 - District 7",
        "9800 - District 8",
        "MnDOT"
    ];

    /// <summary>
    /// CMS's local key for an owner, by position in <see cref="OwnerNames"/>.
    /// Meaningless outside CMS, which is the property being demonstrated.
    /// </summary>
    public static string CmsOwnerCode(int index) => $"OWN-{index + 1:D2}";

    /// <summary>
    /// MMS's local key for an owner, by position in <see cref="OwnerNames"/>.
    ///
    /// The seeder assigns OWNER_ID from this same position, so the two cannot drift.
    /// Stated as a function rather than a second hard-coded table because a duplicated
    /// list would be correct only until somebody inserted a district into one of them.
    /// </summary>
    public static long MmsOwnerId(int index) => index + 1;

    /// <summary>
    /// The position of an owner in <see cref="OwnerNames"/>, or -1 when the name is
    /// not one of them. Used to get from a seeded twin back to the participant keys
    /// for the same district.
    /// </summary>
    public static int OwnerIndex(string ownerName)
    {
        for (var i = 0; i < OwnerNames.Count; i++)
        {
            if (string.Equals(OwnerNames[i], ownerName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The district number embedded in an owner name, or null where there is none.
    ///
    /// The customer's names carry it as a prefix — "9100 - District 1" — and that
    /// number is the site as every system's operators say it aloud. It is extracted
    /// rather than held in a second list so the two cannot drift apart.
    ///
    /// "MnDOT" has no number because it is the agency, not a district, and so becomes
    /// no site: provisioning a site for it would invent a plant that does not exist.
    /// </summary>
    public static string? SiteCodeFor(string ownerName)
    {
        var separator = ownerName.IndexOf(" - ", StringComparison.Ordinal);

        if (separator <= 0)
        {
            return null;
        }

        var candidate = ownerName[..separator];

        return candidate.All(char.IsAsciiDigit) ? candidate : null;
    }

    /// <summary>
    /// The iTwins ENG holds designs for, with the GUIDs Bentley assigned them.
    ///
    /// A fixed list rather than something derived from <see cref="OwnerNames"/>,
    /// because these identifiers are real: they were minted by iTwin and are the
    /// keys the actual platform answers to. Generating them would produce values
    /// that look right and match nothing outside this process.
    ///
    /// ENG covers four districts, not all eleven. A twin exists where somebody has
    /// modelled a plant, which is not everywhere the customer operates -- and that
    /// asymmetry is worth keeping, since a registry whose participants all knew the
    /// same things would not be demonstrating much.
    ///
    /// The site code on each is the district number the rest of the estate uses, so
    /// the twin can be related to the CMS site of the same code.
    /// </summary>
    public static readonly IReadOnlyList<(Guid Id, string Code, string Name)> EngTwins =
    [
        (new Guid("523099d2-4291-4d0f-ad7c-65429109ef81"), "9100", "9100 - District 1"),
        (new Guid("d543ebf6-7f25-4c07-a8cf-cc43410b780d"), "9200", "9200 - District 2"),
        (new Guid("02c9fdd8-645d-4d97-8d95-70be46a58345"), "7200", "7200 - Metro Traffic"),
        (new Guid("c86c9c10-4487-48f6-8f5b-89701307725c"), "9600", "9600 - District 6")
    ];

    /// <summary>
    /// Provisions ENG's iTwins.
    ///
    /// Seeded for the same reason CMS sites are: the twin is the context a design
    /// belongs to, and it has to exist before a tag can be scoped to it or a steward
    /// can relate it to anything. ENG's own <c>EnsureTwinAsync</c> would create one
    /// lazily on first write, but that produces a twin with a GUID nobody outside
    /// this process recognises -- fine as a fallback, useless as an identity.
    /// </summary>
    public static async Task<int> SeedEngTwinsAsync(
        ParticipantDbContext db, CancellationToken ct = default)
    {
        var existing = await db.ITwins.Select(t => t.Id).ToListAsync(ct);
        var seeded = 0;

        foreach (var (id, _, name) in EngTwins)
        {
            if (existing.Contains(id))
            {
                continue;
            }

            db.ITwins.Add(new ITwin
            {
                Id = id,
                Code = SiteCodeFor(name) ?? name,
                Name = name
            });

            seeded++;
        }

        if (seeded > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return seeded;
    }

    /// <summary>
    /// Provisions the CMS sites.
    ///
    /// A site exists here before any message mentions it, which is the point: sites
    /// are created by an act of provisioning — in production, BIC publishing SyncSites
    /// — not by the arrival of a design artefact. An asset may only land at a plant
    /// somebody has established.
    ///
    /// The site code is the district number, which MMS also uses. That the two agree
    /// is not a join and is not relied upon: CMS reaches MMS's key space only through
    /// the registry, and the codes agreeing merely makes the equivalence a steward
    /// asserts an obvious one rather than an arbitrary one. Nothing in the read path
    /// matches these strings against each other.
    ///
    /// Cirid is not seeded, and could not be. Relating this site to an iTwin requires
    /// the twin to already be an entry in the registry, which is a fact about a remote
    /// system that a table seed cannot establish or verify. The relation is asserted
    /// afterwards through RelateCmsSiteAsync.
    /// </summary>
    public static async Task<int> SeedCmsSitesAsync(
        ParticipantDbContext db, CancellationToken ct = default)
    {
        if (await db.Set<CmsSite>().AnyAsync(ct))
        {
            return 0;
        }

        var seeded = 0;

        for (var i = 0; i < OwnerNames.Count; i++)
        {
            var name = OwnerNames[i];
            var code = SiteCodeFor(name);

            if (code is null)
            {
                continue;
            }

            db.Set<CmsSite>().Add(new CmsSite
            {
                // Deterministic rather than random. A provisioned site has no publisher
                // to have supplied a UUID, and re-running a reset should not silently
                // mint a new identity for the same plant. It stays retained data: it is
                // never matched against a foreign identifier.
                SiteUuid = DeterministicSiteUuid(code),
                SiteCode = code,
                SiteName = name,
                CreatedAtUtc = DateTime.UtcNow
            });

            seeded++;
        }

        await db.SaveChangesAsync(ct);
        return seeded;
    }

    /// <summary>
    /// A stable UUID for a provisioned site, derived from its code.
    ///
    /// Deliberately not a v4: two resets must produce the same value, or a site's
    /// retained identity would change every day zero and any equivalence asserted
    /// against it would quietly come to describe a different row.
    /// </summary>
    private static Guid DeterministicSiteUuid(string siteCode)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"cms.site:{siteCode}"));

        return new Guid(bytes);
    }

    /// <summary>
    /// Populates the CMS owner table if it is empty.
    ///
    /// Cirid is left null on every row. Seeding a correlation identifier here would
    /// assert an equivalence nobody has established, which is exactly the shortcut
    /// this design exists to avoid — the relation is a steward's act, not a fixture's.
    /// </summary>
    public static async Task<int> SeedCmsAsync(
        ParticipantDbContext db, CancellationToken ct = default)
    {
        if (await db.Set<ContextOwnerRecord>().AnyAsync(ct))
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < OwnerNames.Count; i++)
        {
            db.Set<ContextOwnerRecord>().Add(new ContextOwnerRecord
            {
                OwnerCode = CmsOwnerCode(i),
                OwnerName = OwnerNames[i],
                Cirid = null,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        return OwnerNames.Count;
    }

    /// <summary>
    /// MMS's class codes, exactly as the customer supplied them.
    /// </summary>
    private static readonly (long Id, string Name)[] ClassCodes =
    [
        (1, "Bridge"),
        (2, "Continuous"),
        (3, "Downtown"),
        (4, "Interchange"),
        (5, "Intersection"),
        (6, "Other"),
        (7, "Rest Area"),
        (8, "Tunnel"),
        (9, "Undetermined")
    ];

    /// <summary>
    /// MMS's asset statuses, as supplied. Shared across asset types, hence the
    /// SETUP_ prefix rather than a LIGHT_SYSTEM_ one.
    /// </summary>
    private static readonly (long Id, string Name)[] AssetStatuses =
    [
        (1, "Abandoned"),
        (2, "Active"),
        (3, "Proposed"),
        (4, "Retired")
    ];

    /// <summary>
    /// A sample of the customer's real inventory, reproduced verbatim.
    ///
    /// These are the customer's own LIGHT_SYSTEM_IDs and names rather than invented
    /// ones, so the demo exercises the same shapes the real data has: names carrying
    /// embedded route and structure codes, statuses that are not all Active, and two
    /// distinct owners. Row 9 is deliberately included as the one Proposed,
    /// non-Interchange outlier.
    /// </summary>
    private static readonly (long Id, string Name, long ClassCode, long? Status, long? Owner)[] Inventory =
    [
        (1, "LightSys-I35-(2)-B26J-1756078", 9, 2, 2),
        (2, "LightSys-I35-(60)/185TH/ORCHARD LAKE TRAIL-B27S-1756080", 9, 2, 2),
        (3, "LightSys-I35-(50)/KENWOOD TRAIL-B28C-1756081", 9, 2, 2),
        (4, "LightSys-I35-FOREST LAKE REST AREA-B34H-1756086", 7, 2, 2),
        (5, "LightSys-I35-(2)/W BROADWAY AVE-B35J-1756087", 4, 2, 2),
        (6, "LightSys-I35-(22)-B36R-1756088", 9, 2, 2),
        (7, "LightSys-I35-(1)-B48T-1756093", 4, 2, 2),
        (8, "LightSys-I35-KENYON AVE-B27T-1757193", 9, 2, 2),
        (9, "LightSys-OTTERLAKE RD-FP B-23526861", 5, 3, 2),
        (10, "LightSys-I35-US65 N Jct, Albert Lea-B05F-1756063", 4, 2, 8),
        (11, "LightSys-I35-Straight River RA SB, S of Owatonna-B12S-1756067", 7, 2, 8),
        (12, "LightSys-I35-(2)/Hoffman Dr, Owatonna-B14E-1756068", 4, 2, 8),
        (13, "LightSys-I35-(12) W Ramps, Medford-B18W-1756969", 4, 2, 8)
    ];

    /// <summary>
    /// Populates MMS's reference tables and a sample of its inventory.
    ///
    /// OWNER_ID is the position in <see cref="OwnerNames"/> plus one, which is the
    /// customer's actual numbering. That MMS and CMS both derive their keys from the
    /// same list is a coincidence of ordering, not a join: MMS files district 2 as
    /// the integer 2 and CMS files it as OWN-02, and neither can reach the other
    /// without the registry.
    ///
    /// No CIRID is seeded anywhere, and none could be: MMS has no column for one.
    /// Relating an owner to an iTwin remains a steward's act.
    /// </summary>
    public static async Task<int> SeedMmsAsync(
        ParticipantDbContext db, CancellationToken ct = default)
    {
        if (await db.Set<LightSystemInventory>().AnyAsync(ct))
        {
            return 0;
        }

        var now = DateTime.UtcNow;

        foreach (var (id, name) in ClassCodes)
        {
            db.Set<LightSystemClassCode>().Add(new LightSystemClassCode
            {
                LightSystemClassCodeId = id,
                LightSystemClassCodeName = name,
                ActiveFlag = true,
                UserUpdate = "seed",
                DateUpdate = now
            });
        }

        foreach (var (id, name) in AssetStatuses)
        {
            db.Set<SetupAssetStatus>().Add(new SetupAssetStatus
            {
                AssetStatusId = id,
                AssetStatusName = name,
                ActiveFlag = true,
                UserUpdate = "seed",
                DateUpdate = now
            });
        }

        for (var i = 0; i < OwnerNames.Count; i++)
        {
            db.Set<SetupOwner>().Add(new SetupOwner
            {
                OwnerId = i + 1,
                OwnerName = OwnerNames[i],
                ActiveFlag = true,
                UserUpdate = "seed",
                DateUpdate = now
            });
        }

        foreach (var (id, name, classCode, status, owner) in Inventory)
        {
            db.Set<LightSystemInventory>().Add(new LightSystemInventory
            {
                LightSystemId = id,
                LightSystemName = name,
                LightSystemClassCodeId = classCode,
                LightSystemStatusId = status,
                OwnerId = owner
            });
        }

        await db.SaveChangesAsync(ct);

        // The owner count, not the inventory count. Both callers report this as
        // contextOwners, and returning Inventory.Length made day zero claim MMS held
        // 13 owners when it holds 11 -- close enough to the real number to look
        // plausible and wrong enough to send someone hunting for two rows that were
        // never there.
        return OwnerNames.Count;
    }
}
