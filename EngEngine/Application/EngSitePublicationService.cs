using Oiie.Cir.Client;
using EngEngine.Infrastructure.Eng;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Isbm.Client;

namespace EngEngine.Application;

public sealed record EngSitePublicationReport
{
    public int SitesSeen { get; init; }
    public int SitesPublished { get; init; }

    /// <summary>
    /// Per-iTwin channels provisioned during this run. Zero is the steady state
    /// once a twin has been published before, so this counts work done rather
    /// than channels that exist.
    /// </summary>
    public int ChannelsCreated { get; init; }

    /// <summary>
    /// CIR entries written for the twins in this run. Zero is likewise normal:
    /// CIR reports an identity it already holds as nothing-written.
    /// </summary>
    public int CirEntriesWritten { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// Sites passed over rather than published, with the reason. A twin with no
    /// type is a question for whoever registered it, and the person running the
    /// publication is the one who can ask.
    /// </summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];
}

/// <summary>
/// Announces iTwins onto the enterprise sites channel.
///
/// Separate from <see cref="EngPublicationService"/> rather than another branch
/// inside it, because the two flows differ in every respect that matters. That
/// one is a poll with a watermark over a stream of markers; this one is an act
/// performed on a named twin. That one publishes onto the twin's own channel;
/// this one cannot, because the twin's channel is not addressable by a message
/// whose purpose is to announce that the twin exists.
///
/// This service also establishes the twin before announcing it: it provisions
/// the per-iTwin channels and registers the twin in CIR, then publishes. ENG
/// does that work because ENG is where the twin comes from. The identity CIR
/// holds is ENG's own primary key, and the channels are named for a federation
/// id only ENG knows at creation time; a consumer doing either on ENG's behalf
/// is recording something it inferred from a message rather than something it
/// owns, and cannot do it until after the message it was needed for.
///
/// There is no watermark and no published-set here. SyncSites goes out as
/// Replace and the receiver upserts on Site.UUID, so republishing a site is
/// harmless -- and it is sometimes exactly what is wanted, when a twin has been
/// renamed or a subscriber has been rebuilt. Suppressing repeats would turn a
/// useful operation into one that silently does nothing.
/// </summary>
public sealed class EngSitePublicationService(
    IEngClient eng,
    IIsbmClient isbm,
    ICirClient cir,
    EngSitesBuilder builder,
    IOptions<EngEngineOptions> options,
    ILogger<EngSitePublicationService> logger)
{
    private readonly EngEngineOptions _options = options.Value;

    /// <summary>
    /// Publishes one iTwin, or every iTwin ENG holds when none is named.
    ///
    /// The all-sites form is what bootstraps a fresh sandbox: the registry needs
    /// every existing site before any design can flow, and asking an operator to
    /// name them one at a time invites the one that gets forgotten.
    /// </summary>
    public async Task<EngSitePublicationReport> PublishAsync(
        Guid? iTwinId = null, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            logger.LogDebug("EngEngine is disabled.");
            return new EngSitePublicationReport();
        }

        if (string.IsNullOrWhiteSpace(_options.EngBaseUrl))
            return Failed("EngEngine__EngBaseUrl is not configured.");

        if (string.IsNullOrWhiteSpace(_options.Enterprise))
            return Failed("EngEngine__Enterprise is not configured.");

        var twins = await eng.GetITwinsAsync(ct);

        if (iTwinId is { } wanted)
        {
            twins = twins.Where(t => t.ITwinId == wanted).ToList();

            if (twins.Count == 0)
                return Failed($"ENG does not have iTwin '{wanted:D}'.");
        }

        var errors = new List<string>();
        var skipped = new List<string>();
        var publishedCount = 0;
        var channelsCreated = 0;
        var cirEntriesWritten = 0;

        string? sessionId = null;

        foreach (var twin in twins)
        {
            if (!EngSitesBuilder.IsPublishable(twin))
            {
                skipped.Add($"{twin.Handle}: has no site type.");
                continue;
            }

            try
            {
                // Established before announced, and in this order deliberately.
                // A subscriber reacting to SyncSites may go looking for the
                // twin's channel or its CIR entry immediately; if either is
                // created after the message goes out, that lookup races the
                // publication and fails intermittently -- the kind of fault
                // that reproduces only under load. Doing both first means the
                // announcement is the last thing that happens, so anything it
                // refers to is already there.
                channelsCreated += await ProvisionChannelsAsync(twin, ct);
                cirEntriesWritten += await RegisterInCirAsync(twin, ct);

                // Opened lazily, and not closed afterwards: the client caches
                // publication sessions per channel and reuses them across runs,
                // as the marker drain does. Closing here would discard a session
                // the next publication would have to re-establish.
                sessionId ??= await isbm.OpenPublicationSessionAsync(
                    _options.SitesChannelUri, ct);

                var correlationId = Guid.NewGuid().ToString();
                var content = builder.Build(twin, correlationId);

                var messageId = await isbm.PostPublicationAsync(
                    sessionId,
                    content,
                    _options.SitesTopics,
                    ct: ct);

                publishedCount++;

                logger.LogInformation(
                    "Published site '{Site}' ({ITwinId:D}) as {MessageId} [{CorrelationId}].",
                    twin.Handle, twin.ITwinId, messageId, correlationId);
            }
            catch (Exception ex)
            {
                // Recorded and carried on, unlike the marker drain. There is no
                // watermark to corrupt here, and one unpublishable twin should
                // not hold back the rest of a bootstrap.
                logger.LogError(ex, "Publishing site '{Site}' failed.", twin.Handle);
                errors.Add($"{twin.Handle}: {ex.Message}");
            }
        }

        return new EngSitePublicationReport
        {
            SitesSeen = twins.Count,
            SitesPublished = publishedCount,
            ChannelsCreated = channelsCreated,
            CirEntriesWritten = cirEntriesWritten,
            Errors = errors,
            Skipped = skipped
        };
    }

