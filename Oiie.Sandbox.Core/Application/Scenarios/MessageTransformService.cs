using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Domain.Eng;
using SimHost.Domain.Mms;
using SimHost.Domain.RegLocation;
using SimHost.Infrastructure.Blob;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Scenarios;

/// <summary>One labelled value from a participant's row.</summary>
public sealed record RecordField(string Label, string? Value);

/// <summary>
/// One row of a participant's own store, flattened to label/value pairs.
///
/// Deliberately not the entity itself: the point of the view is to let someone compare
/// an ENG Tag against an MMS LightSystemInventory side by side, and those types
/// share no base class or column names. Flattening is what makes them comparable.
/// </summary>
/// <param name="Absent">
/// True when the participant holds no row for this identity. Distinct from a row whose
/// fields are all empty, and worth showing: a receiver that recorded the message but
/// created nothing is the failure this view is most likely to be opened for.
/// </param>
public sealed record RecordView(
    string ParticipantId,
    string EntityType,
    string Key,
    Guid FederationId,
    IReadOnlyList<RecordField> Fields,
    bool Absent = false);

/// <summary>
/// What a message did, from the audit trail rather than inferred from the rows.
/// </summary>
public sealed record ProvenanceView(
    string ParticipantId,
    string EntityType,
    string EntityKey,
    ProvenanceAction Action,
    string Actor,
    string? ChangeSummary,
    DateTimeOffset At);

/// <summary>
/// The source record, the BOD it became, and the records it produced.
/// </summary>
/// <param name="Payload">
/// The BOD as it went over the wire, or null when the body was not retained. Null is
/// expected rather than exceptional: payload bodies live in Blob Storage, and a host
/// running without a storage account keeps the message row but discards the body.
/// </param>
public sealed record MessageTransform(
    MessageRecord Message,
    string RecordedBy,
    string? Payload,
    string? PayloadUnavailableReason,
    IReadOnlyList<Guid> Identities,
    IReadOnlyList<RecordView> Sources,
    IReadOnlyList<RecordView> Results,
    IReadOnlyList<ProvenanceView> Provenance);

