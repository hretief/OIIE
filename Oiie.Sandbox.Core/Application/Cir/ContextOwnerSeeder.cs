using Microsoft.EntityFrameworkCore;
using SimHost.Domain.Cms;
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
        return Inventory.Length;
    }
}