    /// <summary>
    /// Provisions the per-iTwin channels the twin's later traffic uses.
    /// </summary>
    /// <remarks>
    /// Non-fatal per channel, and per twin. A channel that could not be created
    /// is retried the next time the twin is published, whereas failing the run
    /// would stop a bootstrap of many twins at the first broker hiccup and leave
    /// the rest unannounced.
    ///
    /// Creation is conditional on a lookup rather than relying on a
    /// create-if-absent: the broker answers an existing channel with a fault,
    /// and treating that fault as success would hide the case where the channel
    /// exists with the wrong type.
    /// </remarks>
    private async Task<int> ProvisionChannelsAsync(EngITwin twin, CancellationToken ct)
    {
        if (!_options.ProvisionITwinChannels)
            return 0;

        var created = 0;

        foreach (var domain in _options.ITwinChannelDomains)
        {
            var uri = _options.ChannelUriFor(twin.ITwinId, domain);

            try
            {
                if (await isbm.GetChannelAsync(uri, ct) is not null)
                    continue;

                await isbm.CreateChannelAsync(
                    uri,
                    IsbmChannelType.Publication,

                    // The readable name lives here, which is why the URI does
                    // not need to carry it: a channel named by federation id
                    // survives the twin being renamed, and this is where someone
                    // reading the broker's channel list finds out what it is.
                    description: twin.Handle,
                    ct: ct);

                created++;

                logger.LogInformation(
                    "Provisioned channel {ChannelUri} for iTwin '{Site}'.", uri, twin.Handle);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Provisioning channel {ChannelUri} failed.", uri);
            }
        }

        return created;
    }

