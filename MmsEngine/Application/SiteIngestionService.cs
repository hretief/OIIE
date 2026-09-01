using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MmsEngine.Infrastructure.Cir;
using MmsEngine.Infrastructure.Mms;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using Oiie.Isbm.Client;

namespace MmsEngine.Application;

/// <summary>
/// What one drain of the sites channel did.
/// </summary>
public sealed class SiteIngestionReport
{
    /// <summary>Publications read from the channel.</summary>
    public int MessagesRead { get; set; }

    /// <summary>Sites found across those publications.</summary>
    public int SitesSeen { get; set; }

    /// <summary>Sites newly created in MMS.</summary>
    public int Created { get; set; }

    /// <summary>Sites that already existed and were updated.</summary>
    public int Updated { get; set; }

    /// <summary>Sites the mapper refused.</summary>
    public int Rejected { get; set; }

    /// <summary>CIR entries written.</summary>
    public int CirEntriesRegistered { get; set; }

    /// <summary>Messages left on the channel because handling them failed.</summary>
    public int Failed { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// Receives SyncSites from ENG and records the site in MMS.
///
/// This is how a plant comes to exist in MMS. Nothing else creates one: sites
/// are not seeded, and MmsProvider is reachable only through its own REST API,
/// so the only route from "ENG published a site" to "MMS knows about it" is this
/// service. That is deliberate -- a maintenance system that invented its own
/// plants would be asserting something only the enterprise can.
///
/// Writes directly rather than proposing, matching REG-LOCATION's site leg: a
/// site is not an assertion about the plant that a steward might reject, it is
/// the context within which such assertions are later made. Holding sites in a
/// queue would stall every downstream flow behind a decision with nothing to
/// decide.
/// </summary>
public sealed class SiteIngestionService(
    IIsbmClient isbm,
    IMmsClient mms,
    ICirClient cir,
    IncomingSiteMapper mapper,
    IOptions<MmsEngineOptions> options,
    ILogger<SiteIngestionService> logger)
{
    private readonly MmsEngineOptions _options = options.Value;

    /// <summary>
    /// The subscription session, cached across polls. A session is the broker's
    /// record of what this consumer has already seen, and reopening one each
    /// pass either replays the channel or silently skips what arrived in
    /// between.
    ///
    /// This engine's session is its own. CMS and REG-LOCATION read the same
    /// channel through separate sessions, so all three receive every site rather
    /// than competing for them.
    /// </summary>
    private string? _sessionId;

    public async Task<SiteIngestionReport> DrainAsync(CancellationToken ct)
    {
        var report = new SiteIngestionReport();

        if (!_options.SitesIngestEnabled)
        {
            report.Note = "SitesIngestEnabled is off.";
            return report;
        }

        if (string.IsNullOrWhiteSpace(_options.MmsBaseUrl))
        {
            report.Note = "MmsBaseUrl is not configured, so sites cannot be recorded.";
            logger.LogWarning("{Note}", report.Note);
            return report;
        }

        _sessionId ??= await isbm.OpenSubscriptionSessionAsync(
            _options.SitesChannelUri, _options.SitesTopics, ct);

        while (report.MessagesRead < _options.MaxMessagesPerPoll)
        {
            var message = await isbm.ReadPublicationAsync(_sessionId, ct);

            // Null is the broker saying the queue is empty, not an error.
            if (message is null)
            {
                break;
            }

            report.MessagesRead++;

            bool handled;
            try
            {
                handled = await HandleMessageAsync(message, report, ct);
            }
            catch (Exception ex)
            {
                // Left on the channel rather than discarded: a fix can be
                // deployed and the message reprocessed. A site that never lands
                // is a plant no work order can ever be raised against.
                logger.LogError(
                    ex,
                    "Failed to handle site publication {MessageId}; leaving it on the channel.",
                    message.MessageId);

                handled = false;
            }

            if (!handled)
            {
                report.Failed++;
                break;
            }

            // Removed only after every site in it has been recorded. A crash
            // before this point redelivers the message, which is safe because
            // the upsert is keyed on the site's own GUID.
            await isbm.RemovePublicationAsync(_sessionId, ct);
        }

        if (report.MessagesRead > 0)
        {
            logger.LogInformation(
                "Site drain read {Messages} message(s): {Created} created, {Updated} updated, " +
                "{Rejected} rejected, {Cir} CIR entry(ies), {Failed} left on the channel.",
                report.MessagesRead, report.Created, report.Updated,
                report.Rejected, report.CirEntriesRegistered, report.Failed);
        }

        return report;
    }

    /// <summary>
    /// Records every site in one publication. Returns false when the message
    /// should stay on the channel.
    /// </summary>
    private async Task<bool> HandleMessageAsync(
        IsbmMessage message, SiteIngestionReport report, CancellationToken ct)
    {
        if (message.Content is null)
        {
            // Unparseable, and retrying will not change that. The one case where
            // discarding is right: leaving it would block everything behind it.
            logger.LogError(
                "Site publication {MessageId} carried no parseable XML; discarding it.",
                message.MessageId);

            return true;
        }

        var envelope = BodEnvelope.Parse(message.Content.ToString());

        if (!envelope.Is("Sync", "Site"))
        {
            logger.LogDebug(
                "Publication {MessageId} was {Verb}{Noun}, which this leg does not handle.",
                message.MessageId, envelope.Verb, envelope.Noun);

            return true;
        }

        var mapped = new List<SiteMappingResult>();

        foreach (var site in envelope.NounsAs(e => new Site(e)))
        {
            report.SitesSeen++;

            var result = mapper.Map(site);

            if (!result.IsMapped)
            {
                // Rejections are counted and logged, not retried. The message is
                // as good as it will ever be: a site with no GUID will not grow
                // one on redelivery.
                report.Rejected++;

                logger.LogWarning(
                    "Site '{Name}' in publication {MessageId} was rejected: {Reason}.",
                    site.ShortName ?? site.FullName ?? "(unnamed)",
                    message.MessageId,
                    result.Rejection);

                continue;
            }

            mapped.Add(result);
        }

        if (mapped.Count == 0)
        {
            // Nothing usable, but the message was understood. Acknowledged so the
            // channel is not blocked by a publication that will never succeed.
            return true;
        }

        var upserts = mapped
            .Select(m => new SiteUpsert(
                SiteId: m.SiteId,
                SiteCode: m.SiteCode,
                SiteName: m.SiteName,
                Description: m.Description,
                SiteType: m.SiteType,

                // CCOM's Site carries no parent, country, region or status in the
                // shape the sandbox publishes, and inventing them would put values
                // in MMS that no publisher ever asserted.
                ParentSiteId: null,
                Country: null,
                Region: null,
                Status: null))
            .ToList();

        var upsertResult = await mms.UpsertSitesAsync(upserts, ct);

        report.Created += upsertResult.Sites.Count(s => s.Created);
        report.Updated += upsertResult.Sites.Count(s => !s.Created);

        foreach (var rejection in upsertResult.Rejections)
        {
            logger.LogWarning(
                "MMS rejected site {Key}: {Reason} (transient: {Transient}).",
                rejection.Key, rejection.Reason, rejection.Transient);
        }

        // A site MMS refused for a reason that may pass is grounds to keep the
        // message: acknowledging it would lose the site permanently.
        if (upsertResult.Rejections.Any(r => r.Transient))
        {
            return false;
        }

        report.CirEntriesRegistered += await RegisterInCirAsync(mapped, ct);

        return true;
    }

    /// <summary>
    /// Registers the sites in CIR under MMS's own key space.
    ///
    /// This is what makes MMS's site resolvable by systems that do not speak
    /// MMS. The CIRID is the site's federation GUID, the same one CMS and
    /// REG-LOCATION registered against their own identifiers -- which is
    /// precisely how the registry relates them without any system learning
    /// another's keys.
    /// </summary>
    private async Task<int> RegisterInCirAsync(
        IReadOnlyList<SiteMappingResult> mapped, CancellationToken ct)
    {
        if (!_options.RegisterInCir || string.IsNullOrWhiteSpace(_options.CirBaseUrl))
        {
            return 0;
        }

        var entries = mapped
            .Select(m => new CirEntry(
                // MMS's own identifier for the site. SiteId doubles as it here
                // because the provider takes the publisher's GUID as its key.
                IdInSource: m.SiteId.ToString(),
                SourceId: _options.SourceId,
                Cirid: m.SiteId,
                SourceOwnerId: _options.Enterprise,
                Name: m.SiteCode,
                Description: new CirLocalizedText(m.Description ?? m.SiteName),
                Properties: []))
            .ToList();

        var request = new CreateRegistryRequest(
            [
                new CirRegistry(
                    Id: _options.Enterprise,
                    Description: [new CirLocalizedText("Sites")],
                    Categories:
                    [
                        new CirCategory(
                            Id: "ITWIN-SITE",
                            SourceId: _options.SourceId,
                            Description: [new CirLocalizedText("MMS sites")],
                            Entries: entries)
                    ])
            ],
            CreateCirid: false);

        try
        {
            return await cir.RegisterEntriesAsync(request, ct);
        }
        catch (CirClientException ex)
        {
            // Not fatal. The site is already in MMS, and failing here would leave
            // it unrecorded and blocked behind a CIR that is still down.
            logger.LogError(ex, "Registering {Count} MMS site(s) in CIR failed.", entries.Count);
            return 0;
        }
    }
}
