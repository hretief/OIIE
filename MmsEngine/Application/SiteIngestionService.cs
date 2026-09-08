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

    /// <summary>Owners newly created in MMS.</summary>
    public int Created { get; set; }

    /// <summary>Owners that already existed and were matched or renamed.</summary>
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
/// Receives SyncSites from ENG and records each site in MMS as a TAMS owner.
///
/// This is how a site comes to exist in MMS. Nothing else creates one:
/// SETUP_OWNER is otherwise reference data, and MmsProvider is reachable only
/// through its own REST API, so the only route from "ENG published a site" to
/// "MMS knows about it" is this service. That is deliberate -- a maintenance
/// system that invented its own sites would be asserting something only the
/// enterprise can.
///
/// A site is an owner, not a light system. TAMS raises work orders against an
/// owner, which is the context a site denotes; light systems are the inventory
/// standing within one. The identity is resolved through CIR rather than stored
/// locally, because SETUP_OWNER has no column for a federation GUID and adding
/// one would mean this emulator no longer matched the customer schema it exists
/// to emulate.
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

        // The subscriber id names this engine's role on the channel rather than
        // this process. _sessionId lives in a field, so it is lost on every
        // restart and redeploy -- and without a stable id the broker would mint a
        // fresh subscription each time, stranding whatever the previous one had
        // not yet read. The enterprise channel is shared, so the id says which
        // reader this is, not which site.
        _sessionId ??= await isbm.OpenSubscriptionSessionAsync(
            _options.SitesChannelUri, _options.SitesTopics, ct,
            subscriberId: $"{_options.SourceId}:sites");

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
            // the owner is re-resolved through CIR and then by name, so a site
            // already recorded is matched rather than created a second time.
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

        // "Sites", plural: the noun is the BOD's root name with the verb
        // removed, so SyncSites yields Sites. The singular matched nothing, and
        // an unrecognised BOD is skipped rather than failed, so this leg
        // reported success while registering no sites at all.
        if (!envelope.Is("Sync", "Sites"))
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

        // Resolve every site to an owner before writing any of them, so a
        // publication carrying several is one round trip to MMS rather than one
        // per site.
        var resolved = new List<ResolvedSite>();

        foreach (var site in mapped)
        {
            long? ownerId;
            try
            {
                ownerId = await ResolveOwnerIdAsync(site, ct);
            }
            catch (CirClientException ex)
            {
                // Unlike registration, a failed lookup is fatal to this pass.
                // Proceeding would treat "CIR is down" as "this site is new" and
                // create a duplicate owner alongside the one CIR already knows.
                logger.LogError(
                    ex,
                    "Could not resolve site '{Name}' in CIR; leaving publication {MessageId} on the channel.",
                    site.OwnerName, message.MessageId);

                return false;
            }

            resolved.Add(new ResolvedSite(site, ownerId));
        }

        var upserts = resolved
            .Select(r => new OwnerUpsert(r.Site.OwnerName, r.OwnerId))
            .ToList();

        OwnerUpsertResult upsertResult;
        try
        {
            upsertResult = await mms.UpsertOwnersAsync(upserts, ct);
        }
        catch (MmsClientException ex)
        {
            logger.LogError(
                ex,
                "MMS refused the owner upsert for publication {MessageId}; leaving it on the channel.",
                message.MessageId);

            return false;
        }

        report.Created += upsertResult.Owners.Count(o => o.Created);
        report.Updated += upsertResult.Owners.Count(o => !o.Created);

        foreach (var rejection in upsertResult.Rejections)
        {
            logger.LogWarning(
                "MMS rejected owner {Key}: {Reason} (transient: {Transient}).",
                rejection.Key, rejection.Reason, rejection.Transient);
        }

        // A site MMS refused for a reason that may pass is grounds to keep the
        // message: acknowledging it would lose the site permanently.
        if (upsertResult.Rejections.Any(r => r.Transient))
        {
            return false;
        }

        // Registered against the OWNER_ID MMS just assigned, matched back to the
        // site by name because that is what was sent. Done for owners that
        // already existed as well as new ones: MMS keeps no CIRID of its own, so
        // the registry entry is the only record relating this site to this owner,
        // and skipping it for an existing owner would leave that relation
        // unrecorded forever.
        var registrations = resolved
            .Select(r => new
            {
                r.Site,
                Owner = upsertResult.Owners.FirstOrDefault(o =>
                    string.Equals(o.OwnerName, r.Site.OwnerName, StringComparison.OrdinalIgnoreCase))
            })
            .Where(x => x.Owner is not null)
            .Select(x => new ResolvedSite(x.Site, x.Owner!.OwnerId))
            .ToList();

        report.CirEntriesRegistered += await RegisterInCirAsync(registrations, ct);

        return true;
    }

    /// <summary>
    /// One mapped site paired with the SETUP_OWNER row it resolved to, or null
    /// when no owner could be found and one must be created.
    /// </summary>
    private sealed record ResolvedSite(SiteMappingResult Site, long? OwnerId);

    /// <summary>
    /// Finds the OWNER_ID this site already corresponds to, or null if it is new.
    ///
    /// CIR is asked first and the name only afterwards, and the order carries
    /// the meaning. The registry holds the durable relation between the site's
    /// federation GUID and MMS's key, so a site that was renamed upstream is
    /// still recognised as the owner it always was. Matching on name first would
    /// miss it and create a second owner for the same site.
    ///
    /// The name fallback is what lets this leg adopt owners MMS already had --
    /// the customer's own seeded districts, which exist in SETUP_OWNER long
    /// before ENG publishes anything and were never registered in CIR. Without
    /// it, the first publication would duplicate every one of them.
    /// </summary>
    private async Task<long?> ResolveOwnerIdAsync(SiteMappingResult site, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.CirBaseUrl))
        {
            var idInSource = await cir.FindIdInSourceAsync(site.Cirid, _options.SourceId, ct);

            if (long.TryParse(idInSource, out var registeredOwnerId))
            {
                return registeredOwnerId;
            }

            if (!string.IsNullOrWhiteSpace(idInSource))
            {
                // Registered, but not as something SETUP_OWNER could be keyed by.
                // Reported rather than trusted: it is likely a stale registration
                // from when this leg wrote light system GUIDs into IdInSource.
                logger.LogWarning(
                    "CIR holds '{IdInSource}' for site {Cirid} under source {SourceId}, " +
                    "which is not an OWNER_ID; falling back to matching on name.",
                    idInSource, site.Cirid, _options.SourceId);
            }
        }

        // Case-insensitive because the customer's owner names are entered by
        // hand and an incoming 'District 3' should find 'DISTRICT 3'. The mapper
        // has already trimmed the incoming name.
        var owners = await mms.GetOwnersAsync(ct);

        var match = owners.FirstOrDefault(o =>
            string.Equals(o.Name?.Trim(), site.OwnerName, StringComparison.OrdinalIgnoreCase));

        return match?.Id;
    }

    /// <summary>
    /// Registers the light systems in CIR under MMS's own key space.
    ///
    /// This is what makes MMS's light system resolvable by systems that do not
    /// speak MMS. The CIRID is the site's federation GUID, the same one CMS and
    /// REG-LOCATION registered against their own identifiers -- which is
    /// precisely how the registry relates them without any system learning
    /// another's keys.
    /// </summary>
    /// <summary>
    /// Registers the owners in CIR under MMS's own key space.
    ///
    /// This is what makes MMS's owner resolvable by systems that do not speak
    /// MMS. The CIRID is the site's federation GUID, the same one CMS and
    /// REG-LOCATION registered against their own identifiers -- which is
    /// precisely how the registry relates them without any system learning
    /// another's keys.
    ///
    /// IdInSource is the OWNER_ID, not the site GUID. The registry's purpose is
    /// to answer "what does MMS call this?", and MMS calls it by an integer:
    /// echoing the CIRID back would register the question as its own answer and
    /// leave the owner unreachable. It is also what the next drain reads to
    /// recognise this site again.
    /// </summary>
    private async Task<int> RegisterInCirAsync(
        IReadOnlyList<ResolvedSite> registered, CancellationToken ct)
    {
        if (!_options.RegisterInCir || string.IsNullOrWhiteSpace(_options.CirBaseUrl))
        {
            return 0;
        }

        var entries = registered
            .Where(r => r.OwnerId is not null)
            .Select(r => new CirEntry(
                IdInSource: r.OwnerId!.Value.ToString(),
                SourceId: _options.SourceId,
                Cirid: r.Site.Cirid,
                SourceOwnerId: _options.Enterprise,
                Name: r.Site.OwnerName,
                Description: new CirLocalizedText(r.Site.OwnerName),
                Properties: []))
            .ToList();

        if (entries.Count == 0)
        {
            return 0;
        }

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
                            Description: [new CirLocalizedText("MMS owners")],
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
            // Not fatal. The owner is already in MMS, and failing here would
            // leave it unrecorded and blocked behind a CIR that is still down.
            // The next drain re-resolves by name and registers again.
            logger.LogError(ex, "Registering {Count} MMS owner(s) in CIR failed.", entries.Count);
            return 0;
        }
    }
}
