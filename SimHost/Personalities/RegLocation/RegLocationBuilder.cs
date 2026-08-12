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

        var bod = new SyncSegments(ActionCodes.Replace);

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

/// <summary>
/// Republishes approved connections to the O&amp;M channel.
///
/// The registry restates the edge in its own vocabulary, exactly as it does for the
/// segments: endpoints travel as LOC- codes rather than the ENG tag numbers the edge
/// arrived with. That is the whole reason the edge waited for approval — before it,
/// there were no codes to state it in.
///
/// Like ENG's builder, this mints one implicit mesh per publication because CCOM has
/// no envelope for a free-standing connection, and sends endpoints as references
/// only. The receiver was told about them by the Sync/Segments message that preceded
/// this one, and restating their content here would give it two sources for the same
/// facts.
/// </summary>
public sealed class RegLocationConnectionsBuilder : IBodBuilder
{
    public (string Verb, string Noun) Handles => ("Sync", "SegmentMeshConnections");

    /// <summary>Only REG-LOCATION uses this builder; ENG has its own.</summary>
    public string? ParticipantId => RegLocationService.ParticipantId;

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

        // Resolved only. An edge that lost an endpoint between approval and dispatch
        // would otherwise be published naming a code that is no longer there.
        var edges = await db.Set<LocationConnection>()
            .Where(c => keys.Contains(c.FederationId) && c.IsResolved)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        var endpointCodes = edges
            .SelectMany(e => new[] { e.FromLocationCode, e.ToLocationCode })
            .Where(c => c is { Length: > 0 })
            .Distinct()
            .ToList();

        var locations = await db.Set<Location>()
            .Where(l => endpointCodes.Contains(l.LocationCode))
            .ToDictionaryAsync(l => l.LocationCode, ct);

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
            Description = "Approved relationships released together."
        };

        foreach (var edge in edges)
        {
            if (!locations.TryGetValue(edge.FromLocationCode!, out var from) ||
                !locations.TryGetValue(edge.ToLocationCode!, out var to))
            {
                continue;
            }

            mesh.Connection.Add(new SegmentConnection
            {
                // The edge's identity as adopted from ENG, not reminted. This is the
                // same relationship, restated by a second holder.
                UUID = edge.FederationId,
                IDInInfoSource = edge.FederationId.ToString(),
                InfoSource = infoSource,
                Type = new ConnectionType
                {
                    UUID = CcomUuid.ForReferenceData(participant.Config.SourceId, edge.TypeKey),
                    IDInInfoSource = edge.TypeKey,
                    InfoSource = infoSource,
                    ShortName = edge.ForwardRole ?? edge.TypeKey,
                    Description = edge.InverseRole
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
    /// A location as an endpoint: enough to resolve it, and nothing more.
    /// </summary>
    private static Segment Reference(Location location, InfoSource infoSource) => new()
    {
        UUID = location.FederationId,
        IDInInfoSource = location.LocationCode is { Length: > 0 }
            ? location.LocationCode
            : location.FederationId.ToString(),
        InfoSource = infoSource,
        ShortName = location.LocationCode
    };
}