/// <summary>
/// Assembles the before/wire/after view for a single message.
///
/// The three panels are correlated by FederationId rather than by any message-to-row
/// foreign key, because no such key exists and adding one would misrepresent the model:
/// participants are independent systems that share an identity, not rows in one
/// database. Reading the identities out of the BOD itself means the correlation is
/// performed the same way a real receiver would perform it.
/// </summary>
public sealed class MessageTransformService(
    IParticipantDbContextFactory participants,
    ParticipantRegistry registry,
    IPayloadStore payloads,
    ILogger<MessageTransformService> logger)
{
    public async Task<MessageTransform?> GetAsync(
        string recordedBy, Guid messageId, CancellationToken ct = default)
    {
        MessageRecord? message;

        try
        {
            await using var db = participants.Create(recordedBy);
            message = await db.Set<MessageRecord>()
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.MessageId == messageId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transform: {ParticipantId} could not be read.", recordedBy);
            return null;
        }

        if (message is null)
        {
            return null;
        }

        var (payload, unavailable) = await ReadPayloadAsync(message, ct);

        // Identities come from the payload when it is available. Without it there is
        // nothing to correlate on, so the view degrades to the message and its
        // provenance rather than guessing which rows were involved.
        var identities = payload is null ? [] : ExtractIdentities(payload);

        var sources = new List<RecordView>();
        var results = new List<RecordView>();

        if (identities.Count > 0)
        {
            var origin = await FindOriginAsync(message, recordedBy, ct);

            foreach (var participant in registry.All)
            {
                var views = await ReadRecordsAsync(participant.ParticipantId, identities, ct);

                if (participant.ParticipantId == origin)
                {
                    sources.AddRange(views);
                }
                else
                {
                    results.AddRange(views);
                }
            }
        }

        var provenance = await ReadProvenanceAsync(messageId, ct);

        return new MessageTransform(
            message, recordedBy, payload, unavailable, identities, sources, results, provenance);
    }

    /// <summary>
    /// The participant that published this BOD.
    ///
    /// An outbound record was written by its own sender, so that participant is the
    /// origin. An inbound record does not name who published it — ISBM delivers a body,
    /// not an author — so the sender is found by locating the outbound record carrying
    /// the same BodId. Returning null when no such record exists is honest: the message
    /// genuinely came from outside this run, and naming a source would be invention.
    /// </summary>
    private async Task<string?> FindOriginAsync(
        MessageRecord message, string recordedBy, CancellationToken ct)
    {
        if (message.Direction == MessageDirection.Outbound)
        {
            return recordedBy;
        }

        foreach (var participant in registry.All)
        {
            if (participant.ParticipantId == recordedBy)
            {
                continue;
            }

            try
            {
                await using var db = participants.Create(participant.ParticipantId);

                var sent = await db.Set<MessageRecord>()
                    .AsNoTracking()
                    .AnyAsync(
                        m => m.BodId == message.BodId &&
                             m.Direction == MessageDirection.Outbound, ct);

                if (sent)
                {
                    return participant.ParticipantId;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "Transform: {ParticipantId} outbound unreadable.",
                    participant.ParticipantId);
            }
        }

        return null;
    }

    private async Task<(string? Payload, string? Reason)> ReadPayloadAsync(
        MessageRecord message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.ContentRef))
        {
            return (null, "No payload reference was recorded for this message.");
        }

        // The null store writes a sentinel rather than a path, so the reason can be
        // reported precisely instead of as a generic read failure.
        if (message.ContentRef.StartsWith("unstored:", StringComparison.OrdinalIgnoreCase))
        {
            return (null,
                "The body was not retained: this host is running without blob storage. " +
                "Set Storage:BlobServiceUri to capture BOD payloads.");
        }

        try
        {
            var xml = await payloads.ReadAsync(message.ContentRef, ct);

            return xml is null
                ? (null, $"The payload reference '{message.ContentRef}' no longer resolves.")
                : (Format(xml), null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transform: payload {ContentRef} unreadable.", message.ContentRef);
            return (null, $"The payload could not be read: {ex.Message}");
        }
    }

    /// <summary>Reformats for display; falls back to the raw text if it will not parse.</summary>
    private static string Format(string xml)
    {
        try
        {
            return XDocument.Parse(xml).ToString();
        }
        catch (System.Xml.XmlException)
        {
            return xml;
        }
    }

    /// <summary>
    /// Pulls the segment identities out of the BOD.
    ///
    /// Matches on local name and ignores namespaces so the same code works for the CCOM
    /// payload and its OAGIS envelope. Only well-formed non-empty GUIDs are kept, since
    /// a malformed one correlates to nothing and would otherwise show as a phantom row.
    /// </summary>
    private static List<Guid> ExtractIdentities(string xml)
    {
        try
        {
            return [.. XDocument.Parse(xml)
                .Descendants()
                .Where(e => e.Name.LocalName == "UUID")
                .Select(e => Guid.TryParse(e.Value?.Trim(), out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()];
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads a participant's own rows for the given identities.
    ///
    /// Public because the run-detail data tab needs exactly the same projection: proving
    /// the tag reached MMS means reading the MMS schema, and a second reader would be a
    /// second definition of what an MMS record looks like.
    /// </summary>
    /// <param name="includeAbsent">
    /// When false the absence placeholder is omitted, for callers that group by
    /// participant themselves and would otherwise render an empty card per identity.
    /// </param>
    public async Task<List<RecordView>> ReadRecordsAsync(
        string participantId,
        IReadOnlyList<Guid> identities,
        CancellationToken ct,
        bool includeAbsent = true)
    {
        var views = new List<RecordView>();

        try
        {
            await using var db = participants.Create(participantId);

            switch (participantId)
            {
                case "eng":
                    var tags = await db.Set<Tag>().AsNoTracking()
                        .Where(t => identities.Contains(t.FederationId))
                        .ToListAsync(ct);

                    views.AddRange(tags.Select(t => new RecordView(
                        participantId, nameof(Tag), t.TagNumber, t.FederationId,
                        new RecordField[]
                        {
                            new("Tag number", t.TagNumber),
                            new("Service description", t.ServiceDescription),
                            new("Class", t.ClassKey),
                            new("Unit", t.UnitNumber),
                            new("Discipline", t.DisciplineCode),
                            new("P&ID", t.PidReference),
                            new("Range minimum", t.RangeMinimum?.ToString()),
                            new("Range maximum", t.RangeMaximum?.ToString()),
                            new("Control action", t.ControlAction),
                            new("Maturity", t.Maturity.ToString())
                        })));
                    break;

                case "reg-location":
                    var locations = await db.Set<Location>().AsNoTracking()
                        .Where(l => identities.Contains(l.FederationId))
                        .ToListAsync(ct);

                    views.AddRange(locations.Select(l => new RecordView(
                        participantId, nameof(Location), l.LocationCode, l.FederationId,
                        new RecordField[]
                        {
                            new("Location code", l.LocationCode),
                            new("Name", l.Name),
                            new("Description", l.Description),
                            new("Class", l.ClassKey),
                            new("Requested class", l.RequestedClassKey),
                            new("Area", l.Area),
                            new("Source participant", l.SourceParticipant),
                            new("Source identifier", l.SourceIdentifier)
                        })));
                    break;

                case "mms":
                    // MMS holds no FederationId, so it cannot be filtered on one
                    // directly. The code assignments recorded at ingest are the only
                    // local bridge from a shared identity to a LIGHT_SYSTEM_ID; the
                    // durable version of the same link lives in ws-CIR.
                    var codes = await db.Codes.AsNoTracking()
                        .Where(c => c.ParticipantId == "mms" && identities.Contains(c.FederationId))
                        .Select(c => new { c.FederationId, c.Code })
                        .ToListAsync(ct);

                    if (codes.Count == 0) break;

                    var keys = codes.Select(c => c.Code).ToList();

                    var rows = await db.Set<LightSystemInventory>().AsNoTracking()
                        .Where(r => keys.Contains(r.LightSystemId.ToString()))
                        .ToListAsync(ct);

                    var classNames = await db.Set<LightSystemClassCode>().AsNoTracking()
                        .ToDictionaryAsync(c => c.LightSystemClassCodeId, c => c.LightSystemClassCodeName, ct);

                    var statusNames = await db.Set<SetupAssetStatus>().AsNoTracking()
                        .ToDictionaryAsync(s => s.AssetStatusId, s => s.AssetStatusName, ct);

                    var ownerNames = await db.Set<SetupOwner>().AsNoTracking()
                        .ToDictionaryAsync(o => o.OwnerId, o => o.OwnerName, ct);

                    views.AddRange(rows.Select(r => new RecordView(
                        participantId, nameof(LightSystemInventory),
                        r.LightSystemId.ToString(),
                        codes.First(c => c.Code == r.LightSystemId.ToString()).FederationId,
                        new RecordField[]
                        {
                            new("LIGHT_SYSTEM_ID", r.LightSystemId.ToString()),
                            new("LIGHT_SYSTEM_NAME", r.LightSystemName),
                            new("Class code", classNames.GetValueOrDefault(r.LightSystemClassCodeId)),
                            new("Status", r.LightSystemStatusId is { } s
                                ? statusNames.GetValueOrDefault(s)
                                : null),

                            // Null is shown as an explicit absence rather than blank.
                            // An unowned light system cannot resolve to any iTwin, and
                            // that is worth seeing rather than glossing over.
                            new("Owner", r.OwnerId is { } o
                                ? ownerNames.GetValueOrDefault(o)
                                : "no owner")
                        })));
                    break;
            }

            views = await WithPropertiesAsync(db, views, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transform: {ParticipantId} records unreadable.", participantId);
        }

        // A participant with nothing for these identities is reported explicitly. The
        // interesting case for this view is a BOD that arrived and produced no row, and
        // silently omitting the participant would hide exactly that.
        if (views.Count == 0 && includeAbsent)
        {
            views.Add(new RecordView(
                participantId, string.Empty, string.Empty, Guid.Empty, [], Absent: true));
        }

        return views;
    }

    /// <summary>
    /// Appends the classified property values held against each row.
    ///
    /// These live in EntityPropertyValue rather than on the entity, because a tag's
    /// property set is decided by its class and is not enumerable as columns: a valve
    /// carries seat material, a controller carries control action, and neither is known
    /// when the table is defined. Reading only the entity therefore shows the spine and
    /// silently omits everything the class contributed — which is why a value such as
    /// rdl:ControlAction looks lost when it is in fact stored one table over.
    ///
    /// Values that arrived without a local definition are marked, not hidden: retained
    /// but unmapped is a distinct state from absent, and the model never discards.
    /// </summary>
    private async Task<List<RecordView>> WithPropertiesAsync(
        ParticipantDbContext db, List<RecordView> views, CancellationToken ct)
    {
        if (views.Count == 0)
        {
            return views;
        }

        var keys = views.Select(v => v.Key).ToList();
        var types = views.Select(v => v.EntityType).Distinct().ToList();

        // The registry ingests against the proposal, keyed by the sender's identifier,
        // and carries the values onto the Location at approval. Both keys are queried so
        // the values still appear for a location approved before that carry existed.
        var sourceKeys = views
            .Select(v => v.Fields.FirstOrDefault(f => f.Label == "Source identifier")?.Value)
            .Where(v => v is { Length: > 0 })
            .Select(v => v!)
            .ToList();

        var values = await db.PropertyValues
            .AsNoTracking()
            .Where(v => (types.Contains(v.EntityType) && keys.Contains(v.EntityKey))
                || (v.EntityType == "StewardshipItem" && sourceKeys.Contains(v.EntityKey)))
            .Where(v => v.ValidTo == null)
            .ToListAsync(ct);

        if (values.Count == 0)
        {
            return views;
        }

        var definitions = await db.PropertyDefinitions
            .AsNoTracking()
            .ToDictionaryAsync(d => d.Id, ct);

        var enriched = new List<RecordView>();

        foreach (var view in views)
        {
            var sourceKey = view.Fields
                .FirstOrDefault(f => f.Label == "Source identifier")?.Value;

            var mine = values
                .Where(v => (v.EntityType == view.EntityType && v.EntityKey == view.Key)
                    || (v.EntityType == "StewardshipItem"
                        && sourceKey is { Length: > 0 }
                        && v.EntityKey == sourceKey))

                // A value carried onto the entity supersedes the proposal copy it came
                // from, so the same property is not listed twice.
                .GroupBy(v => v.DefinitionId)
                .Select(g => g.FirstOrDefault(v => v.EntityType == view.EntityType) ?? g.First())
                .ToList();

            if (mine.Count == 0)
            {
                enriched.Add(view);
                continue;
            }

            var fields = new List<RecordField>(view.Fields);

            foreach (var value in mine)
            {
                definitions.TryGetValue(value.DefinitionId, out var definition);

                // Falling back to the raw definition id rather than skipping: a value
                // whose definition did not travel is still evidence that it arrived.
                var label = definition?.Name is { Length: > 0 } name
                    ? name
                    : definition?.DefinitionKey ?? value.DefinitionId.ToString();

                var text = value.CharacterValue
                    ?? value.CodeValue
                    ?? value.NumericValue?.ToString()
                    ?? value.BooleanValue?.ToString()
                    ?? value.DateTimeValue?.ToString();

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(value.UnitOfMeasure))
                {
                    text = $"{text} {value.UnitOfMeasure}";
                }

                if (!value.Mapped)
                {
                    text = $"{text}  (retained, no local definition)";
                }

                if (value.Orphaned)
                {
                    text = $"{text}  (orphaned by reclassification)";
                }

                fields.Add(new RecordField(label, text));
            }

            enriched.Add(view with { Fields = fields });
        }

        return enriched;
    }

    private async Task<List<ProvenanceView>> ReadProvenanceAsync(
        Guid messageId, CancellationToken ct)
    {
        var entries = new List<ProvenanceView>();

        foreach (var participant in registry.All)
        {
            try
            {
                await using var db = participants.Create(participant.ParticipantId);

                var rows = await db.Provenance
                    .AsNoTracking()
                    .Where(p => p.MessageId == messageId)
                    .ToListAsync(ct);

                entries.AddRange(rows.Select(p => new ProvenanceView(
                    participant.ParticipantId, p.EntityType, p.EntityKey,
                    p.Action, p.Actor, p.ChangeSummary, p.At)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "Transform: {ParticipantId} provenance unreadable.",
                    participant.ParticipantId);
            }
        }

        return [.. entries.OrderBy(e => e.At)];
    }
}
