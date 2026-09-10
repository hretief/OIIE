using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Extensions;
using Oiie.Isbm.Client;

namespace EngEngine.Application;

/// <summary>
/// The outcome of checking the outbound map against the RDL library.
///
/// "Could not verify" is a distinct state from "verified and wrong", and
/// collapsing them would let a provider being down read as a clean mapping.
/// <see cref="Checked"/> is what separates them.
/// </summary>
public sealed record RdlValidationResult
{
    /// <summary>False when the library could not be reached or parsed.</summary>
    public bool Checked { get; init; }

    /// <summary>Configured RDL keys the library does not hold. Empty when not <see cref="Checked"/>.</summary>
    public IReadOnlyList<string> MissingKeys { get; init; } = [];

    /// <summary>How many classes the library returned, for the "none of them matched" case.</summary>
    public int LibrarySize { get; init; }

    /// <summary>Why verification did not happen, when it did not.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Asks RDL what classes it holds, and checks ENG's outbound map against the
/// answer.
///
/// This exists because <see cref="EngEngineOptions.OutboundRdlClassMap"/> is a
/// claim about someone else's library that nothing was checking. A key that is
/// merely a typo -- "rdl:LightingUnits" -- publishes a segment type no
/// subscriber can resolve, and every consumer records it as fact. The map
/// cannot be derived automatically, because deciding that a Streetlight is a
/// LightingUnit is a modelling judgement; but it can be told when it names
/// something that does not exist, which is the failure that actually happens.
///
/// Over ISBM rather than RDL's HTTP API, deliberately. ENG has no business
/// knowing RDL's REST endpoint: in the field the reference library is a system
/// ENG reaches only through the bus, and validating over a private back channel
/// would prove the mapping while proving nothing about the integration.
///
/// Advisory throughout. Every failure returns "not checked" rather than
/// throwing, because the drain this runs inside has markers to publish and the
/// mapping is no more wrong than it was on the previous pass.
/// </summary>
public sealed class RdlTaxonomyValidator(
    IIsbmClient isbm,
    IOptions<EngRdlOptions> rdlOptions,
    IOptions<EngEngineOptions> engineOptions,
    ILogger<RdlTaxonomyValidator> logger)
{
    private readonly EngRdlOptions _options = rdlOptions.Value;
    private readonly EngEngineOptions _engine = engineOptions.Value;

    // One fetch at a time. Timer ticks can overlap when a drain runs long, and
    // two passes racing here would open two sessions and ask the same question
    // twice to fill the same cache slot.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HashSet<string>? _codes;
    private DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;
    private string? _failureReason;

    /// <summary>
    /// Checks the configured map, fetching the library if the cache is cold.
    /// </summary>
    public async Task<RdlValidationResult> ValidateAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return new RdlValidationResult { Reason = "RDL validation is disabled." };
        }

        var configured = _engine.OutboundRdlClassMap.Values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (configured.Count == 0)
        {
            // Nothing to check is a clean result, not an unverified one: the
            // map makes no claims, so none of them can be wrong.
            return new RdlValidationResult { Checked = true };
        }

        var codes = await GetLibraryAsync(ct);

        if (codes is null)
        {
            return new RdlValidationResult { Reason = _failureReason };
        }

        var missing = configured
            .Where(key => !codes.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        return new RdlValidationResult
        {
            Checked = true,
            MissingKeys = missing,
            LibrarySize = codes.Count
        };
    }

    /// <summary>
    /// The library's class codes, from cache when warm. Null means the fetch
    /// failed, with <see cref="_failureReason"/> saying how.
    /// </summary>
    private async Task<HashSet<string>?> GetLibraryAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            // Re-checked inside the gate: a caller that queued behind a fetch
            // should use its result rather than immediately repeat it.
            if (DateTimeOffset.UtcNow < _cachedUntil)
            {
                return _codes;
            }

            try
            {
                var classes = await FetchAsync(ct);

                _codes = new HashSet<string>(
                    classes.Select(c => c.Code), StringComparer.OrdinalIgnoreCase);
                _failureReason = null;
                _cachedUntil = DateTimeOffset.UtcNow.Add(_options.CacheDuration);

                logger.LogInformation(
                    "RDL returned {Count} classes; the outbound map will be checked against them.",
                    _codes.Count);

                return _codes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cached as a failure so a provider that is down does not cost
                // every subsequent drain the full response timeout.
                _codes = null;
                _failureReason = ex.Message;
                _cachedUntil = DateTimeOffset.UtcNow.Add(_options.FailureCacheDuration);

                logger.LogWarning(
                    ex, "Could not read the RDL library; the outbound map is unverified this pass.");

                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One GetTaxonomySet round trip on the RDL request channel.
    ///
    /// An empty selector list, which the BOD defines as "everything you hold" —
    /// exactly what a participant checking a mapping table wants on each pass.
    /// </summary>
    private async Task<IReadOnlyList<RdlClass>> FetchAsync(CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("D");

        var request = TaxonomySetBods.GetTaxonomySet(
            selectors: [],
            senderLogicalId: _engine.ParticipantId,
            correlationId: correlationId);

        var sessionId = await isbm.OpenConsumerRequestSessionAsync(_options.RequestChannelUri, ct);

        try
        {
            var messageId = await isbm.PostRequestAsync(
                sessionId, request.Root!, _options.EffectiveTopics, expiry: null, ct: ct);

            var response = await AwaitResponseAsync(sessionId, messageId, ct);

            if (response?.Content is null)
            {
                throw new InvalidOperationException(
                    $"RDL did not answer within {_options.ResponseTimeout.TotalSeconds:0}s.");
            }

            var classes = TaxonomySetBods.ParseShowTaxonomySet(new XDocument(response.Content));

            // The response is removed only once it has been read successfully.
            // An unremoved response is re-read next pass, which is the right
            // outcome for a message this engine failed to make sense of.
            await isbm.RemoveResponseAsync(sessionId, messageId, CancellationToken.None);

            if (classes.Count == 0)
            {
                throw new InvalidOperationException(
                    "RDL answered, but the response carried no classes.");
            }

            return classes;
        }
        finally
        {
            // Closed even on timeout: a session left open on the broker
            // accumulates one per failed pass for as long as RDL is down.
            try
            {
                await isbm.CloseSessionAsync(
                    IsbmSessionKind.ConsumerRequest, sessionId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Closing the RDL request session failed; continuing.");
            }
        }
    }

    /// <summary>
    /// Polls for the response until it arrives or the timeout expires.
    ///
    /// Polling because request-response over ISBM has no callback: the
    /// consumer asks whether the answer is there yet, and null means "not
    /// yet" rather than "no".
    /// </summary>
    private async Task<IsbmMessage?> AwaitResponseAsync(
        string sessionId, string messageId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(_options.ResponseTimeout);

        while (true)
        {
            var message = await isbm.ReadResponseAsync(sessionId, messageId, ct);

            if (message is not null) return message;

            if (DateTimeOffset.UtcNow >= deadline) return null;

            await Task.Delay(_options.PollInterval, ct);
        }
    }
}
