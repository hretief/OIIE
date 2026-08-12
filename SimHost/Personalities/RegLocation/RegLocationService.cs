using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using SimHost.Application.Bods;
using SimHost.Application.Classification;
using SimHost.Application.Identity;
using SimHost.Application.Participants;
using SimHost.Application.Scenarios;
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

            // The identity the sender asserted. Carried through the gate untouched:
            // the steward decides whether to accept the entity, not what it is.
            var federationId = segment.UUID;

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
                existing.FederationId = federationId;
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
                FederationId = federationId,
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

/// <summary>
/// Ingests SyncSegmentMeshConnections into REG-LOCATION's authoritative model.
///
/// Connections do not queue for stewardship of their own. The steward's decision is
/// about whether a thing belongs in the registry; an edge between two locations
/// already approved adds no new thing to decide about, and holding it for a second
/// approval would mean the registry knew both ends were real yet declined to say how
/// they relate.
///
/// But an edge is published alongside the segments it connects, so it routinely
/// arrives while both ends are still proposals. That edge is retained unresolved
/// rather than rejected: the sender has asserted the relationship and cannot observe
/// the approval that would make it storable, so rejecting it would make the sender
/// responsible for a republication it has no way to know is needed. The endpoints
/// are resolved to location codes when the steward approves, which is the moment the
/// codes first exist.
///
/// What is still rejected is an edge naming an end the registry has never heard of
/// in any state — not a proposal, not a location. That is a genuine sender error and
/// stays visible as one.
/// </summary>
public sealed class SyncSegmentConnectionsHandler(
    ILogger<SyncSegmentConnectionsHandler> logger) : IBodHandler
{
    public (string Verb, string Noun) Handles => ("Sync", "SegmentMeshConnections");

    public string? ParticipantId => RegLocationService.ParticipantId;

    public async Task<BodHandlingResult> HandleAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        BodEnvelope envelope,
        Guid messageId,
        CancellationToken ct)
    {
        var meshes = envelope.NounsAs(e => new SegmentMesh(e));

        // The mesh is unwrapped and discarded: it is the wire envelope CCOM requires
        // for connections, not something the registry models.
        var connections = meshes.SelectMany(m => m.Connection).ToList();

        if (connections.Count == 0)
        {
            return BodHandlingResult.Rejected("The BOD carried no connections.");
        }

        var sourceParticipant = envelope.SenderLogicalId ?? "unknown";
        var applied = 0;
        var pending = 0;
        var unknown = new List<string>();

        foreach (var connection in connections)
        {
            var fromIdentifier = connection.From?.IDInInfoSource;
            var toIdentifier = connection.To?.IDInInfoSource;

            if (string.IsNullOrWhiteSpace(fromIdentifier) || string.IsNullOrWhiteSpace(toIdentifier))
            {
                unknown.Add($"{fromIdentifier ?? "?"} -> {toIdentifier ?? "?"}");
                continue;
            }

            var fromCode = await ResolveAsync(db, connection.From, ct);
            var toCode = await ResolveAsync(db, connection.To, ct);

            // Approved already, or still a proposal, or genuinely unheard of. Only the
            // last is the sender's mistake; the middle one is the ordinary case of an
            // edge travelling with the segments it connects.
            if (fromCode is null && !await IsKnownAsync(db, fromIdentifier, ct)
                || toCode is null && !await IsKnownAsync(db, toIdentifier, ct))
            {
                unknown.Add($"{fromIdentifier} -> {toIdentifier}");
                continue;
            }

            var federationId = connection.UUID;

            var existing = federationId != Guid.Empty
                ? await db.Set<LocationConnection>()
                    .FirstOrDefaultAsync(c => c.FederationId == federationId, ct)
                : await db.Set<LocationConnection>().FirstOrDefaultAsync(
                    c => c.SourceParticipant == sourceParticipant
                        && c.FromSourceIdentifier == fromIdentifier
                        && c.ToSourceIdentifier == toIdentifier
                        && c.TypeKey == connection.Type!.IDInInfoSource, ct);

            var edge = existing ?? new LocationConnection
            {
                FederationId = federationId,
                SourceParticipant = sourceParticipant
            };

            edge.FromSourceIdentifier = fromIdentifier;
            edge.ToSourceIdentifier = toIdentifier;
            edge.FromLocationCode = fromCode;
            edge.ToLocationCode = toCode;
            edge.IsResolved = fromCode is not null && toCode is not null;
            edge.TypeKey = connection.Type?.IDInInfoSource ?? string.Empty;
            // The sender's own reading of the edge, kept so the registry can render it
            // from either end without holding the sender's relationship vocabulary.
            edge.ForwardRole = connection.Type?.ShortName;
            edge.InverseRole = connection.Type?.Description;
            edge.Order = int.TryParse(connection.Order, out var order) ? order : null;
            edge.UpdatedAt = DateTimeOffset.UtcNow;

            if (existing is null)
            {
                db.Set<LocationConnection>().Add(edge);
            }

            db.Provenance.Add(new ProvenanceEntry
            {
                MessageId = messageId,
                EntityType = nameof(LocationConnection),
                EntityKey = edge.IsResolved ? $"{fromCode}->{toCode}" : $"{fromIdentifier}->{toIdentifier}",
                Action = existing is null ? ProvenanceAction.Created : ProvenanceAction.Updated,
                Actor = sourceParticipant,
                ChangeSummary = JsonSerializer.Serialize(new
                {
                    from = connection.From?.IDInInfoSource,
                    to = connection.To?.IDInInfoSource,
                    typeKey = edge.TypeKey,
                    edge.ForwardRole,
                    edge.InverseRole
                })
            });

            applied++;

            if (!edge.IsResolved)
            {
                pending++;
            }
        }

        await db.SaveChangesAsync(ct);

        if (unknown.Count > 0)
        {
            logger.LogWarning(
                "REG-LOCATION rejected {Count} connection(s) naming locations it has never seen: {Edges} [{CorrelationId}]",
                unknown.Count, string.Join("; ", unknown), envelope.BodId);
        }

        logger.LogInformation(
            "REG-LOCATION accepted {Count} connection(s), {Pending} awaiting approval of their endpoints [{CorrelationId}]",
            applied, pending, envelope.BodId);

        // Partial acceptance is still a rejection to report: the sender asserted edges
        // the registry could not store at all, and a clean result would hide that.
        // Edges merely waiting on approval are not in that set — they were stored.
        return unknown.Count > 0
            ? BodHandlingResult.Rejected(
                $"Accepted {applied} connection(s); rejected {unknown.Count} naming unknown locations: " +
                string.Join("; ", unknown))
            : BodHandlingResult.Applied(applied, 0, 0);
    }

    /// <summary>
    /// Whether the registry has heard of this identifier at all, in any state.
    ///
    /// Separate from <see cref="ResolveAsync"/> because the two answer different
    /// questions: that one asks "can I state an edge about this yet", this one asks
    /// "is the sender talking about something real". A proposal answers no to the
    /// first and yes to the second, and conflating them is what made an edge
    /// published with its own segments look like a sender error.
    /// </summary>
    private static async Task<bool> IsKnownAsync(
        ParticipantDbContext db, string identifier, CancellationToken ct) =>
        await db.Set<StewardshipItem>().AnyAsync(s => s.SourceIdentifier == identifier, ct)
        || await db.Set<Location>().AnyAsync(l => l.SourceIdentifier == identifier, ct);

    /// <summary>
    /// The local code for an endpoint the sender named.
    ///
    /// Resolved by the asserted identity first and the sender's identifier second:
    /// the identity is the federation's answer to "same thing", and the identifier
    /// only works while the sender is the one who registered it.
    /// </summary>
    private static async Task<string?> ResolveAsync(
        ParticipantDbContext db, Segment? endpoint, CancellationToken ct)
    {
        if (endpoint is null)
        {
            return null;
        }

        if (endpoint.UUID != Guid.Empty)
        {
            var byIdentity = await db.Set<Location>()
                .FirstOrDefaultAsync(l => l.FederationId == endpoint.UUID, ct);

            if (byIdentity is not null)
            {
                return byIdentity.LocationCode;
            }
        }

        var identifier = endpoint.IDInInfoSource;
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var bySource = await db.Set<Location>()
            .FirstOrDefaultAsync(l => l.SourceIdentifier == identifier, ct);

        return bySource?.LocationCode;
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
    ITagIdentityService identities,
    ScenarioRunContext runContext,
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
                // Adopt what the sender asserted. Issuing a fresh identity here would
                // make LOC-000412 a different thing from the tag it was proposed from,
                // which is precisely the duplicate the federation model prevents. The
                // registry mints only when nothing was asserted — a legacy sender —
                // and then it is genuinely originating the identity.
                var adopted = proposal.FederationId != Guid.Empty;
                var federationId = adopted ? proposal.FederationId : identities.Mint();

                db.Set<Location>().Add(new Location
                {
                    FederationId = federationId,
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

                // LOC-000412 becomes an additional code for the same identity. The
                // sender's code remains equally valid — this is the "same tag, other
                // code" case, and CIR is what lets either one resolve.
                var assignment = identities.RegisterCode(federationId, ParticipantId, code);
                assignment.AdoptedFromRemote = adopted;
                db.Codes.Add(assignment);
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

            // Property values were ingested against the proposal, keyed by the sender's
            // identifier. Approval is what turns a proposal into a Location, so the values
            // have to follow it onto the entity that now represents the thing: leaving
            // them behind means the registry holds them but neither shows nor republishes
            // them, and everything ENG sent beyond the CCOM spine dies at the gate.
            //
            // Copied rather than moved, because the proposal is the record of what was
            // received and rewriting its values would erase the evidence of what arrived.
            await CarryPropertiesAsync(db, proposal.SourceIdentifier, code, ct);

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

        // Edges that arrived before their endpoints were approved can now be stated in
        // the registry's own vocabulary. Done after the loop rather than inside it
        // because an edge needs both ends, and the second end may be approved in this
        // same batch.
        var resolvedEdges = await ResolvePendingConnectionsAsync(db, ct);

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
            State = OutboxState.Pending,
            ScenarioRunId = runContext.CurrentRunId
        });

        // The edges follow as a second publication, after the segments and never
        // merged with them. A receiver cannot store an edge whose ends it has not yet
        // been told about, so the ordering that applied between ENG and the registry
        // applies again between the registry and O&M.
        if (resolvedEdges.Count > 0)
        {
            db.Outbox.Add(new OutboxItem
            {
                ContainerType = "StewardshipApproval",
                ContainerKey = $"{decidedBy}@{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}",
                EntityType = nameof(LocationConnection),
                EntityKeys = JsonSerializer.Serialize(resolvedEdges.Select(e => e.FederationId.ToString())),
                ChangeKind = ChangeKind.Add,
                Verb = "Sync",
                Noun = "SegmentMeshConnections",
                Pattern = MessagePattern.Publication,
                ChannelUri = channelUri,
                Topic = topic,
                CorrelationId = correlationId,
                State = OutboxState.Pending,
                ScenarioRunId = runContext.CurrentRunId
            });
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "REG-LOCATION approved {Count} location(s) and resolved {Edges} connection(s), republishing [{CorrelationId}]",
            codes.Count, resolvedEdges.Count, correlationId);

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
    /// Fills in the location codes for edges whose endpoints have now been approved.
    ///
    /// Returns only the edges resolved by this call, so the caller republishes what
    /// actually changed rather than every edge the registry holds. An edge with one
    /// end still proposed stays pending and is picked up by whichever approval
    /// completes it.
    /// </summary>
    private static async Task<List<LocationConnection>> ResolvePendingConnectionsAsync(
        ParticipantDbContext db, CancellationToken ct)
    {
        var pending = await db.Set<LocationConnection>()
            .Where(c => !c.IsResolved)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return [];
        }

        // Locations approved moments ago are still pending in the change tracker: the
        // approval and this resolution commit together, so a database query would miss
        // exactly the endpoints this call exists to match. Local rows are therefore
        // searched first and the store only consulted for locations approved earlier.
        var resolved = new List<LocationConnection>();

        async Task<Location?> EndpointAsync(string sourceIdentifier) =>
            db.Set<Location>().Local
                .FirstOrDefault(l => l.SourceIdentifier == sourceIdentifier)
            ?? await db.Set<Location>()
                .FirstOrDefaultAsync(l => l.SourceIdentifier == sourceIdentifier, ct);

        foreach (var edge in pending)
        {
            var from = await EndpointAsync(edge.FromSourceIdentifier);
            var to = await EndpointAsync(edge.ToSourceIdentifier);

            if (from is null || to is null)
            {
                continue;
            }

            edge.FromLocationCode = from.LocationCode;
            edge.ToLocationCode = to.LocationCode;
            edge.IsResolved = true;
            edge.UpdatedAt = DateTimeOffset.UtcNow;

            resolved.Add(edge);
        }

        return resolved;
    }

    /// <summary>
    /// Copies a proposal's ingested property values onto the approved Location.
    ///
    /// Mapped and Orphaned are preserved rather than recomputed: whether the registry
    /// understood a value is a fact about ingestion, and re-deciding it here would let an
    /// unmapped value quietly become mapped without any class having sanctioned it.
    /// </summary>
    private static async Task CarryPropertiesAsync(
        ParticipantDbContext db, string sourceIdentifier, string locationCode, CancellationToken ct)
    {
        var values = await db.PropertyValues
            .AsNoTracking()
            .Where(v => v.EntityType == nameof(StewardshipItem)
                && v.EntityKey == sourceIdentifier
                && v.ValidTo == null)
            .ToListAsync(ct);

        if (values.Count == 0)
        {
            return;
        }

        // Re-approval must not double the rows, so anything already carried is skipped.
        var already = await db.PropertyValues
            .AsNoTracking()
            .Where(v => v.EntityType == nameof(Location)
                && v.EntityKey == locationCode
                && v.ValidTo == null)
            .Select(v => v.DefinitionId)
            .ToListAsync(ct);

        foreach (var value in values.Where(v => !already.Contains(v.DefinitionId)))
        {
            db.PropertyValues.Add(new EntityPropertyValue
            {
                EntityType = nameof(Location),
                EntityKey = locationCode,
                DefinitionId = value.DefinitionId,
                ViaClassId = value.ViaClassId,
                NumericValue = value.NumericValue,
                CharacterValue = value.CharacterValue,
                DateTimeValue = value.DateTimeValue,
                BooleanValue = value.BooleanValue,
                UnitOfMeasure = value.UnitOfMeasure,
                CodeValue = value.CodeValue,
                CodeListId = value.CodeListId,
                Mapped = value.Mapped,
                Orphaned = value.Orphaned,
                SourceMessageId = value.SourceMessageId,
                ValidFrom = DateTimeOffset.UtcNow
            });
        }
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
