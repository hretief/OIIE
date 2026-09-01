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

    // CMS and MMS are no longer seeded here, and nothing replaces this.
    //
    // Sites arrive at those systems the way they arrive in production: ENG publishes
    // SyncSites, and CmsEngine and MmsEngine consume it and call their provider's
    // REST API. Provisioning the same rows from a fixture made the demo look like the
    // integration worked when nothing had crossed the bus -- the sites were simply
    // already there, in both systems, agreeing with each other.
    //
    // A greenfield now starts with CMS and MMS genuinely empty. That they fill up is
    // evidence the flow ran.

}
