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
    /// Adds or edits a tag.
    ///
    /// Either <paramref name="tagNumber"/> or <paramref name="codePrefix"/> must be
    /// given. Supplying the number is the ordinary editing path; supplying only a
    /// prefix is the greenfield one — the designer knows they need a valve and asks
    /// the identity service what the next one is called.
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
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tagNumber) && string.IsNullOrWhiteSpace(codePrefix))
        {
            throw new ArgumentException(
                "Supply a tagNumber to add or edit a specific tag, or a codePrefix to " +
                "allocate the next one in a series.", nameof(tagNumber));
        }

        await using var db = factory.Create(ParticipantId);

        // Greenfield: nothing exists yet, so the identity service allocates both the
        // identity and the code. This is the one path where ENG does not know what the
        // tag is called until it has asked.
        if (string.IsNullOrWhiteSpace(tagNumber))
        {
            var allocated = await identities.AllocateAsync(db, ParticipantId, codePrefix!, ct);

            var minted = new Tag
            {
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
        var existing = await db.Set<Tag>().FirstOrDefaultAsync(t => t.TagNumber == tagNumber, ct);

        // ENG is the design tool, so a tag it has not seen before is a tag coming into
        // existence, and this is the one place in the sandbox entitled to mint. The
        // identity is minted once and never revisited on update: correcting a class or
        // adding a range is an edit to the same entity, not a new one.
        var tag = existing ?? new Tag
        {
            TagNumber = tagNumber,
            FederationId = identities.Mint()
        };

        if (existing is null)
        {
            // The tag number is registered as ENG's code for the new identity rather
            // than being assumed to be it. Downstream systems will hold other codes
            // for the same thing, and none of them is more the identity than this one.
            db.Codes.Add(identities.RegisterCode(
                tag.FederationId, ParticipantId, tagNumber));
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
    /// Promotes a named version. The validation gate is mechanical rather than
    /// gestural: every rule below is checkable, and a finding blocks release.
    /// </summary>
    public async Task<PromotionResult> PromoteAsync(
        string versionName, string channelUri, string? topic, CancellationToken ct = default)
    {
        await using var db = factory.Create(ParticipantId);

        var pending = await db.Set<Tag>()
            .Where(t => t.Maturity != TagMaturity.Published)
            .ToListAsync(ct);

        var version = new NamedVersion { Name = versionName, State = NamedVersionState.Draft };
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

        var bod = new SyncSegments(
            item.ChangeKind == ChangeKind.Add ? ActionCodes.Add : ActionCodes.Change);

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
