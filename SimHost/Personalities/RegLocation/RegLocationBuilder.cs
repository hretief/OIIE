using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Bods;
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
/// Republishes approved locations to the O&amp;M channel.
///
/// Note what changes between the inbound and outbound BOD: the identifier. ENG sent
/// TIC-106; this sends LOC-000412 with InfoSource REG-LOCATION. Downstream, MMS
/// receives an identifier it has never seen for equipment it may already hold — the
/// problem the registry exists to solve, created here deliberately rather than by
/// accident.
/// </summary>
public sealed class RegLocationSegmentsBuilder(CcomAttributeMapperFactory mappers) : IBodBuilder
{
    public (string Verb, string Noun) Handles => ("Sync", "Segments");

    /// <summary>Only REG-LOCATION uses this builder; ENG has its own.</summary>
    public string? ParticipantId => RegLocationService.ParticipantId;

    public async Task<XDocument> BuildAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        OutboxItem item,
        CancellationToken ct)
    {
        var codes = JsonSerializer.Deserialize<List<string>>(item.EntityKeys) ?? [];

        var locations = await db.Set<Location>()
            .Where(l => codes.Contains(l.LocationCode))
            .OrderBy(l => l.LocationCode)
            .ToListAsync(ct);

        var bod = new SyncSegments(ActionCodes.Add);

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

        foreach (var location in locations)
        {
            var segment = new Segment
            {
                // The identity as adopted, so what MMS receives is the same entity
                // ENG published, under the registry's code rather than ENG's.
                UUID = location.FederationId,
                IDInInfoSource = location.LocationCode is { Length: > 0 }
                    ? location.LocationCode
                    : location.FederationId.ToString(),
                InfoSource = infoSource,
                ShortName = location.LocationCode,
                FullName = location.Name,
                Description = location.Description
            };

            if (location.ClassKey is { Length: > 0 })
            {
                segment.Type = new SegmentType
                {
                    UUID = CcomUuid.ForReferenceData("MIMOSA-RDL", location.ClassKey),
                    IDInInfoSource = location.ClassKey,
                    InfoSource = new InfoSource
                    {
                        UUID = CcomUuid.ForInfoSource("MIMOSA-RDL"),
                        ShortName = "MIMOSA-RDL"
                    },
                    ShortName = location.ClassKey.Split(':').Last()
                };
            }

            // The originating identifier travels as an attribute so a receiver has a
            // fallback if the registry is unavailable. It is a courtesy, not a
            // substitute: only the CIR knows the full equivalence set.
            segment.Attribute.Add(new Oiie.Ccom.Types.Attribute
            {
                UUID = CcomUuid.ForValue(segment.UUID, "sandbox:SourceIdentifier"),
                ShortName = "Source identifier",
                Type = new AttributeType
                {
                    UUID = CcomUuid.ForReferenceData(null, "sandbox:SourceIdentifier"),
                    IDInInfoSource = "sandbox:SourceIdentifier",
                    ShortName = "Source identifier"
                },
                ValueContent = new TextContent
                {
                    Text = $"{location.SourceParticipant}:{location.SourceIdentifier}"
                }
            });

            // Everything the registry retained travels on, mapped and unmapped alike.
            //
            // Forwarding only what REG-LOCATION understood would make the registry a
            // filter: MMS may hold a definition the registry lacks, and a value dropped
            // here can never be recovered downstream. Unmapped values are flagged rather
            // than withheld, so the receiver can see the registry passed on something it
            // did not itself understand and decide for itself.
            var values = await db.PropertyValues
                .AsNoTracking()
                .Where(v => v.EntityType == nameof(Location)
                    && v.EntityKey == location.LocationCode
                    && v.ValidTo == null)
                .ToListAsync(ct);

            if (values.Count > 0)
            {
                var mapper = mappers.For(participant);

                mapper.Apply(
                    segment, values, EffectivePropertySet.Empty, participant.Config.SourceId);

                // A marker naming what the registry could not place, so the omission is
                // visible on the wire instead of being inferred from an absence.
                var unmapped = values.Where(v => !v.Mapped).ToList();

                if (unmapped.Count > 0)
                {
                    segment.Attribute.Add(new Oiie.Ccom.Types.Attribute
                    {
                        UUID = CcomUuid.ForValue(segment.UUID, "sandbox:UnmappedProperties"),
                        ShortName = "Unmapped properties",
                        Type = new AttributeType
                        {
                            UUID = CcomUuid.ForReferenceData(null, "sandbox:UnmappedProperties"),
                            IDInInfoSource = "sandbox:UnmappedProperties",
                            ShortName = "Unmapped properties"
                        },
                        ValueContent = new TextContent
                        {
                            Text = string.Join(' ', unmapped
                                .Select(v => DefinitionKey(db, v.DefinitionId))
                                .Where(k => k is { Length: > 0 }))
                        }
                    });
                }
            }

            bod.With(segment);
        }

        return bod.CreateDocument();
    }

    /// <summary>
    /// The reference-data key for a definition, for naming a value on the wire. Falls
    /// back to the id so an unnameable value is still reported rather than omitted.
    /// </summary>
    private static string DefinitionKey(ParticipantDbContext db, Guid definitionId) =>
        db.PropertyDefinitions
            .AsNoTracking()
            .Where(d => d.Id == definitionId)
            .Select(d => d.DefinitionKey)
            .FirstOrDefault() ?? definitionId.ToString();
}
