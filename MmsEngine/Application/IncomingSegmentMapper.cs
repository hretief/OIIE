using MmsEngine.Infrastructure.Mms;
using Oiie.Ccom.Types;

namespace MmsEngine.Application;

/// <summary>Validation and translation of approved location facts into an MMS footprint.</summary>
public sealed class IncomingSegmentMapper
{
    public const string LightUnitTable = "LIGHT_UNIT_INVENTORY";

    public string? Validate(Segment segment, Guid channelSite)
    {
        if (segment.UUID == Guid.Empty) return "Segment.UUID is required.";
        if (string.IsNullOrWhiteSpace(segment.ShortName)) return "Segment.ShortName is required.";
        if (segment.Type is null || segment.Type.UUID == Guid.Empty) return "Segment.Type.UUID is required.";
        if (segment.RegistrationSite is null || segment.RegistrationSite.UUID == Guid.Empty)
            return "Segment.RegistrationSite.UUID is required.";
        if (segment.RegistrationSite.UUID != channelSite)
            return "Registration site does not match the publication channel.";
        if (string.IsNullOrWhiteSpace(segment.IDInInfoSource))
            return "Segment.IDInInfoSource is required for the REG-LOCATION CIR anchor.";
        return null;
    }

    public LightUnitUpsert Map(Segment segment, string table, long ownerId)
    {
        if (table != LightUnitTable)
            throw new InvalidOperationException($"MMS table '{table}' has no supported segment writer.");
        if (ownerId <= 0) throw new ArgumentOutOfRangeException(nameof(ownerId));
        return new(segment.UUID, segment.ShortName!, null, null, null, ownerId, null);
    }
}
