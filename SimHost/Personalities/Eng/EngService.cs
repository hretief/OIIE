using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Bods;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using SimHost.Application.Bods;
using SimHost.Application.Identity;
using SimHost.Application.Participants;
using SimHost.Application.Scenarios;
using SimHost.Domain.Common;
using SimHost.Domain.Eng;
using SimHost.Infrastructure.Sql;

namespace SimHost.Personalities.Eng;

public sealed record PromotionResult(
    bool Released,
    long NamedVersionId,
    string Name,
    int TagCount,
    IReadOnlyList<string> Findings);

public sealed record RelationshipPublicationResult(
    int EdgeCount, string? CorrelationId, string? Detail);

/// <summary>
/// ENG's release workflow.
///
/// Edits accrue without ceremony; the deliberate act is promoting a named version.
/// Promotion runs a validation gate, and only a passing gate writes outbox rows —
/// so the BOD is always derived from committed state, never authored by a form.
/// </summary>
public sealed class EngService(
    IParticipantDbContextFactory factory,
    ITagIdentityService identities,
    ScenarioRunContext runContext,
    ILogger<EngService> logger)
{
    public const string ParticipantId = "eng";

    /// <summary>
    /// The twin used when a caller names none.
    ///
    /// A fixed, well-known identity rather than a per-run one, so that a scenario
    /// which never mentions a twin behaves exactly as it did before the dimension
    /// existed. It is a real row like any other -- not a null or sentinel -- because
    /// a tag belonging to no plant is not a state worth being able to represent.
    /// </summary>
    public static readonly Guid DefaultTwinId = new("0198f000-0000-7000-8000-00000000e461");

    private const string DefaultTwinCode = "ACME-SANDBOX";

    /// <summary>
    /// Ensures the default twin exists, so the first write does not fail a foreign
    /// key on a plant nobody was asked to create. Idempotent: reset drops the table,
    /// and every entry point calls this before scoping to a twin.
    /// </summary>
    public async Task<ITwin> EnsureTwinAsync(
        Guid twinId, string? code = null, string? name = null, string? description = null,
        CancellationToken ct = default)
    {
        await using var db = factory.Create(ParticipantId);

        var existing = await db.ITwins.FirstOrDefaultAsync(t => t.Id == twinId, ct);

        if (existing is not null)
        {
            return existing;
        }

        var twin = new ITwin
        {
            Id = twinId,
            Code = code ?? (twinId == DefaultTwinId ? DefaultTwinCode : twinId.ToString()),
            Name = name ?? (twinId == DefaultTwinId ? "ACME sandbox plant" : "Unnamed twin"),
            Description = description
        };

        db.ITwins.Add(twin);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("ENG registered iTwin {Code} ({TwinId})", twin.Code, twin.Id);

        return twin;
    }

    /// <summary>The twins ENG holds designs for.</summary>
    public async Task<List<ITwin>> ListTwinsAsync(CancellationToken ct = default)
    {
        await using var db = factory.Create(ParticipantId);
        return await db.ITwins.OrderBy(t => t.Code).ToListAsync(ct);
    }

    /// <summary>
    /// The tags in one twin. Scoped by the context rather than by a Where here, so
    /// the filter cannot be forgotten as this grows a search or a paging argument.
    /// </summary>
    public async Task<List<Tag>> ListTagsAsync(Guid twinId, CancellationToken ct = default)
    {
        await using var db = factory.Create(ParticipantId, twinId);

        return await db.Set<Tag>()
            .OrderBy(t => t.TagNumber)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Adds or edits a tag.
    ///
    /// Either <paramref name="tagNumber"/> or <paramref name="codePrefix"/> must be
    /// given. Supplying the number is the ordinary editing path; supplying only a
    /// prefix is the greenfield one — the designer knows they need a valve and asks
    /// the identity service what the next one is called.
    ///
    /// <paramref name="twinId"/> names the plant being designed. It is what makes the
    /// tag number unambiguous: the same TIC-106 in two twins is two instruments, and
    /// the upsert below must not treat one as an edit of the other.
    /// </summary>
    public async Task<Tag> AddTagAsync(
        string? tagNumber,
        string? serviceDescription,
        string? unitNumber,
        string? classKey,
        decimal? rangeMinimum = null,
        decimal? rangeMaximum = null,
        string? controlAction = null,
        string? codePrefix = null,
        Guid? twinId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tagNumber) && string.IsNullOrWhiteSpace(codePrefix))
        {
            throw new ArgumentException(
                "Supply a tagNumber to add or edit a specific tag, or a codePrefix to " +
                "allocate the next one in a series.", nameof(tagNumber));
        }

        var twin = twinId ?? DefaultTwinId;
        await EnsureTwinAsync(twin, ct: ct);

        await using var db = factory.Create(ParticipantId, twin);

        // Greenfield: nothing exists yet, so the identity service allocates both the
        // identity and the code. This is the one path where ENG does not know what the
        // tag is called until it has asked.
        if (string.IsNullOrWhiteSpace(tagNumber))
        {
            var allocated = await identities.AllocateAsync(db, ParticipantId, codePrefix!, twin, ct);

            var minted = new Tag
            {
                ITwinId = twin,
                TagNumber = allocated.Code,
                FederationId = allocated.FederationId
            };

            db.Codes.Add(allocated.Assignment);
            db.Set<Tag>().Add(minted);

            Apply(minted, serviceDescription, unitNumber, classKey,
                rangeMinimum, rangeMaximum, controlAction);

            db.Provenance.Add(new ProvenanceEntry
            {
                EntityType = nameof(Tag),
                EntityKey = allocated.Code,
                Action = ProvenanceAction.Created,
                Actor = "operator",
                ChangeSummary = JsonSerializer.Serialize(new
                {
                    tagNumber = allocated.Code,
                    federationId = allocated.FederationId,
                    allocatedFrom = codePrefix,
                    serviceDescription,
                    classKey
                })
            });

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Allocated {TagNumber} ({FederationId}) from series {Prefix}",
                minted.TagNumber, minted.FederationId, codePrefix);

            return minted;
        }

        // Upsert. Editing a tag — correcting a class, adding a range — is ordinary
        // engineering work, and insert-only would make the second edit of any tag an
        // error. It also means a tag already Published returns to WorkInProgress,
        // which is correct: a changed tag has not been released in its new form.
        //
        // The lookup is twin-scoped by the context's query filter, so an identical tag
        // number in another plant is invisible here rather than being edited by
        // mistake.
        var existing = await db.Set<Tag>().FirstOrDefaultAsync(t => t.TagNumber == tagNumber, ct);

        // ENG is the design tool, so a tag it has not seen before is a tag coming into
        // existence, and this is the one place in the sandbox entitled to mint. The
        // identity is minted once and never revisited on update: correcting a class or
        // adding a range is an edit to the same entity, not a new one.
        var tag = existing ?? new Tag
        {
            ITwinId = twin,
            TagNumber = tagNumber,
            FederationId = identities.Mint()
        };

        if (existing is null)
        {
            // The tag number is registered as ENG's code for the new identity rather
            // than being assumed to be it. Downstream systems will hold other codes
            // for the same thing, and none of them is more the identity than this one.
            db.Codes.Add(identities.RegisterCode(
                tag.FederationId, ParticipantId, tagNumber, twin));
        }

        Apply(tag, serviceDescription, unitNumber, classKey,
            rangeMinimum, rangeMaximum, controlAction);

        if (existing is null)
        {
            db.Set<Tag>().Add(tag);
        }

        // User-originated change: no message caused it, so MessageId stays null.
        db.Provenance.Add(new ProvenanceEntry
        {
            EntityType = nameof(Tag),
            EntityKey = tagNumber,
            Action = existing is null ? ProvenanceAction.Created : ProvenanceAction.Updated,
            Actor = "operator",
            ChangeSummary = JsonSerializer.Serialize(new { tagNumber, serviceDescription, classKey })
        });

        await db.SaveChangesAsync(ct);
        return tag;
    }

    /// <summary>
    /// The editable fields, applied identically whether the tag was allocated or
    /// named. Shared so the two paths cannot drift into treating maturity or the
    /// derived P&amp;ID reference differently.
    /// </summary>
    private static void Apply(
        Tag tag,
        string? serviceDescription,
        string? unitNumber,
        string? classKey,
        decimal? rangeMinimum,
        decimal? rangeMaximum,
        string? controlAction)
    {
        tag.ServiceDescription = serviceDescription;
        tag.UnitNumber = unitNumber;
        tag.ClassKey = classKey;
        tag.RangeMinimum = rangeMinimum;
        tag.RangeMaximum = rangeMaximum;
        tag.ControlAction = controlAction;
        tag.PidReference = unitNumber is null ? null : $"PID-{unitNumber}-001";
        tag.Maturity = TagMaturity.WorkInProgress;
        tag.PublishedInVersionId = null;
        tag.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Asserts a directed design relationship between two existing tags, e.g. power
    /// supply BBFQ0032 supplies pump P-101.
    ///
    /// Both ends must already exist. A relationship to a tag ENG has never heard of
    /// is a design error rather than an instruction to invent the missing end: minting
    /// it here would put a tag into the model that no one designed, and it would then
    /// publish as though it were real.
    ///
    /// Restating an existing edge updates it rather than duplicating it, matching the
    /// upsert semantics of <see cref="AddTagAsync"/>.
    /// </summary>
    public async Task<TagRelationship> RelateTagsAsync(
        string fromTagNumber,
        string toTagNumber,
        string typeKey,
        int? order = null,
        Guid? twinId = null,
        CancellationToken ct = default)
    {
        if (string.Equals(fromTagNumber, toTagNumber, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"A tag cannot be related to itself ({fromTagNumber}).", nameof(toTagNumber));
        }

        var twin = twinId ?? DefaultTwinId;
        await using var db = factory.Create(ParticipantId, twin);

        var relationshipType = await db.Set<TagRelationshipType>()
            .FirstOrDefaultAsync(t => t.Key == typeKey, ct)
            ?? throw new ArgumentException(
                $"Unknown relationship type '{typeKey}'.", nameof(typeKey));

        // Both lookups are twin-scoped, so relating to a tag number that exists only
        // in another plant fails as a missing end rather than silently drawing an edge
        // across two projects.
        var from = await db.Set<Tag>().FirstOrDefaultAsync(t => t.TagNumber == fromTagNumber, ct)
            ?? throw new ArgumentException(
                $"No tag '{fromTagNumber}' to relate from.", nameof(fromTagNumber));

        var to = await db.Set<Tag>().FirstOrDefaultAsync(t => t.TagNumber == toTagNumber, ct)
            ?? throw new ArgumentException(
                $"No tag '{toTagNumber}' to relate to.", nameof(toTagNumber));

        var existing = await db.Set<TagRelationship>().FirstOrDefaultAsync(
            r => r.FromTagId == from.Id && r.ToTagId == to.Id && r.TypeKey == typeKey, ct);

        var relationship = existing ?? new TagRelationship
        {
            ITwinId = twin,
            FederationId = identities.Mint(),
            FromTagId = from.Id,
            ToTagId = to.Id,
            TypeKey = typeKey
        };

        relationship.Order = order;
        relationship.UpdatedAt = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            db.Set<TagRelationship>().Add(relationship);
        }

        db.Provenance.Add(new ProvenanceEntry
        {
            EntityType = nameof(TagRelationship),
            EntityKey = $"{fromTagNumber}->{toTagNumber}",
            Action = existing is null ? ProvenanceAction.Created : ProvenanceAction.Updated,
            Actor = "operator",
            ChangeSummary = JsonSerializer.Serialize(new
            {
                from = fromTagNumber,
                to = toTagNumber,
                typeKey,
                relationshipType.ForwardRole,
                relationshipType.InverseRole
            })
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{From} {Role} {To}",
            fromTagNumber, relationshipType.ForwardRole, toTagNumber);

        return relationship;
    }

    /// <summary>
    /// Promotes a named version. The validation gate is mechanical rather than
    /// gestural: every rule below is checkable, and a finding blocks release.
    ///
    /// Scoped to one twin: a release is an act about a single plant, so promoting in
    /// one must not gather up another's work-in-progress tags and publish them.
    /// </summary>
    public async Task<PromotionResult> PromoteAsync(
        string versionName, string channelUri, string? topic,
        Guid? twinId = null, CancellationToken ct = default)
    {
        var twin = twinId ?? DefaultTwinId;
        await using var db = factory.Create(ParticipantId, twin);

        // Twin-scoped by the query filter, so this is every unpublished tag in this
        // plant rather than in the schema.
        var pending = await db.Set<Tag>()
            .Where(t => t.Maturity != TagMaturity.Published)
            .ToListAsync(ct);

        var version = new NamedVersion
        {
            ITwinId = twin,
            Name = versionName,
            State = NamedVersionState.Draft
        };
        db.Set<NamedVersion>().Add(version);
        await db.SaveChangesAsync(ct);

        var findings = Validate(pending, version.Id);

        if (findings.Count > 0)
        {
            db.Set<ValidationFinding>().AddRange(findings);
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Promotion of '{Version}' blocked by {Count} finding(s)", versionName, findings.Count);

            return new PromotionResult(
                false, version.Id, versionName, pending.Count,
                findings.Select(f => $"{f.TagNumber}: {f.Rule} — {f.Detail}").ToList());
        }

        if (pending.Count == 0)
        {
            return new PromotionResult(false, version.Id, versionName, 0, ["Nothing to publish."]);
        }

        // Domain change and publication intent commit together, so ISBM being
        // briefly unavailable cannot lose the operator's work.
        var correlationId = Guid.NewGuid().ToString();

        foreach (var tag in pending)
        {
            tag.Maturity = TagMaturity.Published;
            tag.PublishedInVersionId = version.Id;
            tag.UpdatedAt = DateTimeOffset.UtcNow;
        }

        version.State = NamedVersionState.Published;
        version.PublishedAt = DateTimeOffset.UtcNow;

        db.Outbox.Add(new OutboxItem
        {
            ContainerType = nameof(NamedVersion),
            ContainerKey = versionName,
            EntityType = nameof(Tag),
            ITwinId = twin,
            EntityKeys = JsonSerializer.Serialize(pending.Select(t => t.TagNumber)),
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

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Promoted '{Version}' with {Count} tag(s) [{CorrelationId}]",
            versionName, pending.Count, correlationId);

        return new PromotionResult(true, version.Id, versionName, pending.Count, []);
    }

    /// <summary>
    /// Publishes the relationships among tags already released.
    ///
    /// A separate release from the segments, and deliberately not part of promotion.
    /// A receiver can only store an edge once both ends exist in its own model, and a
    /// registry that stewards incoming segments does not have them until a steward has
    /// approved. Riding along with the segments would mean every edge arrived naming
    /// two locations that did not yet exist, and being rejected for it — so the edges
    /// are a second act, published once the ends have landed.
    ///
    /// Only edges whose both ends are Published are sent. One end still in draft is not
    /// a partial edge to repair later; it is one the receiver would have to reject.
    /// </summary>
    public async Task<RelationshipPublicationResult> PublishRelationshipsAsync(
        string channelUri, string? topic, Guid? twinId = null, CancellationToken ct = default)
    {
        var twin = twinId ?? DefaultTwinId;
        await using var db = factory.Create(ParticipantId, twin);

        var publishedIds = await db.Set<Tag>()
            .Where(t => t.Maturity == TagMaturity.Published)
            .Select(t => t.Id)
            .ToListAsync(ct);

        var edges = await db.Set<TagRelationship>()
            .Where(r => publishedIds.Contains(r.FromTagId) && publishedIds.Contains(r.ToTagId))
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        if (edges.Count == 0)
        {
            return new RelationshipPublicationResult(0, null, "No relationships with both ends published.");
        }

        var correlationId = Guid.NewGuid().ToString();

        db.Outbox.Add(new OutboxItem
        {
            ContainerType = nameof(TagRelationship),
            ContainerKey = "eng:DesignRelationships",
            EntityType = nameof(TagRelationship),
            ITwinId = twin,
            EntityKeys = JsonSerializer.Serialize(edges.Select(r => r.FederationId.ToString())),
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

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Published {Count} relationship(s) [{CorrelationId}]", edges.Count, correlationId);

        return new RelationshipPublicationResult(edges.Count, correlationId, null);
    }

    /// <summary>
    /// The gate. Kept deliberately shallow for now — classification conformance
    /// against the effective property set arrives with the RDL participant.
    /// </summary>
    private static List<ValidationFinding> Validate(IReadOnlyList<Tag> tags, long versionId)
    {
        var findings = new List<ValidationFinding>();

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag.ClassKey))
            {
                findings.Add(new ValidationFinding
                {
                    NamedVersionId = versionId,
                    TagNumber = tag.TagNumber,
                    Rule = "Unclassified",
                    Detail = "Every tag must carry a reference-data class before publication."
                });
            }

            if (string.IsNullOrWhiteSpace(tag.ServiceDescription))
            {
                findings.Add(new ValidationFinding
                {
                    NamedVersionId = versionId,
                    TagNumber = tag.TagNumber,
                    Rule = "MissingRequiredProperty",
                    Detail = "ServiceDescription is required."
                });
            }
        }

        return findings;
    }
}

