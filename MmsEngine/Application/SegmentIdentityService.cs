using System.Globalization;
using Microsoft.Extensions.Options;
using MmsEngine.Infrastructure.Cir;
using Oiie.Ccom.Types;

namespace MmsEngine.Application;

/// <summary>CIR-backed class dispatch, site ownership and asset identity write-back.</summary>
public sealed class SegmentIdentityService(ICirClient cir, IOptions<MmsEngineOptions> options)
{
    private readonly MmsEngineOptions _options = options.Value;

    public async Task<string?> ResolveTableAsync(SegmentType type, CancellationToken ct)
    {
        var entries = await cir.FindEntriesAsync(
            _options.Enterprise, "RDL-CLASS", type.UUID, null, null, ct);
        var ours = entries.Where(e => e.Entry.SourceId == _options.SourceId).ToArray();
        if (ours.Length > 0)
        {
            if (ours.Length != 1 || ours[0].CategorySourceId != _options.SourceId)
                throw new CirClientException("The governed class has ambiguous MMS table mappings.");
            return ours[0].Entry.IdInSource;
        }

        var table = _options.ResolveTargetTable(type.ShortName);
        if (table is null || !entries.Any(e =>
                e.Entry.SourceId != _options.SourceId
                && string.Equals(e.Entry.Name, type.ShortName, StringComparison.OrdinalIgnoreCase)))
            return null;

        await cir.EnsureEntryAsync(_options.Enterprise, "RDL-CLASS", _options.SourceId,
            new CirEntry(table, _options.SourceId, type.UUID, _options.Enterprise,
                table, new CirLocalizedText(type.ShortName!), []), ct);

        // Registration success alone is not dispatch authority: read CIR's answer.
        var registered = await cir.FindEntriesAsync(
            _options.Enterprise, "RDL-CLASS", type.UUID, _options.SourceId, null, ct);
        if (registered.Count != 1 || registered[0].CategorySourceId != _options.SourceId
            || registered[0].Entry.IdInSource != table)
            throw new CirClientException("MMS class mapping could not be verified after registration.");
        return registered[0].Entry.IdInSource;
    }

    public async Task<long?> ResolveOwnerAsync(Guid site, CancellationToken ct)
    {
        var entries = await cir.FindEntriesAsync(
            _options.Enterprise, "ITWIN-SITE", site, _options.SourceId, null, ct);
        if (entries.Count == 0) return null;
        if (entries.Count != 1 || entries[0].CategorySourceId != _options.SourceId
            || !long.TryParse(entries[0].Entry.IdInSource, NumberStyles.None,
                CultureInfo.InvariantCulture, out var owner) || owner <= 0)
            throw new CirClientException("Site has an ambiguous or invalid MMS OWNER_ID mapping.");
        return owner;
    }

    public async Task<LocatedCirEntry?> ResolveAnchorAsync(Segment segment, CancellationToken ct)
    {
        var entries = await cir.FindEntriesAsync(_options.Enterprise, "FunctionalLocation",
            segment.UUID, _options.ApprovedSourceId, segment.IDInInfoSource, ct);
        if (entries.Count == 0) return null;
        if (entries.Count != 1 || entries[0].CategorySourceId != _options.ApprovedSourceId)
            throw new CirClientException("Approved segment has ambiguous REG-LOCATION CIR anchors.");
        return entries[0];
    }

    public async Task RegisterAssetAsync(
        Segment segment, LocatedCirEntry anchor, long lightUnitId, CancellationToken ct)
    {
        var nativeId = lightUnitId.ToString(CultureInfo.InvariantCulture);
        var existing = await cir.FindEntriesAsync(
            _options.Enterprise, "ASSET", null, _options.SourceId, nativeId, ct);
        if (existing.Count > 0)
        {
            VerifyAsset(existing, segment.UUID);
            return;
        }

        await cir.CreateEquivalentEntryAsync(new EquivalentEntryRequest(
            anchor.Entry.IdInSource, anchor.Entry.SourceId, _options.Enterprise,
            anchor.CategoryId, anchor.CategorySourceId,
            new CirEntry(nativeId, _options.SourceId, segment.UUID, _options.Enterprise,
                segment.ShortName, null, []), "ASSET", _options.SourceId), ct);

        var registered = await cir.FindEntriesAsync(
            _options.Enterprise, "ASSET", null, _options.SourceId, nativeId, ct);
        VerifyAsset(registered, segment.UUID);
    }

    private void VerifyAsset(IReadOnlyList<LocatedCirEntry> entries, Guid identity)
    {
        if (entries.Count != 1 || entries[0].CategorySourceId != _options.SourceId
            || entries[0].Entry.Cirid != identity)
            throw new CirClientException("MMS asset registration is missing or conflicts with the segment identity.");
    }
}
