using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Bods;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using SimHost.Application.Bods;
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
public sealed class RegLocationSegmentsBuilder : IBodBuilder
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

        var infoSource = new InfoSource { ShortName = participant.Config.SourceId };

        foreach (var location in locations)
        {
            var segment = new Segment
            {
                IDInInfoSource = location.LocationCode,
                InfoSource = infoSource,
                ShortName = location.LocationCode,
                FullName = location.Name,
                Description = location.Description
            };

            if (location.ClassKey is { Length: > 0 })
            {
                segment.Type = new SegmentType
                {
                    IDInInfoSource = location.ClassKey,
                    InfoSource = new InfoSource { ShortName = "MIMOSA-RDL" },
                    ShortName = location.ClassKey.Split(':').Last()
                };
            }

            // The originating identifier travels as an attribute so a receiver has a
            // fallback if the registry is unavailable. It is a courtesy, not a
            // substitute: only the CIR knows the full equivalence set.
            segment.Attribute.Add(new Oiie.Ccom.Types.Attribute
            {
                ShortName = "Source identifier",
                Type = new AttributeType
                {
                    IDInInfoSource = "sandbox:SourceIdentifier",
                    ShortName = "Source identifier"
                },
                ValueContent = new TextContent
                {
                    Text = $"{location.SourceParticipant}:{location.SourceIdentifier}"
                }
            });

            bod.With(segment);
        }

        return bod.CreateDocument();
    }
}