/// <summary>
/// Builds SyncSegments from committed Tag rows.
///
/// This mapper is the interoperability work made visible: ENG's own column names on
/// one side, CCOM element names on the other, and nothing in between that a form
/// could have shortcut.
/// </summary>
public sealed class SyncSegmentsBuilder : IBodBuilder
{
    public (string Verb, string Noun) Handles => ("Sync", "Segments");

    public string? ParticipantId => EngService.ParticipantId;

    public async Task<XDocument> BuildAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        OutboxItem item,
        CancellationToken ct)
    {
        var keys = JsonSerializer.Deserialize<List<string>>(item.EntityKeys) ?? [];

        var tags = await db.Set<Tag>()
            .Where(t => keys.Contains(t.TagNumber))
            .OrderBy(t => t.TagNumber)
            .ToListAsync(ct);

        // ChangeKind still records local intent, but Add and Change both publish as
        // Replace: the receiver upserts either way, so the distinction never reached
        // anything that acted on it. Delete stays distinct because it is the one case
        // that means something different on the far side.
        var bod = new SyncSegments(
            item.ChangeKind == ChangeKind.Delete ? ActionCodes.Delete : ActionCodes.Replace);

        bod.ApplicationArea.BODID = item.CorrelationId;
        bod.ApplicationArea.Sender = new Sender
        {
            LogicalID = participant.Config.LogicalId,
            ComponentID = "SimHost",
            // The release container, so a receiver can answer "which release
            // produced this" without inspecting anything else.
            ReferenceID = item.ContainerKey
        };

        var infoSource = new InfoSource
        {
            UUID = CcomUuid.ForInfoSource(participant.Config.SourceId),
            ShortName = participant.Config.SourceId
        };

        foreach (var tag in tags)
        {
            var segment = new Segment
            {
                UUID = tag.FederationId,
                // The code where there is one, the identity where there is not. A tag
                // needs no human-facing code to be publishable, but a receiver still
                // has to be able to address it, and the identity always can.
                IDInInfoSource = tag.TagNumber is { Length: > 0 }
                    ? tag.TagNumber
                    : tag.FederationId.ToString(),
                InfoSource = infoSource,
                ShortName = tag.TagNumber,
                FullName = tag.ServiceDescription,
                Description = tag.ServiceDescription
            };

            if (tag.ClassKey is { Length: > 0 })
            {
                segment.Type = new SegmentType
                {
                    UUID = CcomUuid.ForReferenceData("MIMOSA-RDL", tag.ClassKey),
                    IDInInfoSource = tag.ClassKey,
                    InfoSource = new InfoSource
                    {
                        UUID = CcomUuid.ForInfoSource("MIMOSA-RDL"),
                        ShortName = "MIMOSA-RDL"
                    },
                    ShortName = tag.ClassKey.Split(':').Last()
                };

                // The ancestor chain, leaf first.
                //
                // CCOM's Type is a single reference, so a receiver holding a
                // different subset of the library can only bind the leaf exactly or
                // not at all. Sending the ancestry is what lets it bind at the
                // nearest class it does know instead of giving up — the sender is
                // the only party that has both the leaf and its parents.
                //
                // This is a stopgap. Once an RDL participant publishes definitions,
                // receivers resolve ancestry themselves and this attribute becomes
                // redundant rather than load-bearing.
                var chainKeys = ResolveChain(participant, tag.ClassKey);
                if (chainKeys.Count > 1)
                {
                    AddAttribute(segment, "sandbox:ClassChain", "Class chain",
                        string.Join(' ', chainKeys));
                }
            }

            // ENG-local fields with no CCOM spine equivalent travel as attributes
            // rather than being dropped — the same retention rule receivers apply.
            // Class-governed values, sent with the reference-data keys the receiver
            // will look up. A measure carries its unit so the receiver need not
            // assume one — which is what makes MeasureContent worth the extra shape.
            AddMeasure(segment, "rdl:RangeMinimum", "Range minimum", tag.RangeMinimum, "degC");
            AddMeasure(segment, "rdl:RangeMaximum", "Range maximum", tag.RangeMaximum, "degC");
            AddAttribute(segment, "rdl:ControlAction", "Control action", tag.ControlAction);

            // ENG-local fields with no reference-data definition. The receiver will
            // retain these flagged rather than drop them.
            AddAttribute(segment, "eng:PidReference", "P&ID reference", tag.PidReference);
            AddAttribute(segment, "eng:LineClass", "Line class", tag.LineClass);
            AddAttribute(segment, "eng:DisciplineCode", "Discipline", tag.DisciplineCode);
            AddAttribute(segment, "eng:UnitNumber", "Unit", tag.UnitNumber);

            bod.With(segment);
        }

        return bod.CreateDocument();
    }

    /// <summary>Leaf-first ancestor chain for a class this participant holds.</summary>
    private static List<string> ResolveChain(ParticipantContext participant, string classKey)
    {
        var held = participant.ClassificationSource.FindClassByKey(classKey);
        if (held is null)
        {
            return [classKey];
        }

        return participant.Resolver.BuildTaxonomyChain(held.Id)
            .Select(c => c.ClassKey)
            .Reverse()
            .ToList();
    }

    private static void AddMeasure(
        Segment segment, string key, string name, decimal? value, string unit)
    {
        if (value is not { } number) return;

        segment.Attribute.Add(new Oiie.Ccom.Types.Attribute
        {
            UUID = CcomUuid.ForValue(segment.UUID, key),
            ShortName = name,
            Type = new AttributeType
            {
                UUID = CcomUuid.ForReferenceData(null, key),
                IDInInfoSource = key,
                ShortName = name
            },
            ValueContent = new MeasureContent { Value = number, UnitOfMeasure = unit }
        });
    }

    private static void AddAttribute(Segment segment, string key, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        segment.Attribute.Add(new Oiie.Ccom.Types.Attribute
        {
            UUID = CcomUuid.ForValue(segment.UUID, key),
            ShortName = name,
            Type = new AttributeType
            {
                UUID = CcomUuid.ForReferenceData(null, key),
                IDInInfoSource = key,
                ShortName = name
            },
            ValueContent = new TextContent { Text = value }
        });
    }
}

