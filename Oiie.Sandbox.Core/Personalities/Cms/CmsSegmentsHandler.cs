using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using SimHost.Application.Bods;
using SimHost.Application.Cir;
using SimHost.Application.Identity;
using SimHost.Application.Participants;
using SimHost.Domain.Cms;
using SimHost.Domain.Common;
using SimHost.Infrastructure.Sql;

namespace SimHost.Personalities.Cms;

/// <summary>
/// Creates CMS asset placeholders from segments arriving on the O&amp;M channel.
///
/// A condition monitoring system rarely holds functional locations. What it holds is
/// assets — the things whose condition it monitors. So the design artefact arriving
/// from REG-LOCATION is not stored as a location here; it is transformed into an
/// <see cref="CmsAsset"/> placeholder, which is the record CMS will later complete
/// when CONSTRUCT supplies the physical detail through REG-ASSET.
///
/// That transformation is the interesting part of this handler and the reason
/// <c>cms.Location</c> is not modelled at all. Mapping a segment onto the customer's
/// own plant hierarchy would assert that the two mean the same thing, and they do
/// not: <c>cms.Location</c> is CMS's internal structure, not a register of
/// engineering functional locations.
///
/// The placeholder is deliberately incomplete rather than plausibly complete. Serial
/// number, manufacturer, model and commission date are all left null, because a
/// functional location has none of them — the physical unit that will carry them has
/// not been fitted yet. Filling them with defaults would produce a row that reads as
/// a commissioned asset and hide the fact that the install has not happened.
///
/// As in MMS, CMS cannot write down where an asset came from: the asset table has no
/// column for a foreign identifier, a FederationId or a CIRID, and none may be
/// added. Matching on re-receipt is therefore by AssetTag alone — weaker than
/// matching on a key, and the honest consequence of the constraint.
///
/// The site is the exception, and a narrow one. <c>cms.Site</c> does have a UUID
/// column, so where the segment's RegistrationSite carries one it is retained rather
/// than discarded — it is the publisher's data and CMS was told it, so throwing it
/// away would be losing information, not protecting a boundary. Retaining it is not
/// the same as querying on it: the segment is placed at a site by resolving the
/// sender's own site identifier through the registry, never by matching that column.
///
/// Sites are not created here. They are provisioned ahead of time, the way
/// <c>SETUP_OWNER</c> is in MMS, and in production by BIC publishing SyncSites. A
/// segment naming a plant CMS has not provisioned is skipped: an operator establishes
/// where the business operates, and a publisher mentioning a site is not that act.
///
/// The site's UUID is deliberately not used as a join key for foreign queries.
/// Filtering CMS assets by matching that column against an iTwin GUID would work, and
/// would also teach a condition monitoring system to speak iTwin, which is the
/// shortcut this sandbox exists to argue against. Scoping goes through the registry by
/// CIRID, the same route every other participant uses, and the relation it depends on
/// is asserted by RelateCmsSiteAsync.
/// </summary>
public sealed class CmsSegmentsHandler(
    ITagIdentityService identities,
    CmsContextResolver context,
    ILogger<CmsSegmentsHandler> logger) : IBodHandler
{
    public (string Verb, string Noun) Handles => ("Sync", "Segments");

    public string? ParticipantId => CmsService.ParticipantId;

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

        var created = 0;
        var updated = 0;
        var skipped = 0;
        var sites = new Dictionary<string, CmsSite?>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in segments)
        {
            var foreignSourceId = segment.InfoSource?.ShortName ?? envelope.SenderLogicalId ?? "unknown";
            var foreignId = segment.IDInInfoSource;

            if (string.IsNullOrWhiteSpace(foreignId))
            {
                continue;
            }

            // The site comes first, and not merely for foreign key ordering. ASSET.SiteID
            // is NOT NULL, so a segment CMS cannot place at a site is a segment CMS
            // cannot store — it says it does not monitor things whose plant is unknown.
            // Skipping is therefore the correct outcome, and it is counted and reported
            // rather than swallowed, because silently dropping a segment would look
            // identical to never having received it.
            var site = await ResolveSiteAsync(db, segment, sites, ct);

            if (site is null)
            {
                logger.LogWarning(
                    "CMS skipped segment {SegmentId}: it names no provisioned site. A site must be provisioned before assets can land in it.",
                    foreignId);

                skipped++;
                continue;
            }

            // AssetTag is UNIQUE and is the only thing CMS can match on. The sender's
            // identifier is used for it because that is what stays stable across
            // republication; the display name is not, and matching on a name would
            // duplicate the asset the first time someone corrects a spelling.
            var assetTag = foreignId;
            var assetName = segment.FullName ?? segment.ShortName ?? foreignId;

            var existing = await db.Set<CmsAsset>()
                .FirstOrDefaultAsync(a => a.AssetTag == assetTag, ct);

            if (existing is not null)
            {
                // Re-receipt refreshes the descriptive fields only. Anything a planner
                // has since filled in — class, criticality, the serial number of the
                // unit actually fitted — is left alone: a republished design artefact
                // is not authority to overwrite what the operating system has learned
                // since.
                existing.AssetName = assetName;
                existing.Description = segment.Description ?? existing.Description;

                // The site is re-asserted, because a segment genuinely moving between
                // plants is a fact about the design that CMS should follow.
                existing.SiteId = site.SiteId;
                existing.UpdatedAtUtc = DateTime.UtcNow;

                updated++;
                continue;
            }

            var asset = new CmsAsset
            {
                SiteId = site.SiteId,
                AssetTag = assetTag,
                AssetName = assetName,
                Description = segment.Description,

                // Placeholder, and visibly so. The row exists because a design says
                // this thing will be there, not because anyone has seen it.
                OperationalStatus = PlaceholderStatus,

                // Left null on purpose: AssetClassID is the customer's taxonomy and
                // the sender's classification is not CMS's classification. A planner
                // sets it once the asset is surveyed.
                AssetClassId = null
            };


            db.Set<CmsAsset>().Add(asset);
            created++;

            // The identity correspondence has to live outside the customer schema,
            // since no column here can hold it. Saved after the row so the database
            // has allocated AssetID — it is an IDENTITY column, unlike MMS's
            // LIGHT_SYSTEM_ID, so it does not exist until the insert completes.
            await db.SaveChangesAsync(ct);

            if (segment.UUID != Guid.Empty)
            {
                var assignment = identities.RegisterCode(
                    segment.UUID, CmsService.ParticipantId, asset.AssetId.ToString());
                assignment.AdoptedFromRemote = true;
                db.Codes.Add(assignment);
            }

            db.Provenance.Add(new ProvenanceEntry
            {
                MessageId = messageId,
                EntityType = nameof(CmsAsset),
                EntityKey = asset.AssetId.ToString(),
                Action = ProvenanceAction.Created,
                Actor = "system",
                ChangeSummary = JsonSerializer.Serialize(new
                {
                    foreignSourceId,
                    foreignId,
                    assetTag,
                    siteCode = site.SiteCode,
                    placeholder = true
                })
            });
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "CMS created {Created} and refreshed {Updated} asset placeholder(s) across {Sites} site(s), skipping {Skipped}, from {Count} segment(s) [{CorrelationId}]",
            created, updated, sites.Count, skipped, segments.Count, envelope.BodId);

        // Rejected only when nothing at all could be stored. A partial result is
        // reported as applied with the shortfall named, because the segments that did
        // land are genuinely in the database and pretending otherwise would invite a
        // redelivery that duplicates nothing but confuses everything.
        if (created == 0 && updated == 0 && skipped > 0)
        {
            return BodHandlingResult.Rejected(
                $"None of the {segments.Count} segment(s) named a provisioned site. " +
                "A CMS site must be provisioned before assets can land in it.");
        }

        var detail = skipped > 0
            ? $"{skipped} segment(s) were skipped for naming no provisioned site."
            : null;

        return new BodHandlingResult(ProcessingStatus.Applied, detail, created + updated, 0, 0);
    }

    /// <summary>
    /// Finds the provisioned CMS site a segment's <c>RegistrationSite</c> names, or
    /// null when no such site has been provisioned.
    ///
    /// Nothing is created here. A site is established by provisioning — in production
    /// by BIC publishing SyncSites — so a segment naming an unknown plant is a segment
    /// about somewhere CMS does not operate. Creating the site on demand would let a
    /// publisher silently extend the estate by mentioning it, and the resulting row
    /// would be unrelated in the registry and invisible to every scoped read anyway.
    ///
    /// Matched on the publisher's UUID first and the short name second. The UUID is
    /// tried first because it is an identity rather than a label, but a provisioned
    /// site has no publisher-supplied UUID to match — its own was derived at seed time
    /// — so the code is what actually connects a segment to a plant. Both are lookups;
    /// neither is a foreign key stored on the row.
    /// </summary>
    /// <summary>
    /// The CMS site a segment belongs to, resolved through the registry.
    ///
    /// The sender names its own site by IDInInfoSource, which is its record ID and
    /// means nothing to CMS: for ENG it is an iTwin GUID, and CMS holds no such key.
    /// So the identifier is resolved to a CIRID and back to the CMS site sharing it,
    /// which is the same route the read paths take.
    ///
    /// Neither of the two column matches this replaces was sound. SiteUuid holds the
    /// publisher's key rather than the twin's, so it matched only by luck; SiteCode
    /// against ShortName matched because an ENG twin code and a CMS site code happen
    /// to be the same district number today, which is a coincidence of naming and not
    /// an assertion anyone made. A twin rename would have quietly broken it.
    ///
    /// A site becomes reachable here only once someone has related it, which is the
    /// point: the relation is a steward's act, not two columns holding equal bytes.
    /// </summary>
    private async Task<CmsSite?> ResolveSiteAsync(
        ParticipantDbContext db,
        Segment segment,
        Dictionary<string, CmsSite?> cache,
        CancellationToken ct)
    {
        var site = segment.RegistrationSite;

        if (site?.IDInInfoSource is not { Length: > 0 } foreignSiteId)
        {
            return null;
        }

        if (cache.TryGetValue(foreignSiteId, out var cached))
        {
            return cached;
        }

        CmsSite? resolved = null;

        try
        {
            var siteCodes = await context.ResolveSiteCodesAsync(foreignSiteId, ct);

            if (siteCodes.Count > 0)
            {
                resolved = await db.Set<CmsSite>()
                    .FirstOrDefaultAsync(s => siteCodes.Contains(s.SiteCode), ct);
            }
        }
        catch (Exception ex)
        {
            // An unreachable registry is not an absent relation. Both end in a skipped
            // segment, but only one of them is a fact about the data, so the reason is
            // logged rather than left to look like an unprovisioned site.
            logger.LogWarning(
                ex, "CMS context resolution failed for {SiteId}.", foreignSiteId);
        }

        cache[foreignSiteId] = resolved;
        return resolved;
    }

    /// <summary>
    /// OPERATIONAL_STATUS for a row that exists because a design named it, not because
    /// anyone has surveyed it. Free text in the DDL, so this is a sandbox convention
    /// rather than a customer-defined value.
    /// </summary>
    private const string PlaceholderStatus = "Planned";
}
