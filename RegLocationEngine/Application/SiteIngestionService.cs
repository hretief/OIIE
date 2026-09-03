using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using Oiie.Isbm.Client;
using RegLocationEngine.Infrastructure.Cir;
using RegLocationEngine.Infrastructure.RegLocation;

namespace RegLocationEngine.Application;

/// <summary>What one drain of the enterprise sites channel did.</summary>
public sealed class SiteIngestionReport
{
    public int MessagesRead { get; set; }
    public int SitesSeen { get; set; }

    /// <summary>Sites whose scope the registry did not already hold.</summary>
    public int SitesRegistered { get; set; }

    /// <summary>Sites already present, left as they were.</summary>
    public int AlreadyKnown { get; set; }

    /// <summary>Site types reused rather than created.</summary>
    public int TypesReused { get; set; }

    /// <summary>Sites that could not be registered.</summary>
    public int Rejected { get; set; }

    /// <summary>Channels provisioned for newly registered sites.</summary>
    public int ChannelsCreated { get; set; }

    /// <summary>CIR entries written.</summary>
    public int CirEntriesRegistered { get; set; }

    /// <summary>Sites removed on a Delete action.</summary>
    public int SitesRemoved { get; set; }

    /// <summary>
    /// Sites a delete named that the registry did not hold.
    ///
    /// Counted rather than treated as an error: a redelivered deletion finds the
    /// scope the first delivery removed, and that is success, not a fault.
    /// </summary>
    public int AlreadyRemoved { get; set; }

    /// <summary>CIR entries removed.</summary>
    public int CirEntriesRemoved { get; set; }

    /// <summary>Per-iTwin channels torn down after a site was removed.</summary>
    public int ChannelsRemoved { get; set; }

    /// <summary>Messages left on the channel because handling them failed.</summary>
    public int Failed { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// Receives SyncSites from ENG and establishes the site in REG-LOCATION.
///
/// This is the flow every other flow depends on. SyncSegments has nowhere to
/// land until a Scope exists to hold its functional locations, and the per-iTwin
/// channels it travels on do not exist either. So this service does more than
/// file a row: it creates the registry context and provisions the channels that
/// context implies.
///
/// Unlike <see cref="SegmentIngestionService"/>, which proposes and lets a
/// steward dispose, this leg writes directly. There is no gate here and there
/// should not be one: a site is not an assertion about the plant that a steward
/// might reject, it is the context within which such assertions are later made.
/// Holding sites in a queue would stall every downstream flow behind a decision
/// with nothing to decide.
/// </summary>
public sealed class SiteIngestionService(
    IIsbmClient isbm,
    IRegLocationClient regLocation,
    ICirClient cir,
    IncomingSiteMapper mapper,
    IOptions<RegLocationEngineOptions> options,
    ILogger<SiteIngestionService> logger)
{
    private readonly RegLocationEngineOptions _options = options.Value;

    /// <summary>
    /// The subscription session, cached across polls for the same reason as the
    /// segment leg's: a session is the broker's record of what this consumer has
    /// already seen, and reopening one each pass either replays the channel or
    /// silently skips what arrived in between.
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

        if (string.IsNullOrWhiteSpace(_options.RegLocationBaseUrl))
        {
            report.Note = "RegLocationBaseUrl is not configured, so sites cannot be registered.";
            logger.LogWarning("{Note}", report.Note);
            return report;
        }

        // No ITwinFederationId check, unlike the segment leg. That one derives its channel
        // from an iTwin; this one cannot, because the message it reads is what
        // brings an iTwin into existence.
        //
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
                // Left on the channel rather than discarded, as in the segment
                // leg: a fix can be deployed and the message reprocessed. Here
                // the argument is stronger, because a site left unregistered
                // blocks every segment that would have landed in its scope.
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

            // Removed only after every step for every site in it has succeeded.
            // A crash before this point redelivers the message, which is why
            // each step looks the entity up by GUID first.
            await isbm.RemovePublicationAsync(_sessionId, ct);
        }

        if (report.MessagesRead > 0)
        {
            logger.LogInformation(
                "Site drain read {Messages} message(s): {Registered} registered, {Known} already known, " +
                "{Rejected} rejected, {Channels} channel(s) created, {Failed} left on the channel.",
                report.MessagesRead, report.SitesRegistered, report.AlreadyKnown,
                report.Rejected, report.ChannelsCreated, report.Failed);
        }

        return report;
    }

