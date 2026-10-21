using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MmsEngine.Infrastructure.Mms;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using Oiie.Isbm.Client;

namespace MmsEngine.Application;

/// <summary>Consumes approved SC02 segments on per-site operations channels.</summary>
public sealed class SegmentIngestionService(
    IIsbmClient isbm,
    IMmsClient mms,
    IncomingSegmentMapper mapper,
    SegmentIdentityService identities,
    IOptions<MmsEngineOptions> options,
    ILogger<SegmentIngestionService> logger)
{
    private readonly MmsEngineOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, string> _sessions = [];
    private int _subscriptionCount;

    public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

    /// <summary>
    /// Opens durable subscribers before approvals are published. Does not consume,
    /// replay, or reset data, and can run while segment ingestion is disabled.
    /// </summary>
    public Task<SegmentIngestionReport> BootstrapAsync(CancellationToken ct = default) =>
        RunAsync(consume: false, ct);

    public Task<SegmentIngestionReport> DrainAsync(CancellationToken ct = default) =>
        RunAsync(consume: true, ct);

    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _sessions.Clear();
            Volatile.Write(ref _subscriptionCount, 0);
        }
        finally { _gate.Release(); }
    }

    private async Task<SegmentIngestionReport> RunAsync(bool consume, CancellationToken ct)
    {
        var report = new SegmentIngestionReport();
        if (consume && !_options.SegmentsIngestEnabled)
        {
            report.Notes.Add("Segment ingestion is disabled.");
            return report;
        }

        if (string.IsNullOrWhiteSpace(_options.Enterprise)
            || _options.SegmentsTopics.Length == 0
            || _options.SegmentsTopics.Any(string.IsNullOrWhiteSpace)
            || _options.MaxMessagesPerPoll <= 0
            || (consume && (string.IsNullOrWhiteSpace(_options.MmsBaseUrl)
                || string.IsNullOrWhiteSpace(_options.CirBaseUrl) || !_options.RegisterInCir)))
        {
            report.Failed++;
            report.Notes.Add("Segments require an enterprise, topics, a positive poll limit, MMS/CIR URLs and RegisterInCir=true.");
            logger.LogError("{Reason}", report.Notes[0]);
            return report;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var channels = await isbm.GetChannelsAsync(ct);
            var sites = channels.Where(c => c.ChannelType == IsbmChannelType.Publication)
                .Select(c => SiteFromChannel(c.ChannelUri))
                .OfType<Guid>()
                .Where(s => _options.ITwinFederationId == Guid.Empty || _options.ITwinFederationId == s)
                .Distinct().Order().ToArray();
            report.SitesDiscovered = sites.Length;

            // Subscribe every discovered site before spending the polling budget.
            foreach (var site in sites)
            {
                try
                {
                    var reopening = _sessions.ContainsKey(site);
                    if (!reopening || !consume)
                    {
                        // Bootstrap reopens through the stable subscriber ID rather
                        // than trusting a cached handle or an inconclusive probe.
                        await OpenSessionAsync(site, ct);
                        if (reopening)
                        {
                            report.SessionsReopened++;
                            report.Notes.Add($"Reopened site {site:D}; if its subscription was deleted, earlier publications require explicit replay.");
                        }
                    }
                    report.SubscriptionsReady++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Fail(report, ex, $"Could not subscribe to site {site:D}.");
                }
            }

            if (consume)
            {
                foreach (var site in sites)
                {
                    if (report.MessagesRead >= _options.MaxMessagesPerPoll) break;
                    if (!_sessions.TryGetValue(site, out var session)) continue;
                    try { await DrainSiteAsync(site, session, report, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        Fail(report, ex, $"Site {site:D} drain failed; its publication was not acknowledged.");
                    }
                }
            }
        }
        finally { _gate.Release(); }
        return report;
    }

    private Guid? SiteFromChannel(string channel)
    {
        var parts = channel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4 && Guid.TryParse(parts[1], out var site) && site != Guid.Empty
            && string.Equals(channel, _options.SegmentsChannelUriFor(site), StringComparison.OrdinalIgnoreCase)
            ? site : null;
    }

    private async Task<string> OpenSessionAsync(Guid site, CancellationToken ct)
    {
        var session = await isbm.OpenSubscriptionSessionAsync(_options.SegmentsChannelUriFor(site),
            _options.SegmentsTopics, ct, $"{_options.SourceId}:segments:{site:D}");
        _sessions[site] = session;
        Volatile.Write(ref _subscriptionCount, _sessions.Count);
        return session;
    }

    private async Task DrainSiteAsync(
        Guid site, string session, SegmentIngestionReport report, CancellationToken ct)
    {
        while (report.MessagesRead < _options.MaxMessagesPerPoll)
        {
            IsbmMessage? message;
            try { message = await isbm.ReadPublicationAsync(session, ct); }
            catch (IsbmException ex) when (ex.IsSessionProblem)
            {
                logger.LogWarning(ex, "Reopening the stale MMS segment session on site {Site}.", site);
                session = await OpenSessionAsync(site, ct);
                report.SessionsReopened++;
                report.Notes.Add($"Reopened site {site:D}; publications on a deleted subscription require explicit replay.");
                message = await isbm.ReadPublicationAsync(session, ct);
            }
            if (message is null) break;
            report.MessagesRead++;
            if (!await HandleMessageAsync(site, message, report, ct)) break;
            await isbm.RemovePublicationAsync(session, ct);
            report.MessagesAcknowledged++;
        }
    }

    private async Task<bool> HandleMessageAsync(
        Guid site, IsbmMessage message, SegmentIngestionReport report, CancellationToken ct)
    {
        if (message.Content is null)
            throw new InvalidOperationException($"Publication {message.MessageId} contains no parseable XML.");
        var envelope = BodEnvelope.Parse(message.Content.ToString());
        if (!envelope.Is("Sync", "Segments"))
        {
            report.Ignored++;
            logger.LogWarning("Ignoring {Verb}{Noun} publication {MessageId} on the MMS segment subscription.",
                envelope.Verb, envelope.Noun, message.MessageId);
            return true;
        }
        if (!string.Equals(envelope.SenderLogicalId, _options.ApprovedLogicalId, StringComparison.Ordinal)
            || envelope.ActionCode != "Replace")
            throw new InvalidOperationException("Only Replace SyncSegments from the configured approval sender are supported.");

        var segments = envelope.NounsAs(e => new Segment(e)).ToArray();
        if (segments.Length == 0)
            throw new InvalidOperationException("SyncSegments contained no segments.");
        if (segments.Select(s => s.UUID).Distinct().Count() != segments.Length)
            throw new InvalidOperationException("A publication contains duplicate segment identities.");

        foreach (var segment in segments)
        {
            report.SegmentsSeen++;
            var reason = mapper.Validate(segment, site);
            if (reason is not null) throw new InvalidOperationException(reason);
            var table = await identities.ResolveTableAsync(segment.Type!, ct);
            if (table is null)
                return Defer(report, segment, "No verified MMS RDL-CLASS table mapping.");
            if (table != IncomingSegmentMapper.LightUnitTable)
                return Defer(report, segment, $"Table '{table}' has no supported segment writer.");
            var owner = await identities.ResolveOwnerAsync(site, ct);
            if (owner is null)
                return Defer(report, segment, "No ITWIN-SITE/MMS owner mapping.");
            var anchor = await identities.ResolveAnchorAsync(segment, ct);
            if (anchor is null)
                return Defer(report, segment, "No FunctionalLocation/REG-LOCATION CIR anchor.");

            var request = mapper.Map(segment, table, owner.Value);
            var result = await mms.UpsertLightUnitsAsync([request], ct);
            if (result.Rejections.Count != 0)
                throw new MmsClientException($"MMS rejected segment {segment.UUID:D}: {result.Rejections[0].Reason}");
            if (result.Units.Count != 1 || result.Units[0].ExtAssetId != segment.UUID
                || result.Units[0].LightUnitId <= 0)
                throw new MmsClientException("MMS returned an invalid native identity for the segment.");
            report.UnitsUpserted++;
            await identities.RegisterAssetAsync(segment, anchor, result.Units[0].LightUnitId, ct);
            report.AssetsRegistered++;
        }
        return true;
    }

    private bool Defer(SegmentIngestionReport report, Segment segment, string reason)
    {
        report.Deferred++;
        var note = $"Segment {segment.UUID:D} ({segment.ShortName}) deferred: {reason}";
        report.Notes.Add(note);
        logger.LogWarning("{Reason}", note);
        return false;
    }

    private void Fail(SegmentIngestionReport report, Exception ex, string reason)
    {
        report.Failed++;
        report.Notes.Add($"{reason} {ex.Message}");
        logger.LogError(ex, "{Reason}", reason);
    }
}

/// <summary>Counts and diagnostics from subscription bootstrap or a segment poll.</summary>
public sealed class SegmentIngestionReport
{
    public int SitesDiscovered { get; set; }
    public int SubscriptionsReady { get; set; }
    public int MessagesRead { get; set; }
    public int MessagesAcknowledged { get; set; }
    public int SegmentsSeen { get; set; }
    public int UnitsUpserted { get; set; }
    public int AssetsRegistered { get; set; }
    public int Deferred { get; set; }
    public int Failed { get; set; }
    public int Ignored { get; set; }
    public int SessionsReopened { get; set; }
    public List<string> Notes { get; } = [];
}
