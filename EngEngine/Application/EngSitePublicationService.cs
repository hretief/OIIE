using EngEngine.Infrastructure.Eng;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Isbm.Client;

namespace EngEngine.Application;

public sealed record EngSitePublicationReport
{
    public int SitesSeen { get; init; }
    public int SitesPublished { get; init; }
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
/// this one cannot, because the twin's channel does not exist until this message
/// has been processed.
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

        string? sessionId = null;

        foreach (var twin in twins)
        {
            if (!EngSitesBuilder.IsPublishable(twin))
            {
                skipped.Add($"{twin.Code}: has no site type.");
                continue;
            }

            try
            {
                // Resolved before the session is opened, because this is the
                // step that can legitimately fail on a twin and it should not
                // open a session against the broker to then send nothing.
                var typeUuid = await eng.ResolveSiteTypeUuidAsync(
                    EngSitesBuilder.TypeNameOf(twin), ct);

                // Opened lazily, and not closed afterwards: the client caches
                // publication sessions per channel and reuses them across runs,
                // as the marker drain does. Closing here would discard a session
                // the next publication would have to re-establish.
                sessionId ??= await isbm.OpenPublicationSessionAsync(
                    _options.SitesChannelUri, ct);

                var correlationId = Guid.NewGuid().ToString();
                var content = builder.Build(twin, typeUuid, correlationId);

                var messageId = await isbm.PostPublicationAsync(
                    sessionId,
                    content,
                    _options.SitesTopics,
                    ct: ct);

                publishedCount++;

                logger.LogInformation(
                    "Published site '{Site}' ({ITwinId:D}) as {MessageId} [{CorrelationId}].",
                    twin.Code, twin.ITwinId, messageId, correlationId);
            }
            catch (Exception ex)
            {
                // Recorded and carried on, unlike the marker drain. There is no
                // watermark to corrupt here, and one unpublishable twin should
                // not hold back the rest of a bootstrap.
                logger.LogError(ex, "Publishing site '{Site}' failed.", twin.Code);
                errors.Add($"{twin.Code}: {ex.Message}");
            }
        }

        return new EngSitePublicationReport
        {
            SitesSeen = twins.Count,
            SitesPublished = publishedCount,
            Errors = errors,
            Skipped = skipped
        };
    }

    private static EngSitePublicationReport Failed(string error) => new() { Errors = [error] };
}