    /// <summary>
    /// Establishes every site in one publication. Returns false when the message
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

        // "Sites", plural: the noun is the BOD's root name with the verb removed,
        // so SyncSites yields Sites the same way SyncSegments yields Segments.
        // The singular silently matched nothing, and because an unrecognised BOD
        // is skipped rather than failed, the whole leg reported success while
        // registering no sites at all.
        if (!envelope.Is("Sync", "Sites"))
        {
            logger.LogDebug(
                "Publication {MessageId} was {Verb}{Noun}, which this leg does not handle.",
                message.MessageId, envelope.Verb, envelope.Noun);

            return true;
        }

        // Add and Delete arrive as the same Sync/Sites BOD and are told apart by
        // the action code, not the verb element. OAGIS derives the noun by
        // stripping the verb from the root name, so a DeleteSites root would parse
        // to the noun 'Sites' only if the root were named for the Delete verb --
        // and SyncSites is not. Keeping the pair intact and reading the action
        // code is both what the code is for and the only form that reaches here.
        var isDelete = string.Equals(
            envelope.ActionCode, ActionCodes.Delete, StringComparison.OrdinalIgnoreCase);

        foreach (var site in envelope.NounsAs(e => new Site(e)))
        {
            report.SitesSeen++;

            if (isDelete)
            {
                await RemoveAsync(site, report, envelope.BodId, ct);
                continue;
            }

            var mapped = mapper.Map(site);

            if (!mapped.IsMapped)
            {
                report.Rejected++;

                logger.LogWarning(
                    "Site '{Name}' in {MessageId} rejected: {Reason} [{CorrelationId}].",
                    site.ShortName ?? site.IDInInfoSource ?? "(unnamed)",
                    message.MessageId,
                    mapped.Rejection,
                    envelope.BodId);

                continue;
            }

            await EstablishAsync(mapped, report, envelope.BodId, ct);
        }

