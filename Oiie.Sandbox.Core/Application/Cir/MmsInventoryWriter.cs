using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimHost.Application.Participants;
using SimHost.Domain.Mms;
using SimHost.Infrastructure.Sql;
using SimHost.Personalities.Mms;

namespace SimHost.Application.Cir;

/// <summary>
/// Inserts a light system into the customer's inventory and registers its key.
///
/// The two halves belong together. LIGHT_SYSTEM_ID is meaningless outside MMS, so
/// a row inserted without a registry entry is invisible to every other system, and
/// a registry entry without a row points at nothing. Keeping them in one place is
/// what stops the pair from drifting apart.
///
/// MMS mints no FederationId here even though it is originating the row. It has
/// nowhere to store one, and the registry assigns the CIRID on registration, so a
/// locally minted identity would be a second identity that nothing reads.
/// </summary>
public sealed class MmsInventoryWriter(
    CirRegistrationService registration,
    ParticipantRegistry registry,
    IParticipantDbContextFactory factory,
    ILogger<MmsInventoryWriter> logger)
{
    /// <summary>
    /// Allocates the next LIGHT_SYSTEM_ID, writes the row, then registers it.
    ///
    /// Allocation is MAX(LIGHT_SYSTEM_ID)+1 inside the insert transaction. This is a
    /// sandbox-grade choice and not safe against a concurrently writing customer
    /// system: two callers can read the same maximum before either commits. It is
    /// acceptable here because the sandbox is the only writer, and it must not be
    /// carried into production without a sequence or an allocation procedure.
    /// </summary>
    public async Task<MmsInsertResult> InsertAsync(
        string lightSystemName,
        long classCodeId,
        long? statusId,
        long? ownerId,
        CancellationToken ct = default)
    {
        var participant = registry.Get(MmsService.ParticipantId);
        await using var db = factory.Create(participant.ParticipantId);

        // LIGHT_SYSTEM_NAME is the alternate key, so a duplicate is a real conflict
        // rather than something to silently deduplicate. Reporting it lets the
        // caller decide whether they meant to update an existing system.
        var clash = await db.Set<LightSystemInventory>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LightSystemName == lightSystemName, ct);

        if (clash is not null)
        {
            return MmsInsertResult.Rejected(
                $"LIGHT_SYSTEM_NAME '{lightSystemName}' already exists as LIGHT_SYSTEM_ID {clash.LightSystemId}.");
        }

        // Reject unknown reference data rather than writing an orphan. The lookup
        // tables are the customer's, and a row pointing at a class code they do not
        // have is corruption we introduced.
        var classExists = await db.Set<LightSystemClassCode>()
            .AsNoTracking().AnyAsync(c => c.LightSystemClassCodeId == classCodeId, ct);

        if (!classExists)
        {
            return MmsInsertResult.Rejected(
                $"LIGHT_SYSTEM_CLASS_CODE_ID {classCodeId} does not exist.");
        }

        if (statusId is { } status)
        {
            var statusExists = await db.Set<SetupAssetStatus>()
                .AsNoTracking().AnyAsync(s => s.AssetStatusId == status, ct);

            if (!statusExists)
            {
                return MmsInsertResult.Rejected($"ASSET_STATUS_ID {status} does not exist.");
            }
        }

        if (ownerId is { } owner)
        {
            var ownerExists = await db.Set<SetupOwner>()
                .AsNoTracking().AnyAsync(o => o.OwnerId == owner, ct);

            if (!ownerExists)
            {
                return MmsInsertResult.Rejected($"OWNER_ID {owner} does not exist.");
            }
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var maxId = await db.Set<LightSystemInventory>()
            .AsNoTracking()
            .MaxAsync(r => (long?)r.LightSystemId, ct) ?? 0;

        var row = new LightSystemInventory
        {
            LightSystemId = maxId + 1,
            LightSystemName = lightSystemName,
            LightSystemClassCodeId = classCodeId,
            LightSystemStatusId = statusId,
            OwnerId = ownerId
        };

        db.Set<LightSystemInventory>().Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Registration follows the commit. If it fails the row still exists and is
        // simply unregistered, which a later sync corrects; registering first would
        // risk advertising a key that no row ever got.
        var sync = await registration.SyncAsync(MmsService.ParticipantId, ct);

        logger.LogInformation(
            "MMS inserted LIGHT_SYSTEM_ID {Id} ('{Name}') and registered it with the registry.",
            row.LightSystemId, row.LightSystemName);

        return MmsInsertResult.Inserted(row.LightSystemId, sync.Faults);
    }
}

/// <summary>
/// The outcome of an inventory insert, carrying any registration faults separately
/// so a written-but-unregistered row is distinguishable from a rejected one.
/// </summary>
public sealed record MmsInsertResult(
    bool Success,
    long? LightSystemId,
    string? Reason,
    IReadOnlyList<string> RegistrationFaults)
{
    public static MmsInsertResult Inserted(long id, IReadOnlyList<string> faults)
        => new(true, id, null, faults);

    public static MmsInsertResult Rejected(string reason)
        => new(false, null, reason, []);
}
