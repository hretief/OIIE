using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using SimHost.Application.Bods;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Domain.Cms;
using SimHost.Infrastructure.Sql;

namespace SimHost.Personalities.Cms;

/// <summary>
/// Records asset installation and removal events — the receiving half of OIIE
/// Scenario 11.
///
/// CMS holds no reference data and no fixtures: everything it knows arrives by
/// publication. What it does keep is its own asset and location records, built up
/// from the events it receives, because a condition monitoring system that could
/// only answer questions by replaying an event log would not have a repository, it
/// would have an inbox.
///
/// It resolves nothing. The identifiers arrive from MMS, and until a registry
/// reconciles them CMS cannot tell whether the asset it just heard about is one it
/// already tracks under another name. As with MMS in uc01, leaving
/// <see cref="AssetInstallationEvent.Cirid"/> null makes that visible rather than
/// papering over it with a false match on the source identifier.
/// </summary>
public sealed class CmsAssetSegmentEventsHandler(
    ILogger<CmsAssetSegmentEventsHandler> logger) : IBodHandler
{
    public (string Verb, string Noun) Handles => ("Sync", "AssetSegmentEvents");

    public string? ParticipantId => CmsService.ParticipantId;

    public async Task<BodHandlingResult> HandleAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        BodEnvelope envelope,
        Guid messageId,
        CancellationToken ct)
    {
        var events = envelope.NounsAs(e => new AssetSegmentEvent(e));
        if (events.Count == 0)
        {
            return BodHandlingResult.Rejected("The BOD carried no asset segment events.");
        }

        var sourceParticipant = envelope.SenderLogicalId ?? "unknown";
        var recorded = 0;
        var nextLocation = await NextSequenceAsync<MonitoredLocationRecord>(
            db, r => r.LocationCode, ct);
        var nextAsset = await NextSequenceAsync<MonitoredAssetRecord>(
            db, r => r.AssetCode, ct);

        foreach (var evt in events)
        {
            // Idempotent on the event identity. Scenario 11 is a publication, and a
            // consumer that re-reads a message after a restart must not double-count
            // an installation — time-in-service figures would silently drift.
            var existing = await db.Set<AssetInstallationEvent>()
                .AnyAsync(e => e.FederationId == evt.UUID, ct);

            if (existing)
            {
                continue;
            }

            var attributes = ReadAttributes(evt);

            // The context the publisher asserted, carried as the CCOM RegistrationSite
            // of the segment the event names. Taken from the segment rather than the
            // asset because context is a property of where the work happened: an asset
            // moves between districts over its life, whereas a functional location does not.
            //
            // Captured as SourceId + IdInSource and never interpreted. The value is an
            // iTwin GUID when ENG published it, an OWNER_ID when MMS did, and CMS
            // recognises neither — relating it to a CMS owner code is the registry's
            // job, not this handler's.
            var site = evt.Segment?.RegistrationSite ?? evt.Asset?.RegistrationSite;

            var ownerSourceId = site?.InfoSource?.ShortName
                ?? site?.InfoSource?.FullName
                ?? sourceParticipant;

            var ownerIdInSource = site?.IDInInfoSource
                ?? site?.ShortName;

            // Resolved locally only when CMS already holds an owner whose CIRID matches
            // one the registry has related to this foreign identifier. Null until then,
            // which is the honest state: CMS has been told a context it cannot yet name.
            var ownerCode = await ResolveOwnerCodeAsync(db, ownerSourceId, ownerIdInSource, ct);

            var occurredAt = evt.EventDateTime?.ToDateTimeOffset();

            db.Set<AssetInstallationEvent>().Add(new AssetInstallationEvent
            {
                FederationId = evt.UUID,

                // Taken from the event type's own name, not inferred from the GUID.
                // An event type this system has never seen is still recorded under
                // whatever the publisher called it, rather than discarded for failing
                // to match one of two known identifiers.
                EventKind = evt.Type?.ShortName
                    ?? evt.Type?.IDInInfoSource
                    ?? evt.ShortName
                    ?? "Unknown",
                EventTypeId = evt.Type?.UUID ?? Guid.Empty,

                AssetFederationId = evt.Asset?.UUID ?? Guid.Empty,
                AssetIdInSource = evt.Asset?.IDInInfoSource,
                AssetSerialNumber = evt.Asset?.SerialNumber,
                AssetDesignation = evt.Asset?.FullName ?? evt.Asset?.ShortName,

                LocationFederationId = evt.Segment?.UUID ?? Guid.Empty,
                LocationIdInSource = evt.Segment?.IDInInfoSource,
                LocationDesignation = evt.Segment?.FullName ?? evt.Segment?.ShortName,

                OccurredAt = occurredAt,
                PerformedBy = attributes.GetValueOrDefault("sandbox:PerformedBy"),
                WorkOrderNumber = attributes.GetValueOrDefault("sandbox:WorkOrder"),
                SourceParticipant = sourceParticipant,
                OwnerCode = ownerCode,
                ForeignOwnerSourceId = ownerSourceId,
                ForeignOwnerIdInSource = ownerIdInSource,
                Cirid = null
            });

            // The event log records what happened; the records below are what CMS
            // knows as a result. Both are kept because they answer different
            // questions, and deriving one from the other on every read would make the
            // common question — what is fitted here now — the expensive one.
            var location = await UpsertLocationAsync(
                db, evt, sourceParticipant, ownerCode, ownerSourceId, ownerIdInSource, nextLocation, ct);

            if (location is not null && location.Id == 0)
            {
                nextLocation++;
            }

            var asset = await UpsertAssetAsync(
                db, evt, sourceParticipant, ownerCode, ownerSourceId, ownerIdInSource, nextAsset, ct);

            if (asset is not null && asset.Id == 0)
            {
                nextAsset++;
            }

            if (asset is not null)
            {
                // Install fits the asset to the location; anything else removes it.
                // Defaulting an unrecognised event type to removal would be worse
                // than the reverse: it would silently empty a location that is in
                // fact still occupied.
                var install = string.Equals(
                    evt.Type?.ShortName, "Install", StringComparison.OrdinalIgnoreCase);

                asset.InstalledAtLocationCode = install ? location?.LocationCode : null;
                asset.InstalledAt = install ? occurredAt : null;
                asset.UpdatedAt = DateTimeOffset.UtcNow;
            }

            db.Provenance.Add(new ProvenanceEntry
            {
                MessageId = messageId,
                EntityType = nameof(AssetInstallationEvent),
                EntityKey = evt.IDInInfoSource ?? evt.UUID.ToString(),
                Action = ProvenanceAction.Created,
                Actor = "system",
                ChangeSummary = JsonSerializer.Serialize(new
                {
                    eventKind = evt.Type?.ShortName,
                    asset = evt.Asset?.IDInInfoSource,
                    location = evt.Segment?.IDInInfoSource,
                    ownerAsserted = ownerIdInSource,
                    ownerSource = ownerSourceId,
                    ownerCode,
                    resolved = false
                })
            });

            recorded++;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "CMS recorded {Recorded} of {Count} asset event(s) from {Source}, " +
            "none resolved [{CorrelationId}]",
            recorded, events.Count, sourceParticipant, envelope.BodId);

        return BodHandlingResult.Applied(recorded, 0, 0);
    }

    /// <summary>
    /// The next free local code, one past the highest already issued.
    ///
    /// CMS assigns its own numbers rather than reusing the publisher's, because a
    /// local key that happens to equal a foreign one invites code elsewhere to treat
    /// them as interchangeable — which is the confusion the CIR exists to prevent.
    /// </summary>
    private static async Task<int> NextSequenceAsync<T>(
        ParticipantDbContext db,
        Expression<Func<T, string>> code,
        CancellationToken ct) where T : class
    {
        var codes = await db.Set<T>().AsNoTracking().Select(code).ToListAsync(ct);

        var highest = codes
            .Select(c => int.TryParse(c, out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();

        return highest + 1;
    }

    /// <summary>
    /// Records the functional location the event names, or updates what CMS already
    /// holds for it. Matched on the foreign identifier, which is all CMS has until a
    /// registry resolves the identity.
    /// </summary>
    private static async Task<MonitoredLocationRecord?> UpsertLocationAsync(
        ParticipantDbContext db,
        AssetSegmentEvent evt,
        string sourceParticipant,
        string? ownerCode,
        string? ownerSourceId,
        string? ownerIdInSource,
        int nextCode,
        CancellationToken ct)
    {
        if (evt.Segment is not { } segment)
        {
            return null;
        }

        var foreignId = segment.IDInInfoSource;
        var foreignSource = segment.InfoSource?.ShortName ?? sourceParticipant;

        if (string.IsNullOrWhiteSpace(foreignId))
        {
            return null;
        }

        var existing = await db.Set<MonitoredLocationRecord>()
            .FirstOrDefaultAsync(
                r => r.ForeignSourceId == foreignSource && r.ForeignIdInSource == foreignId,
                ct);

        if (existing is not null)
        {
            existing.Designation = segment.FullName ?? segment.ShortName ?? existing.Designation;
            existing.OwnerCode = ownerCode ?? existing.OwnerCode;
            existing.ForeignOwnerSourceId = ownerSourceId ?? existing.ForeignOwnerSourceId;
            existing.ForeignOwnerIdInSource = ownerIdInSource ?? existing.ForeignOwnerIdInSource;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            return existing;
        }

        var record = new MonitoredLocationRecord
        {
            LocationCode = nextCode.ToString(),
            FederationId = segment.UUID,
            Designation = segment.FullName ?? segment.ShortName,
            ForeignSourceId = foreignSource,
            ForeignIdInSource = foreignId,
            OwnerCode = ownerCode,
            ForeignOwnerSourceId = ownerSourceId,
            ForeignOwnerIdInSource = ownerIdInSource,
            Cirid = null
        };

        db.Set<MonitoredLocationRecord>().Add(record);
        return record;
    }

    /// <summary>
    /// Records the serialised asset the event names, or updates what CMS already
    /// holds for it. The installation state itself is set by the caller, which knows
    /// whether the event was an install or a removal.
    /// </summary>
    private static async Task<MonitoredAssetRecord?> UpsertAssetAsync(
        ParticipantDbContext db,
        AssetSegmentEvent evt,
        string sourceParticipant,
        string? ownerCode,
        string? ownerSourceId,
        string? ownerIdInSource,
        int nextCode,
        CancellationToken ct)
    {
        if (evt.Asset is not { } asset)
        {
            return null;
        }

        var foreignId = asset.IDInInfoSource;
        var foreignSource = asset.InfoSource?.ShortName ?? sourceParticipant;

        if (string.IsNullOrWhiteSpace(foreignId))
        {
            return null;
        }

        var existing = await db.Set<MonitoredAssetRecord>()
            .FirstOrDefaultAsync(
                r => r.ForeignSourceId == foreignSource && r.ForeignIdInSource == foreignId,
                ct);

        if (existing is not null)
        {
            existing.Designation = asset.FullName ?? asset.ShortName ?? existing.Designation;
            existing.SerialNumber = asset.SerialNumber ?? existing.SerialNumber;
            existing.OwnerCode = ownerCode ?? existing.OwnerCode;
            existing.ForeignOwnerSourceId = ownerSourceId ?? existing.ForeignOwnerSourceId;
            existing.ForeignOwnerIdInSource = ownerIdInSource ?? existing.ForeignOwnerIdInSource;
            return existing;
        }

        var record = new MonitoredAssetRecord
        {
            AssetCode = nextCode.ToString(),
            FederationId = asset.UUID,
            Designation = asset.FullName ?? asset.ShortName,
            SerialNumber = asset.SerialNumber,
            ForeignSourceId = foreignSource,
            ForeignIdInSource = foreignId,
            OwnerCode = ownerCode,
            ForeignOwnerSourceId = ownerSourceId,
            ForeignOwnerIdInSource = ownerIdInSource,
            Cirid = null
        };

        db.Set<MonitoredAssetRecord>().Add(record);
        return record;
    }

    /// <summary>
    /// Finds the CMS owner code for a context another system asserted.
    ///
    /// This is a local lookup, not a registry call. It can only succeed once a CIRID
    /// has been written onto a CMS owner row by the registration path — CMS matches
    /// the foreign identifier against owners it has already had related to it.
    /// Returning null when nothing matches is the point: an unresolved context must
    /// stay visibly unresolved rather than be guessed at from a string that happens
    /// to look familiar.
    /// </summary>
    private static async Task<string?> ResolveOwnerCodeAsync(
        ParticipantDbContext db,
        string? ownerSourceId,
        string? ownerIdInSource,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerIdInSource))
        {
            return null;
        }

        // The identity map is what the CIR resolution path writes into. If the foreign
        // identifier has been resolved before, its CIRID is here without a round trip.
        //
        // Invalidated and stale entries are excluded rather than used: a mapping the
        // registry has since corrected is exactly the case this cache exists to make
        // visible, and reading through it would reintroduce the stale answer.
        var now = DateTimeOffset.UtcNow;

        var mapped = await db.IdentityMap
            .AsNoTracking()
            .Where(m => m.ForeignSourceId == ownerSourceId
                && m.ForeignIdInSource == ownerIdInSource
                && !m.Invalidated
                && m.StaleAfter > now)
            .Select(m => m.Cirid)
            .FirstOrDefaultAsync(ct);

        if (mapped is not { } cirid)
        {
            return null;
        }

        return await db.Set<ContextOwnerRecord>()
            .AsNoTracking()
            .Where(o => o.Cirid == cirid)
            .Select(o => o.OwnerCode)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Flattens the event's attributes by reference-data key.
    ///
    /// Scenario 11's optional context — the agent and the work order — has no
    /// dedicated element on CCOM's AssetSegmentEvent, so it travels as attributes.
    /// </summary>
    private static Dictionary<string, string> ReadAttributes(AssetSegmentEvent evt)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var attribute in evt.Attribute)
        {
            var key = attribute.Type?.IDInInfoSource;
            var value = (attribute.ValueContent as TextContent)?.Text;

            if (key is { Length: > 0 } && value is { Length: > 0 })
            {
                result[key] = value;
            }
        }

        return result;
    }
}

public sealed class CmsService
{
    public const string ParticipantId = "cms";

    public const string SourceId = "CMS";
}