        return true;
    }

    /// <summary>
    /// The spec's Steps 6 to 12 for one site.
    ///
    /// Ordered by dependency rather than by the spec's numbering, though they
    /// coincide: the Scope must exist before the Item can name it, the Item
    /// before the Serial can be an instance of it, and the Serial before the
    /// Scope can point back at it. That last one is why the context link is a
    /// separate write rather than part of scope creation.
    ///
    /// Every step asks the registry whether its work is already done. That, and
    /// not any engine-side record, is what makes redelivery harmless: the
    /// registry is the thing that actually knows, and its answer stays correct
    /// if this engine's state is lost or the same site arrives twice.
    /// </summary>
    private async Task EstablishAsync(
        SiteMappingResult mapped, SiteIngestionReport report, string? correlationId, CancellationToken ct)
    {
        // Step 6: the Scope.
        var scope = await regLocation.FindScopeByGuidAsync(mapped.SiteGuid, ct);
        var scopeExisted = scope is not null;

        scope ??= await regLocation.CreateScopeAsync(mapper.ToScopeRequest(mapped), ct);

        if (scopeExisted)
        {
            report.AlreadyKnown++;

            logger.LogDebug(
                "Site {Guid} is already registered as scope {ScopeId}; left untouched.",
                mapped.SiteGuid, scope.ScopeId);
        }

        // Step 7: the Item, which is the site *type* and is shared. Found by the
        // type's GUID, not the site's, so the second Highway project reuses the
        // first one's classification rather than creating a rival to it.
        var item = await regLocation.FindItemByGuidAsync(mapped.TypeGuid, ct);

        if (item is not null)
        {
            report.TypesReused++;
        }
        else
        {
            item = await regLocation.CreateItemAsync(
                mapper.ToItemRequest(mapped, scope.ScopeId), ct);

            logger.LogInformation(
                "Registered site type '{Type}' as item {ItemId} [{CorrelationId}].",
                mapped.TypeCode, item.ItemId, correlationId);
        }

        // Step 8: the Serial, which is this particular site.
        var serial = await regLocation.FindSerialByGuidAsync(mapped.SiteGuid, ct);

        serial ??= await regLocation.CreateSerialAsync(
            mapper.ToSerialRequest(mapped, item.ItemId, scope.ScopeId), ct);

        // Step 9: point the scope at the serial. Written whenever the link is
        // absent rather than only on creation, because a previous run may have
        // crashed between creating the serial and linking it -- and a scope with
        // no context is exactly what that partial failure leaves behind.
        if (scope.ContextObjectId != serial.Object.ObjectId)
        {
            await regLocation.SetScopeContextAsync(
                scope.ScopeId,
                new SetScopeContextRequest(
                    ContextObjectId: serial.Object.ObjectId,
                    ContextObjectType: serial.Object.ObjectType),
                ct);
        }

        if (!scopeExisted)
        {
            report.SitesRegistered++;

            logger.LogInformation(
                "Registered site '{Name}' as scope {ScopeId} / serial {SerialId} [{CorrelationId}].",
                mapped.Name, scope.ScopeId, serial.Serial.SerialId, correlationId);
        }

        // Step 11: CIR.
        report.CirEntriesRegistered += await RegisterInCirAsync(mapped, scope.ScopeId, item.ItemId, ct);

        // Step 12: the per-iTwin channels this site's later traffic will use.
        report.ChannelsCreated += await BootstrapChannelsAsync(mapped, ct);
    }

    /// <summary>
    /// Removes a site: its CIR identity, its registry rows, and its channels.
    /// </summary>
    /// <remarks>
    /// The reverse of <see cref="EstablishAsync"/>, and ordered CIR-first for a
    /// reason that is not symmetry. Deleting the registry rows first would leave
    /// CIR entries keyed on a scopeId that no longer exists, and REG-LOCATION
    /// allocates scope ids as MAX+1 -- so the next site created would take that
    /// id and collide with the stale entry. A failure after the CIR step leaves
    /// rows without identity, which the next delivery repairs; a failure after
    /// the Provider step, had the order been reversed, would poison a site
    /// nobody has created yet.
    ///
    /// Every step tolerates its work having already been done, because a delete
    /// that fails halfway is redelivered and must be able to finish.
    /// </remarks>
    private async Task RemoveAsync(
        Site site, SiteIngestionReport report, string? correlationId, CancellationToken ct)
    {
        var siteGuid = site.UUID;

        if (siteGuid == Guid.Empty)
        {
            report.Rejected++;

            logger.LogWarning(
                "A site deletion carried no UUID and names nothing to remove [{CorrelationId}].",
                correlationId);

            return;
        }

        var scope = await regLocation.FindScopeByGuidAsync(siteGuid, ct);

        if (scope is null)
        {
            // Nothing left to do, and not a failure: either this is a redelivery,
            // or the site was never registered here. The channels are still torn
            // down below, since a scope can be absent while its channels survive
            // an earlier partial delete.
            report.AlreadyRemoved++;

            logger.LogInformation(
                "REG-LOCATION holds no site {SiteGuid:D}; nothing to remove [{CorrelationId}].",
                siteGuid, correlationId);

            report.ChannelsRemoved += await RemoveChannelsAsync(siteGuid, ct);
            return;
        }

        // Read before the delete, because afterwards there is no scope to read it
        // from. The item id is half the CIR key and cannot be recovered later.
        var itemId = await ResolveSiteTypeItemIdAsync(siteGuid, scope.ScopeId, ct);

        // The site's own identity goes first and unconditionally: it belongs to
        // this scope alone, and this scope is being destroyed.
        report.CirEntriesRemoved += await CancelInCirAsync(
            [CirKeyFor("ITWIN-SITE", scope.ScopeId)], ct);

        var cascade = await regLocation.DeleteScopeCascadeAsync(scope.ScopeId, ct);

        if (cascade is null)
        {
            report.AlreadyRemoved++;
        }
        else
        {
            report.SitesRemoved++;

            logger.LogInformation(
                "Removed site {SiteGuid:D} as scope {ScopeId}: {Tags} tag(s), {Serials} serial(s), " +
                "{Items} item(s), {Scopes} scope(s) [{CorrelationId}].",
                siteGuid, scope.ScopeId, cascade.TagsDeleted, cascade.SerialsDeleted,
                cascade.ItemsDeleted, cascade.ScopesDeleted, correlationId);

            // The site type's identity goes only if the type itself went. A type
            // is shared across sites, and the Provider has just answered whether
            // this was the last reference -- so that answer is used rather than a
            // second guard here that could disagree with it. Cancelling a type
            // another site still uses would leave that site's item with no
            // resolvable identity.
            if (cascade.ItemsDeleted > 0 && itemId is { } id)
            {
                report.CirEntriesRemoved += await CancelInCirAsync(
                    [CirKeyFor("SITE-TYPE", id)], ct);
            }
        }

        report.ChannelsRemoved += await RemoveChannelsAsync(siteGuid, ct);
    }

    /// <summary>
    /// The item id behind a scope's site type, or null if it cannot be resolved.
    /// </summary>
    /// <remarks>
    /// Reached through the Serial representing this site, which carries the same
    /// federation GUID as the scope by design, and whose item is the site type.
    /// That indirection is the only path: the scope itself does not name its type.
    ///
    /// Null is tolerable. It costs a stale SITE-TYPE entry, which is far cheaper
    /// than the alternative failure -- ITWIN-SITE entries collide on reused scope
    /// ids, but a SITE-TYPE entry is keyed on an item id that is only reused if
    /// the type is recreated, and then it is the same type.
    /// </remarks>
    private async Task<int?> ResolveSiteTypeItemIdAsync(
        Guid siteGuid, int scopeId, CancellationToken ct)
    {
        try
        {
            var serial = await regLocation.FindSerialByGuidAsync(siteGuid, ct);
            return serial?.Serial.ItemId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Could not resolve the site type for scope {ScopeId}.", scopeId);

            return null;
        }
    }

    private CirEntryIdentifier CirKeyFor(string categoryId, int idInSource) => new(
        RegistryId: _options.Enterprise,
        CategoryId: categoryId,

        // Both source fields are this engine's own SourceId, which is what makes
        // these entries ours to cancel: an engine may remove exactly the
        // categories whose CategorySourceId matches its SourceId.
        CategorySourceId: _options.SourceId,

        // The REG-LOCATION integer id, not the federation GUID. The GUID is the
        // entry's Cirid; keying a cancel on it matches nothing and silently
        // removes nothing.
        EntryIdInSource: idInSource.ToString(),
        EntrySourceId: _options.SourceId);

    private async Task<int> CancelInCirAsync(
        IReadOnlyList<CirEntryIdentifier> entries, CancellationToken ct)
    {
        if (!_options.RegisterInCir || string.IsNullOrWhiteSpace(_options.CirBaseUrl))
            return 0;

        try
        {
            return await cir.CancelEntriesAsync(entries, ct);
        }
        catch (CirClientException ex)
        {
            // Raised rather than swallowed, unlike the registration path. A site
            // whose CIR identity survives its registry rows is the precise
            // condition that breaks the next site to reuse its scope id, so the
            // message stays on the channel and the delete is retried.
            logger.LogError(ex, "Cancelling CIR entries failed.");
            throw;
        }
    }

    /// <summary>
    /// Tears down the two per-iTwin channels bootstrapped for this site.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="BootstrapChannelsAsync"/> exactly, and derives both
    /// URIs from the same options rather than naming the domains: a delete list
    /// that names channels the bootstrap never created would leave the real ones
    /// behind while appearing to succeed.
    /// </remarks>
    private async Task<int> RemoveChannelsAsync(Guid siteGuid, CancellationToken ct)
    {
        var removed = 0;

        foreach (var uri in new[]
        {
            _options.InboundChannelUriFor(siteGuid),
            _options.ChannelUriFor(siteGuid)
        })
        {
            try
            {
                if (await isbm.GetChannelAsync(uri, ct) is null)
                    continue;

                await isbm.DeleteChannelAsync(uri, ct);
                removed++;

                logger.LogInformation("Removed channel {ChannelUri}.", uri);
            }
            catch (Exception ex)
            {
                // Not fatal, matching the bootstrap. The site is gone from the
                // registry either way, and a channel that outlives it is inert:
                // nothing publishes to a site that no longer exists. Failing the
                // message here would replay the whole delete to retry a teardown.
                logger.LogError(ex, "Removing channel {ChannelUri} failed.", uri);
            }
        }

        return removed;
    }

    /// <summary>
    /// Registers the site and its type in CIR.
    ///
    /// Two categories rather than one, because they are different kinds of
    /// identity: ITWIN-SITE entries are instances and SITE-TYPE entries are
    /// classifications, and a consumer resolving a CIRID needs to know which it
    /// has found. Both carry the GUID that arrived rather than one CIR mints,
    /// for the same reason the tag registration does -- the whole point is that
    /// this is the same site the platform already identified.
    /// </summary>
    private async Task<int> RegisterInCirAsync(
        SiteMappingResult mapped, int scopeId, int itemId, CancellationToken ct)
    {
        if (!_options.RegisterInCir || string.IsNullOrWhiteSpace(_options.CirBaseUrl))
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
                            Description: [new CirLocalizedText("REG-LOCATION sites")],
                            Entries:
                            [
                                new CirEntry(
                                    IdInSource: scopeId.ToString(),
                                    SourceId: _options.SourceId,
                                    Cirid: mapped.SiteGuid,
                                    SourceOwnerId: _options.Enterprise,
                                    Name: mapped.Name,
                                    Description: new CirLocalizedText(mapped.Description ?? mapped.Name),
                                    Properties: [])
                            ]),

                        new CirCategory(
                            Id: "SITE-TYPE",
                            SourceId: _options.SourceId,
                            Description: [new CirLocalizedText("REG-LOCATION site types")],
                            Entries:
                            [
                                new CirEntry(
                                    IdInSource: itemId.ToString(),
                                    SourceId: _options.SourceId,
                                    Cirid: mapped.TypeGuid,
                                    SourceOwnerId: _options.Enterprise,
                                    Name: mapped.TypeCode,
                                    Description: new CirLocalizedText(mapped.TypeDescription ?? mapped.TypeCode),
                                    Properties: [])
                            ])
                    ])
            ],
            CreateCirid: false);

        try
        {
            var written = await cir.RegisterEntriesAsync(request, ct);

            logger.LogInformation(
                "CIR registration for site '{Name}' wrote {Written} entry(ies) " +
                "(scope {ScopeId}, item {ItemId}).",
                mapped.Name, written, scopeId, itemId);

            return written;
        }
        catch (CirClientException ex)
        {
            // Not fatal, matching the approval service: the registry rows are
            // already written, and failing here would leave the site
            // unestablished and blocked behind a CIR that is still down.
            logger.LogError(ex, "Registering site '{Name}' in CIR failed.", mapped.Name);
            return 0;
        }
    }

    /// <summary>
    /// Provisions the per-iTwin channels this site's later traffic uses.
    ///
    /// Done here, at the moment the site becomes real, because the alternative
    /// is provisioning them on first publication -- which means the first
    /// SyncSegments for a new site fails against a channel that does not exist
    /// yet, and looks like a broker fault rather than a missing bootstrap step.
    /// </summary>
    private async Task<int> BootstrapChannelsAsync(SiteMappingResult mapped, CancellationToken ct)
    {
        var created = 0;

        foreach (var uri in new[]
        {
            _options.InboundChannelUriFor(mapped.SiteGuid),
            _options.ChannelUriFor(mapped.SiteGuid)
        })
        {
            try
            {
                if (await isbm.GetChannelAsync(uri, ct) is not null)
                {
                    continue;
                }

                await isbm.CreateChannelAsync(
                    uri,
                    IsbmChannelType.Publication,

                    // The readable name lives here, which is why the URI does not
                    // need to carry it: a channel named by federation id survives
                    // the site being renamed, and this is where someone reading
                    // the broker's channel list finds out what it is.
                    description: mapped.Name,
                    ct: ct);

                created++;

                logger.LogInformation("Provisioned channel {ChannelUri} for site '{Name}'.", uri, mapped.Name);
            }
            catch (Exception ex)
            {
                // Not fatal. The site is registered, and a channel that could not
                // be created will be retried the next time this site is
                // published -- whereas failing the whole message would leave the
                // registry rows written and the message still on the channel.
                logger.LogError(ex, "Provisioning channel {ChannelUri} failed.", uri);
            }
        }

        return created;
    }
}