/// <summary>
/// Builds SyncSegmentMeshConnections from committed TagRelationship rows.
///
/// The mesh exists only here. ENG stores edges with no notion of a network, but CCOM
/// has no envelope for a free-standing connection, so one implicit mesh is minted per
/// release to carry them. Its identity is derived from the release container, so
/// republishing the same version yields the same mesh rather than a new one each time.
///
/// Segments at either end are sent as references — identity and code only, no
/// attributes. The receiver already has them from the Sync/Segments message that
/// preceded this one; restating their content here would give it two sources for the
/// same facts and no rule for which wins.
/// </summary>
public sealed class SyncSegmentConnectionsBuilder : IBodBuilder
{
    public (string Verb, string Noun) Handles => ("Sync", "SegmentMeshConnections");

    public string? ParticipantId => EngService.ParticipantId;

    public async Task<XDocument> BuildAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        OutboxItem item,
        CancellationToken ct)
    {
        var keys = (JsonSerializer.Deserialize<List<string>>(item.EntityKeys) ?? [])
            .Select(k => Guid.TryParse(k, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();

        var edges = await db.Set<TagRelationship>()
            .Where(r => keys.Contains(r.FederationId))
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        // Both endpoints and the type vocabulary, resolved in one pass rather than
        // per edge: the endpoint sets overlap heavily in any realistic release.
        var tagIds = edges.SelectMany(r => new[] { r.FromTagId, r.ToTagId }).Distinct().ToList();

        var tags = await db.Set<Tag>()
            .Where(t => tagIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        var typeKeys = edges.Select(r => r.TypeKey).Distinct().ToList();

        var types = await db.Set<TagRelationshipType>()
            .Where(t => typeKeys.Contains(t.Key))
            .ToDictionaryAsync(t => t.Key, ct);

        var bod = new SyncSegmentMeshConnections(
            item.ChangeKind == ChangeKind.Delete ? ActionCodes.Delete : ActionCodes.Replace);

        bod.ApplicationArea.BODID = item.CorrelationId;
        bod.ApplicationArea.Sender = new Sender
        {
            LogicalID = participant.Config.LogicalId,
            ComponentID = "SimHost",
            ReferenceID = item.ContainerKey
        };

        var infoSource = new InfoSource
        {
            UUID = CcomUuid.ForInfoSource(participant.Config.SourceId),
            ShortName = participant.Config.SourceId
        };

        var mesh = new SegmentMesh
        {
            UUID = CcomUuid.FromKey("SegmentMesh", $"{participant.Config.SourceId}\u001f{item.ContainerKey}"),
            IDInInfoSource = item.ContainerKey,
            InfoSource = infoSource,
            ShortName = item.ContainerKey,
            Description = "Design relationships released together."
        };

        foreach (var edge in edges)
        {
            if (!tags.TryGetValue(edge.FromTagId, out var from) ||
                !tags.TryGetValue(edge.ToTagId, out var to))
            {
                continue;
            }

            types.TryGetValue(edge.TypeKey, out var type);

            mesh.Connection.Add(new SegmentConnection
            {
                UUID = edge.FederationId,
                IDInInfoSource = edge.FederationId.ToString(),
                InfoSource = infoSource,
                Type = new ConnectionType
                {
                    UUID = CcomUuid.ForReferenceData(participant.Config.SourceId, edge.TypeKey),
                    IDInInfoSource = edge.TypeKey,
                    InfoSource = infoSource,
                    ShortName = type?.ForwardRole ?? edge.TypeKey,
                    // The inverse reading travels with the type so the receiver can
                    // render the edge from either end without holding ENG's
                    // vocabulary. Sending only "Supplies" would leave a registry
                    // displaying the pump unable to say anything about the supply.
                    Description = type?.InverseRole
                },
                From = Reference(from, infoSource),
                To = Reference(to, infoSource),
                Order = edge.Order?.ToString()
            });
        }

        bod.With(mesh);

        return bod.CreateDocument();
    }

    /// <summary>
    /// A segment as an endpoint: enough to resolve it, and nothing more.
    /// </summary>
    private static Segment Reference(Tag tag, InfoSource infoSource) => new()
    {
        UUID = tag.FederationId,
        IDInInfoSource = tag.TagNumber is { Length: > 0 }
            ? tag.TagNumber
            : tag.FederationId.ToString(),
        InfoSource = infoSource,
        ShortName = tag.TagNumber
    };
}