    /// <summary>
    /// Registers the twin in CIR under ENG's own key.
    /// </summary>
    /// <remarks>
    /// IdInSource and Cirid are both the iTwin GUID here, and that is a fact
    /// about ENG rather than a shortcut: ENG's internal key for a twin *is* its
    /// federation id, so this entry records that the two coincide at the source.
    /// Other participants differ -- REG-LOCATION carries an integer scope id
    /// against the same Cirid -- and it is exactly that difference CIR exists to
    /// hold.
    ///
    /// Because they coincide here, the two fields are the easiest place in the
    /// system for a formatting difference to hide: written differently they
    /// would still look correct side by side, while a consumer matching on the
    /// string would find nothing. Both are rendered "D" explicitly, which is
    /// also what <see cref="EngSitesBuilder"/> publishes as IDInInfoSource, so
    /// the id in the message and the id in the registry are the same characters.
    ///
    /// CreateCirid is false because the GUID already exists -- ENG assigned it
    /// when the twin was created. Asking CIR to mint one would produce a second
    /// identity for a thing that already has one.
    ///
    /// Failure is logged and swallowed, matching the channel step: an
    /// unreachable CIR should not prevent the twin from being announced, and the
    /// next publication retries the registration.
    /// </remarks>
    private async Task<int> RegisterInCirAsync(EngITwin twin, CancellationToken ct)
    {
        if (!_options.RegisterInCir || string.IsNullOrWhiteSpace(_options.CirBaseUrl))
            return 0;

        // Named apart even though they carry the same value, so the trace says
        // which role each is playing. When ENG's internal key stops being the
        // federation id -- or another participant reads this log to work out
        // what to match on -- the distinction is already recorded rather than
        // needing to be reconstructed.
        var federationId = twin.ITwinId.ToString("D");
        var internalId = twin.ITwinId.ToString("D");

        var request = new CreateRegistryRequest(
            [
                new CirRegistry(
                    Id: _options.Enterprise,
                    Description: [new CirLocalizedText("Sites")],
                    Categories:
                    [
                        new CirCategory(
                            Id: _options.CirITwinCategory,
                            SourceId: _options.SourceId,
                            Description: [new CirLocalizedText("ENG iTwins")],
                            Entries:
                            [
                                new CirEntry(
                                    IdInSource: internalId,
                                    SourceId: _options.SourceId,
                                    Cirid: twin.ITwinId,
                                    SourceOwnerId: _options.Enterprise,
                                    Name: twin.Handle,
                                    Description: new CirLocalizedText(
                                        twin.Description ?? twin.Handle),
                                    Properties: [])
                            ])
                    ])
            ],
            CreateCirid: false);

        try
        {
            var written = await cir.RegisterEntriesAsync(request, ct);

            logger.LogInformation(
                "CIR registration for iTwin '{Site}' wrote {Written} entry(ies) " +
                "(internal id {InternalId}, federation id {FederationId}).",
                twin.Handle, written, internalId, federationId);

            return written;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Registering iTwin '{Site}' in CIR failed.", twin.Handle);
            return 0;
        }
    }

    /// <summary>
    /// Announces that an iTwin has been deleted.
    /// </summary>
    /// <remarks>
    /// Takes an id rather than a twin, and deliberately does not check ENG for
    /// it. By the time this is called the row is already gone -- the provider
    /// delete is the system of record and runs first -- so a lookup here could
    /// only fail. That ordering is what makes the announcement truthful: ENG has
    /// really stopped holding the twin before anyone is told it has.
    ///
    /// The consequence is that this cannot validate the id, so the caller must.
    /// The sandbox endpoint does, by publishing only after a 204 from the
    /// provider.
    /// </remarks>
    public async Task<EngSitePublicationReport> PublishDeleteAsync(
        Guid iTwinId, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            logger.LogDebug("EngEngine is disabled.");
            return new EngSitePublicationReport();
        }

        if (string.IsNullOrWhiteSpace(_options.Enterprise))
            return Failed("EngEngine__Enterprise is not configured.");

        try
        {
            var sessionId = await isbm.OpenPublicationSessionAsync(
                _options.SitesChannelUri, ct);

            var correlationId = Guid.NewGuid().ToString();
            var content = builder.BuildDelete(iTwinId, correlationId);

            var messageId = await isbm.PostPublicationAsync(
                sessionId,
                content,
                _options.SitesTopics,
                ct: ct);

            logger.LogInformation(
                "Published deletion of iTwin {ITwinId:D} as {MessageId} [{CorrelationId}].",
                iTwinId, messageId, correlationId);

            return new EngSitePublicationReport { SitesSeen = 1, SitesPublished = 1 };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publishing deletion of iTwin {ITwinId:D} failed.", iTwinId);
            return new EngSitePublicationReport { SitesSeen = 1, Errors = [ex.Message] };
        }
    }

    private static EngSitePublicationReport Failed(string error) => new() { Errors = [error] };
}
