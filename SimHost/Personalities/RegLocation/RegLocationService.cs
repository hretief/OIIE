using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using SimHost.Application.Bods;
using SimHost.Application.Classification;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Domain.RegLocation;
using SimHost.Infrastructure.Sql;

namespace SimHost.Personalities.RegLocation;

/// <summary>
/// Ingests SyncSegments into REG-LOCATION's review queue.
///
/// Arrival is not acceptance. Incoming segments become StewardshipItem rows for a
/// steward to approve; nothing enters the authoritative model until then, and
/// nothing is republished. That gate is what makes REG-LOCATION a registry rather
/// than a relay.
/// </summary>
public sealed class SyncSegmentsHandler(
    CcomAttributeMapperFactory mappers,
    ILogger<SyncSegmentsHandler> logger) : IBodHandler
{
    public (string Verb, string Noun) Handles => ("Sync", "Segments");

    public string? ParticipantId => RegLocationService.ParticipantId;

    /// <summary>
    /// The leaf-first ancestor chain, when the sender supplied one. Space-separated
    /// so it survives as a single property value rather than needing a repeating
    /// structure the receiver may not model.
    /// </summary>
    private static List<string>? ReadClassChain(IReadOnlyList<IncomingProperty> properties)
    {
        var value = properties
            .FirstOrDefault(p => p.DefinitionKey == "sandbox:ClassChain")?
            .CharacterValue;

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public async Task<BodHandlingResult> HandleAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        BodEnvelope envelope,
        Guid messageId,
        CancellationToken ct)
    {
        var segments = envelope.NounsAs(e => new Segment(e));
        if (segments.Count == 0)
        {
            return BodHandlingResult.Rejected("The BOD carried no segments.");
        }

        var mapper = mappers.For(participant);
        var mapped = 0;
        var unmapped = 0;

        // Inferred definitions are reconciled across the whole BOD, not per segment.
        //
        // The ingestor mints a definition for every unrecognised property it meets,
        // so two segments carrying the same eng: field produce two definitions with
        // the same key — and DefinitionKey is uniquely indexed. The same happens
        // across messages, because a definition saved by an earlier BOD is not in the
        // in-memory snapshot the ingestor consults.
        //
        // Both cases are the same reconciliation: one definition per key, and every
        // value pointed at it.
        var inferredByKey = new Dictionary<string, PropertyDefinition>(StringComparer.Ordinal);
        var definitionRemap = new Dictionary<Guid, Guid>();
        var pendingValues = new List<EntityPropertyValue>();

        foreach (var segment in segments)
        {
            var sourceParticipant = segment.InfoSource?.ShortName ?? envelope.SenderLogicalId ?? "unknown";
            var sourceIdentifier = segment.IDInInfoSource;

            if (string.IsNullOrWhiteSpace(sourceIdentifier))
            {
                logger.LogWarning("Segment with no IDInInfoSource skipped [{CorrelationId}]", envelope.BodId);
                continue;
            }

            // Bind the class the sender named, falling back to the nearest ancestor
            // this participant holds. A leaf class we have never seen still yields a
            // usable record rather than a rejection.
            var (incoming, _) = mapper.Extract(segment);

            // Bind against the sender's ancestor chain where it supplied one.
            // A bare leaf key can only bind exactly or not at all, so a receiver
            // holding a smaller subset of the library would reject data it could
            // perfectly well understand at a coarser class.
            var requestedClassKey = segment.Type?.IDInInfoSource;
            var chain = ReadClassChain(incoming) ?? (requestedClassKey is null ? [] : [requestedClassKey]);

            var binding = chain.Count == 0 ? null : participant.Binder.Bind(chain);

            // Properties are ingested against the bound class's effective set.
            // Anything outside it is retained and flagged, never discarded.
            var effective = binding?.BoundClass is null
                ? EffectivePropertySet.Empty
                : participant.Resolver.Compose(
                    participant.Resolver.BuildTaxonomyChain(binding.BoundClass.Id), []);

            // Excluded from ingestion: the chain describes how to interpret the
            // segment, not something true about the location. Retaining it as an
            // unmapped property would misreport transport metadata as data the
            // sender expected us to keep.
            var ingestible = incoming
                .Where(p => !p.DefinitionKey.StartsWith("sandbox:", StringComparison.Ordinal))
                .ToList();

            var ingestion = participant.Ingestor.Ingest(
                nameof(StewardshipItem),
                sourceIdentifier,
                ingestible,
                effective,
                sourceParticipant,
                messageId,
                DateTimeOffset.UtcNow);

            mapped += ingestion.MappedCount;
            unmapped += ingestion.UnmappedCount;

            var existing = await db.Set<StewardshipItem>()
                .FirstOrDefaultAsync(s =>
                    s.SourceParticipant == sourceParticipant
                    && s.SourceIdentifier == sourceIdentifier
                    && s.State == StewardshipState.Proposed, ct);

            if (existing is not null)
            {
                // A resend before the steward has decided replaces the proposal
                // rather than queueing a second one.
                existing.ProposedName = segment.ShortName ?? segment.FullName;
                existing.ProposedDescription = segment.Description;
                existing.RequestedClassKey = requestedClassKey;
                existing.BoundClassKey = binding?.BoundClass?.ClassKey;
                existing.ClassDegraded = binding?.IsDegraded ?? false;
                existing.PropertiesMapped = ingestion.MappedCount;
                existing.PropertiesUnmapped = ingestion.UnmappedCount;
                existing.SourceMessageId = messageId;
                continue;
            }

            db.Set<StewardshipItem>().Add(new StewardshipItem
            {
                SourceMessageId = messageId,
                SourceParticipant = sourceParticipant,
                SourceIdentifier = sourceIdentifier,
                ProposedName = segment.ShortName ?? segment.FullName,
                ProposedDescription = segment.Description,
                RequestedClassKey = requestedClassKey,
                BoundClassKey = binding?.BoundClass?.ClassKey,
                ClassDegraded = binding?.IsDegraded ?? false,
                PropertiesMapped = ingestion.MappedCount,
                PropertiesUnmapped = ingestion.UnmappedCount
            });

            db.Provenance.Add(new ProvenanceEntry
            {
                MessageId = messageId,
                EntityType = nameof(StewardshipItem),
                EntityKey = sourceIdentifier,
                Action = ProvenanceAction.Created,
                Actor = "system",
                ChangeSummary = JsonSerializer.Serialize(new
                {
                    sourceParticipant,
                    requestedClassKey,
                    boundClassKey = binding?.BoundClass?.ClassKey,
                    degraded = binding?.IsDegraded ?? false,
                    mapped = ingestion.MappedCount,
                    unmapped = ingestion.UnmappedCount
                })
            });

            // Definitions inferred from the wire are persisted so the same property
            // arriving again is recognised as the same unknown thing, and so the
            // unmapped panel can name where each came from. Reconciled below.
            foreach (var definition in ingestion.InferredDefinitions)
            {
                if (inferredByKey.TryGetValue(definition.DefinitionKey, out var first))
                {
                    definitionRemap[definition.Id] = first.Id;
                }
                else
                {
                    inferredByKey[definition.DefinitionKey] = definition;
                }
            }

            pendingValues.AddRange(ingestion.Values);
        }

        foreach (var (key, definition) in inferredByKey)
        {
            var existing = await db.PropertyDefinitions
                .FirstOrDefaultAsync(d => d.DefinitionKey == key, ct);

            if (existing is null)
            {
                db.PropertyDefinitions.Add(definition);
            }
            else
            {
                // Already known from an earlier message. Point this BOD's values at
                // it rather than inserting a second definition for the same key.
                definitionRemap[definition.Id] = existing.Id;
            }
        }

        foreach (var value in pendingValues)
        {
            if (definitionRemap.TryGetValue(value.DefinitionId, out var canonical))
            {
                value.DefinitionId = canonical;
            }
        }

        db.PropertyValues.AddRange(pendingValues);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "REG-LOCATION queued {Count} segment(s) for stewardship [{CorrelationId}]",
            segments.Count, envelope.BodId);

        return BodHandlingResult.Applied(segments.Count, mapped, unmapped);
    }
}

public sealed record ApprovalResult(
    int Approved, int Rejected, IReadOnlyList<string> LocationCodes, string? CorrelationId);

/// <summary>
/// REG-LOCATION's release event.
///
/// Approving admits proposals to the authoritative model, assigns registry
/// identifiers, and republishes to the O&amp;M channel. The registry deliberately
/// does not adopt the source's identifier — an ENG tag becomes LOC-000412 here,
/// which is what creates the identity problem the CIR exists to solve.
/// </summary>
public sealed class RegLocationService(
    IParticipantDbContextFactory factory,
    ILogger<RegLocationService> logger)
{
    public const string ParticipantId = "reg-location";

    public async Task<IReadOnlyList<StewardshipItem>> GetQueueAsync(CancellationToken ct = default)
    {
        await using var db = factory.Create(ParticipantId);

        return await db.Set<StewardshipItem>()
            .Where(s => s.State == StewardshipState.Proposed)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<ApprovalResult> ApproveAllAsync(
        string channelUri, string? topic, string decidedBy, CancellationToken ct = default)
    {
        await using var db = factory.Create(ParticipantId);

        var proposals = await db.Set<StewardshipItem>()
            .Where(s => s.State == StewardshipState.Proposed)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

        if (proposals.Count == 0)
        {
            return new ApprovalResult(0, 0, [], null);
        }

        var nextSequence = await NextSequenceAsync(db, ct);
        var codes = new List<string>();
        var correlationId = Guid.NewGuid().ToString();

        foreach (var proposal in proposals)
        {
            var existing = await db.Set<Location>().FirstOrDefaultAsync(l =>
                l.SourceParticipant == proposal.SourceParticipant
                && l.SourceIdentifier == proposal.SourceIdentifier, ct);

            var code = existing?.LocationCode ?? $"LOC-{nextSequence++:D6}";

            if (existing is null)
            {
                db.Set<Location>().Add(new Location
                {
                    LocationCode = code,
                    Name = proposal.ProposedName,
                    Description = proposal.ProposedDescription,
                    ClassKey = proposal.BoundClassKey,
                    // Retained whenever it differs from what was bound, which
                    // includes the case where nothing could be bound at all.
                    // Discarding it there would lose the sender's classification
                    // entirely — the same mistake as dropping an unmapped property,
                    // and for the same reason it must not happen.
                    RequestedClassKey =
                        string.Equals(proposal.RequestedClassKey, proposal.BoundClassKey, StringComparison.Ordinal)
                            ? null
                            : proposal.RequestedClassKey,
                    SourceParticipant = proposal.SourceParticipant,
                    SourceIdentifier = proposal.SourceIdentifier
                });
            }
            else
            {
                existing.Name = proposal.ProposedName;
                existing.Description = proposal.ProposedDescription;
                existing.ClassKey = proposal.BoundClassKey;
                existing.RequestedClassKey =
                    string.Equals(proposal.RequestedClassKey, proposal.BoundClassKey, StringComparison.Ordinal)
                        ? null
                        : proposal.RequestedClassKey;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }

            proposal.State = StewardshipState.Approved;
            proposal.DecidedBy = decidedBy;
            proposal.DecidedAt = DateTimeOffset.UtcNow;
            proposal.LocationCode = code;

            db.Provenance.Add(new ProvenanceEntry
            {
                MessageId = proposal.SourceMessageId,
                EntityType = nameof(Location),
                EntityKey = code,
                Action = existing is null ? ProvenanceAction.Created : ProvenanceAction.Updated,
                Actor = decidedBy,
                ChangeSummary = JsonSerializer.Serialize(new
                {
                    fromSource = proposal.SourceParticipant,
                    sourceIdentifier = proposal.SourceIdentifier
                })
            });

            codes.Add(code);
        }

        // Approval and republication intent commit together, as everywhere else.
        db.Outbox.Add(new OutboxItem
        {
            ContainerType = "StewardshipApproval",
            ContainerKey = $"{decidedBy}@{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}",
            EntityType = nameof(Location),
            EntityKeys = JsonSerializer.Serialize(codes),
            ChangeKind = ChangeKind.Add,
            Verb = "Sync",
            Noun = "Segments",
            Pattern = MessagePattern.Publication,
            ChannelUri = channelUri,
            Topic = topic,
            CorrelationId = correlationId,
            State = OutboxState.Pending
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "REG-LOCATION approved {Count} location(s), republishing [{CorrelationId}]",
            codes.Count, correlationId);

        return new ApprovalResult(codes.Count, 0, codes, correlationId);
    }

    public async Task<ApprovalResult> RejectAllAsync(
        string reason, string decidedBy, CancellationToken ct = default)
    {
        await using var db = factory.Create(ParticipantId);

        var proposals = await db.Set<StewardshipItem>()
            .Where(s => s.State == StewardshipState.Proposed)
            .ToListAsync(ct);

        foreach (var proposal in proposals)
        {
            proposal.State = StewardshipState.Rejected;
            proposal.DecidedBy = decidedBy;
            proposal.DecidedAt = DateTimeOffset.UtcNow;
            proposal.RejectReason = reason;

            db.Provenance.Add(new ProvenanceEntry
            {
                MessageId = proposal.SourceMessageId,
                EntityType = nameof(StewardshipItem),
                EntityKey = proposal.SourceIdentifier,
                Action = ProvenanceAction.Rejected,
                Actor = decidedBy,
                ChangeSummary = reason
            });
        }

        await db.SaveChangesAsync(ct);
        return new ApprovalResult(0, proposals.Count, [], null);
    }

    /// <summary>
    /// Next value in the registry's own key series. Derived from existing rows
    /// rather than an identity column so the codes stay legible and stable across
    /// a reset, which matters when they appear in demo narration.
    /// </summary>
    private static async Task<int> NextSequenceAsync(ParticipantDbContext db, CancellationToken ct)
    {
        var codes = await db.Set<Location>().Select(l => l.LocationCode).ToListAsync(ct);

        var highest = codes
            .Select(c => int.TryParse(c.Split('-').Last(), out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return highest + 1;
    }
}
