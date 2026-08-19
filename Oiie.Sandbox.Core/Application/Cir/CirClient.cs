using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Cir;
using Oiie.Isbm.Client;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Infrastructure.Isbm;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Cir;

public sealed record RegistrationResult(
    int Registered, IReadOnlyList<CirFault> Faults, string CorrelationId)
{
    public bool Succeeded => Faults.Count == 0;
}

public sealed record ResolutionResult(
    Guid? Cirid,
    IReadOnlyList<Entry> Equivalents,
    bool FromCache,
    string? Detail);

/// <summary>
/// Registers entries with the ws-CIR provider and resolves foreign identifiers
/// against it.
///
/// Every call travels over ISBM as an Annex A BOD, not as a REST call to the
/// registry. The CIR provider is itself an ISBM participant, so a participant needs
/// exactly one integration mechanism rather than two — which is the property the
/// whole ecosystem argument rests on.
/// </summary>
public sealed class CirClient(
    IsbmSessionManager sessions,
    IIsbmClientAccessor clients,
    IParticipantDbContextFactory factory,
    CirTelemetry telemetry,
    ILogger<CirClient> logger)
{
    /// <summary>
    /// How long to wait before re-reading, given the number of empty reads so far.
    ///
    /// A fixed one-second interval made every exchange cost a whole second even when
    /// the provider answered in tens of milliseconds: the first read happens before
    /// the provider can possibly have replied, so the answer was always collected on
    /// the second read, one full second later. Since a context resolution is two
    /// exchanges, that put a two-second floor under an operation whose real cost is
    /// the SQL query behind it.
    ///
    /// The backoff starts short enough that a local provider is caught on the first
    /// or second retry, and grows to the original interval so a provider that is
    /// genuinely absent is not polled hard for the whole timeout. Capping at the old
    /// value keeps the load during a long wait exactly what it was before.
    /// </summary>
    private static TimeSpan NextPollDelay(int emptyReads) => emptyReads switch
    {
        0 => TimeSpan.FromMilliseconds(25),
        1 => TimeSpan.FromMilliseconds(50),
        2 => TimeSpan.FromMilliseconds(100),
        3 => TimeSpan.FromMilliseconds(250),
        4 => TimeSpan.FromMilliseconds(500),
        _ => TimeSpan.FromSeconds(1)
    };

    /// <summary>
    /// How often a still-waiting exchange is logged. Time-based rather than
    /// poll-count-based: with a variable delay the two are no longer the same thing.
    /// </summary>
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(15);

    private static string Truncate(string? value, int max = 1500) =>
        string.IsNullOrWhiteSpace(value) ? "(empty)"
        : value.Length <= max ? value
        : value[..max] + "…";

    public async Task<RegistrationResult> RegisterAsync(
        ParticipantContext participant,
        string categoryId,
        IReadOnlyList<Entry> entries,
        CancellationToken ct = default)
    {
        var config = participant.Config;
        var correlationId = Guid.NewGuid().ToString();

        var registry = new Registry
        {
            ID = config.Cir.RegistryId,
            Category =
            [
                new Category
                {
                    ID = categoryId,
                    // The authority that defined the category, so two organisations
                    // can both have a "Segment" category without collision.
                    CategorySourceID = "OIIE-SANDBOX",
                    Entry = entries.ToList()
                }
            ]
        };

        var document = CirBods.ProcessRegistry(
            registry, config.LogicalId, correlationId, createCirid: true);

        var response = await ExchangeAsync(participant, document, correlationId, ct);

        if (response is null)
        {
            return new RegistrationResult(0, [new CirFault("NoResponse",
                $"No response within {(int)participant.Config.Cir.ResponseTimeout.TotalSeconds}s. " +
                "Run GET /admin/cir/diagnose to see whether the request is still queued.")],
                correlationId);
        }

        if (response.HasFaults)
        {
            // Acknowledgement is not success. Faults arrive inside a well-formed
            // acknowledgement, so a caller treating any response as confirmation
            // would discard exactly what the round trip exists to obtain.
            foreach (var fault in response.Faults)
            {
                logger.LogWarning(
                    "CIR {Kind} registering for {ParticipantId}: {Detail}",
                    fault.Kind, participant.ParticipantId, fault.Detail);
            }

            return new RegistrationResult(0, response.Faults, correlationId);
        }

        // A response that is neither an acknowledgement nor a recognised fault is
        // still a failure, and reporting it as success would be the worst outcome:
        // the caller proceeds as though entries were registered.
        if (!response.Verb.StartsWith("Acknowledge", StringComparison.Ordinal))
        {
            return new RegistrationResult(0,
                [new CirFault("UnexpectedResponse",
                    $"Expected an Acknowledge verb, got '{response.Verb}'. " +
                    $"Response was: {Truncate(response.RawXml)}")],
                correlationId);
        }

        return new RegistrationResult(entries.Count, response.Faults, correlationId);
    }

    /// <summary>
    /// Asserts that new entries denote the same things as entries already in the
    /// registry.
    ///
    /// Distinct from registration, and the distinction is the whole point: three
    /// participants registering independently produce three identities for one pump,
    /// which is the duplication the registry is meant to prevent. Equivalence
    /// produces one identity with three names.
    /// </summary>
    public async Task<RegistrationResult> AssertEquivalenceAsync(
        ParticipantContext participant,
        IReadOnlyList<EquivalentEntry> equivalences,
        CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString();

        var document = CirBods.ProcessEquivalentEntries(
            equivalences, participant.Config.LogicalId, correlationId);

        var response = await ExchangeAsync(participant, document, correlationId, ct);

        if (response is null)
        {
            return new RegistrationResult(0, [new CirFault("NoResponse",
                "The registry did not respond within the timeout.")], correlationId);
        }

        foreach (var fault in response.Faults)
        {
            logger.LogWarning(
                "CIR {Kind} asserting equivalence for {ParticipantId}: {Detail}",
                fault.Kind, participant.ParticipantId, fault.Detail);
        }

        if (response.HasFaults)
        {
            return new RegistrationResult(0, response.Faults, correlationId);
        }

        if (!response.Verb.StartsWith("Acknowledge", StringComparison.Ordinal))
        {
            return new RegistrationResult(0,
                [new CirFault("UnexpectedResponse",
                    $"Expected an Acknowledge verb, got '{response.Verb}'. " +
                    $"Response was: {Truncate(response.RawXml)}")],
                correlationId);
        }

        return new RegistrationResult(equivalences.Count, response.Faults, correlationId);
    }

    /// <summary>
    /// Resolves a foreign identifier to its shared identity, consulting the local
    /// cache first.
    ///
    /// The cache is a feature rather than an optimisation: it is what makes stale
    /// mappings possible, and correcting one after a merge is a behaviour worth
    /// being able to demonstrate.
    /// </summary>
    public async Task<ResolutionResult> ResolveAsync(
        ParticipantContext participant,
        string foreignSourceId,
        string foreignIdInSource,
        CancellationToken ct = default)
    {
        await using var db = factory.Create(participant.ParticipantId);

        var cached = await db.IdentityMap.FirstOrDefaultAsync(
            m => m.ForeignSourceId == foreignSourceId && m.ForeignIdInSource == foreignIdInSource, ct);

        if (cached is not null && cached.IsLive(DateTimeOffset.UtcNow))
        {
            // The CIRID is cached; the equivalence set deliberately is not. What
            // else shares an identity changes whenever any participant registers or
            // relinks, and MMS reads the OWNER_ID out of that set rather than from a
            // local column — so serving an empty list here would report every twin
            // as unrelated for the lifetime of the cache entry, which is exactly the
            // symptom a successful relink produced.
            var equivalents = cached.Cirid is { } cachedCirid
                ? await FindEquivalentsAsync(participant, cachedCirid, ct)
                : [];

            return new ResolutionResult(cached.Cirid, equivalents, FromCache: true, null);
        }

        var config = participant.Config;
        var correlationId = Guid.NewGuid().ToString();

        var filter = new Filter
        {
            RegistryFilter = new RegistryFilter { ID = config.Cir.RegistryId },
            EntryFilter = new EntryFilter
            {
                SourceID = foreignSourceId,
                IDInSource = foreignIdInSource
            }
        };

        var document = CirBods.GetRegistry([filter], config.LogicalId, correlationId);
        var response = await ExchangeAsync(participant, document, correlationId, ct);

        if (response is null)
        {
            return new ResolutionResult(null, [], false, "The registry did not respond.");
        }

        var match = response.AllEntries.FirstOrDefault(e =>
            string.Equals(e.SourceID, foreignSourceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.IDInSource, foreignIdInSource, StringComparison.OrdinalIgnoreCase));

        if (match?.CIRID is not { } cirid)
        {
            return new ResolutionResult(null, [], false, "No entry with a shared identity.");
        }

        // Everything else the registry knows under that identity. This is the answer
        // to "what else is this thing called", and the reason resolution is worth
        // more than a lookup table.
        var equivalentEntries = await FindEquivalentsAsync(participant, cirid, ct);

        var entry = cached ?? new IdentityMapEntry
        {
            ForeignSourceId = foreignSourceId,
            ForeignIdInSource = foreignIdInSource
        };

        entry.Cirid = cirid;
        entry.ForeignName = match.Name;
        entry.ResolvedAt = DateTimeOffset.UtcNow;
        entry.StaleAfter = DateTimeOffset.UtcNow.Add(config.Cir.IdentityCacheTtl);
        entry.Invalidated = false;
        entry.InvalidatedReason = null;

        if (cached is null) db.IdentityMap.Add(entry);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{ParticipantId} resolved {SourceId}:{IdInSource} to {Cirid} with {Count} equivalent(s)",
            participant.ParticipantId, foreignSourceId, foreignIdInSource, cirid, equivalentEntries.Count);

        return new ResolutionResult(cirid, equivalentEntries, false, null);
    }

    private async Task<IReadOnlyList<Entry>> FindEquivalentsAsync(
        ParticipantContext participant, Guid cirid, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString();

        var filter = new Filter
        {
            RegistryFilter = new RegistryFilter { ID = participant.Config.Cir.RegistryId },
            EntryFilter = new EntryFilter { CIRID = cirid }
        };

        var document = CirBods.GetRegistry(
            [filter], participant.Config.LogicalId, correlationId);

        var response = await ExchangeAsync(participant, document, correlationId, ct);
        return response?.AllEntries.ToList() ?? [];
    }

    /// <summary>
    /// Collapses several CIRIDs onto one, via ws-CIR ChangeEntryCIRID.
    ///
    /// This is the correct verb for relating two entries that both already exist.
    /// ProcessEquivalentEntries inserts, so using it here answers DuplicateEntryFault
    /// rather than linking the pair.
    ///
    /// The local identity cache is dropped for the affected CIRIDs, because a
    /// cached row still pointing at a collapsed identity would keep answering with
    /// the identity that was just superseded.
    /// </summary>
    public async Task<RegistrationResult> RelinkCiridAsync(
        ParticipantContext participant,
        IReadOnlyList<Guid> stale,
        Guid newCirid,
        CancellationToken ct = default)
    {
        if (stale.Count == 0)
        {
            return new RegistrationResult(0, [], string.Empty);
        }

        var correlationId = Guid.NewGuid().ToString();

        var document = CirBods.ChangeEntryCirid(
            stale, newCirid, participant.Config.LogicalId, correlationId);

        await SendAsync(participant, document, correlationId, ct);

        await using var db = factory.Create(participant.ParticipantId);

        var cached = await db.IdentityMap
            .Where(m => m.Cirid != null && stale.Contains(m.Cirid.Value))
            .ToListAsync(ct);

        foreach (var entry in cached)
        {
            entry.Cirid = newCirid;
            entry.ResolvedAt = DateTimeOffset.UtcNow;
        }

        if (cached.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "{ParticipantId} relinked {Count} CIRID(s) onto {NewCirid}.",
            participant.ParticipantId, stale.Count, newCirid);

        return new RegistrationResult(stale.Count, [], correlationId);
    }

    /// <summary>
    /// Every entry in the registry, bypassing the local identity cache.
    ///
    /// A diagnostic rather than part of any flow: the cache answers what this
    /// participant has already looked up, which is precisely not the question when
    /// the question is what the registry itself still holds after a reset.
    /// </summary>
    public async Task<IReadOnlyList<Entry>> DumpRegistryAsync(
        ParticipantContext participant,
        CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString();

        // Registry filter only. An EntryFilter would scope this to one identifier,
        // and the point is to see everything.
        var filter = new Filter
        {
            RegistryFilter = new RegistryFilter { ID = participant.Config.Cir.RegistryId }
        };

        var document = CirBods.GetRegistry(
            [filter], participant.Config.LogicalId, correlationId);

        var response = await ExchangeAsync(participant, document, correlationId, ct);

        return response?.AllEntries.ToList() ?? [];
    }

    /// <summary>
    /// Deletes the whole registry, via ws-CIR CancelRegistry.
    ///
    /// This is a day-zero operation, not part of any scenario. Dropping the
    /// participant tables leaves the registry untouched, so entries registered on a
    /// previous run keep their CIRIDs and the next registration attaches to an
    /// identity that predates the reset. That makes a "first" run indistinguishable
    /// from a repeat, which is the confusion this removes.
    ///
    /// The local identity cache is emptied in the same breath. A cached row
    /// pointing at a CIRID that no longer exists anywhere would keep answering
    /// resolutions with an identity the registry has forgotten, and the TTL means
    /// it would do so for minutes rather than visibly failing.
    /// </summary>
    public async Task<RegistrationResult> DeleteRegistryAsync(
        ParticipantContext participant,
        CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString();
        var registryId = participant.Config.Cir.RegistryId;

        var document = CirBods.CancelRegistry(
            registryId, participant.Config.LogicalId, correlationId);

        await SendAsync(participant, document, correlationId, ct);

        await using var db = factory.Create(participant.ParticipantId);

        var cached = await db.IdentityMap.ToListAsync(ct);

        if (cached.Count > 0)
        {
            db.IdentityMap.RemoveRange(cached);
            await db.SaveChangesAsync(ct);
        }

        logger.LogWarning(
            "{ParticipantId} deleted registry {RegistryId} and dropped {Count} cached identity mapping(s).",
            participant.ParticipantId, registryId, cached.Count);

        return new RegistrationResult(cached.Count, [], correlationId);
    }

    /// <summary>
    /// Posts a request and returns without waiting for a response.
    ///
    /// For the BODs that declare no response (§3.1.4 ChangeEntryCIRID and the
    /// Cancel verbs). Reusing <see cref="ExchangeAsync"/> for these would block
    /// for the full timeout and then report a failure, because the absence of a
    /// reply is correct behaviour here rather than a symptom.
    ///
    /// The exchange is still recorded, so a change that appears not to have taken
    /// effect can be traced to the exact request that was sent.
    /// </summary>
    private async Task SendAsync(
        ParticipantContext participant,
        System.Xml.Linq.XDocument request,
        string correlationId,
        CancellationToken ct)
    {
        var channelUri = participant.Config.Cir.ChannelUri;

        if (string.IsNullOrWhiteSpace(channelUri))
        {
            logger.LogError(
                "{ParticipantId} has no CIR channel configured.", participant.ParticipantId);
            return;
        }

        var client = clients.For(participant.ParticipantId);

        var sessionId = await sessions.GetOrOpenAsync(
            participant.ParticipantId, IsbmSessionKind.ConsumerRequest, channelUri, [], ct);

        var topic = participant.Config.Cir.RequestTopic;

        var exchange = new CirExchange
        {
            ParticipantId = participant.ParticipantId,
            Bod = request.Root!.Name.LocalName,
            CorrelationId = correlationId,
            ChannelUri = channelUri,
            Topic = topic,
            RequestXml = request.ToString(System.Xml.Linq.SaveOptions.None)
        };

        await telemetry.SaveAsync(exchange, ct);

        var requestMessageId = await client.PostRequestAsync(
            sessionId, request.Root!, [topic], null, ct);

        exchange.RequestMessageId = requestMessageId;
        exchange.ConsumerSessionId = sessionId;

        // Recorded as sent rather than answered: no reply is expected, so waiting
        // for one would misreport correct behaviour as a timeout.
        exchange.Outcome = "Sent";

        await telemetry.SaveAsync(exchange, ct);

        logger.LogDebug(
            "{ParticipantId} sent {Bod} on topic {Topic} as {RequestMessageId} [{CorrelationId}]",
            participant.ParticipantId, request.Root!.Name.LocalName, topic,
            requestMessageId, correlationId);
    }

    /// <summary>
    /// Posts a request on the CIR channel and waits for the response.
    ///
    /// This is the first use of the ISBM consumer-request path, which ws-CIR itself
    /// never exercised — it is a request provider, not a consumer. Failures here are
    /// as likely to be route shapes as logic.
    /// </summary>
    private async Task<CirResponse?> ExchangeAsync(
        ParticipantContext participant,
        System.Xml.Linq.XDocument request,
        string correlationId,
        CancellationToken ct)
    {
        var channelUri = participant.Config.Cir.ChannelUri;

        if (string.IsNullOrWhiteSpace(channelUri))
        {
            logger.LogError(
                "{ParticipantId} has no CIR channel configured.", participant.ParticipantId);
            return null;
        }

        var client = clients.For(participant.ParticipantId);

        var sessionId = await sessions.GetOrOpenAsync(
            participant.ParticipantId, IsbmSessionKind.ConsumerRequest, channelUri, [], ct);

        // Exactly one topic, and it must match what the provider's listener
        // subscribes to. Configured rather than derived from the BOD name: a topic
        // per BOD would require the subscriber to enumerate every request BOD, and a
        // missing one fails silently — the request is accepted and never delivered.
        var topic = participant.Config.Cir.RequestTopic;

        // Recorded before the post, so a request that is consumed and discarded can
        // still be produced verbatim afterwards.
        var exchange = new CirExchange
        {
            ParticipantId = participant.ParticipantId,
            Bod = request.Root!.Name.LocalName,
            CorrelationId = correlationId,
            ChannelUri = channelUri,
            Topic = topic,
            RequestXml = request.ToString(System.Xml.Linq.SaveOptions.None)
        };

        await telemetry.SaveAsync(exchange, ct);

        var requestMessageId = await client.PostRequestAsync(
            sessionId, request.Root!, [topic], null, ct);

        exchange.RequestMessageId = requestMessageId;
        exchange.ConsumerSessionId = sessionId;

        // Persisted again now the ids are known. Until this point the row says what
        // was sent but not where, and "where" is half of what the provider's owner
        // needs to find it.
        await telemetry.SaveAsync(exchange, ct);

        logger.LogDebug(
            "{ParticipantId} posted {Bod} on topic {Topic} as {RequestMessageId} [{CorrelationId}]",
            participant.ParticipantId, request.Root!.Name.LocalName, topic,
            requestMessageId, correlationId);

        var timeout = participant.Config.Cir.ResponseTimeout;
        var startedAt = DateTimeOffset.UtcNow;
        var deadline = startedAt.Add(timeout);

        // Counted separately: the first drives the backoff, the second is what the
        // telemetry and the log messages report. A poll is no longer a second, so
        // one counter can no longer stand for both.
        var emptyReads = 0;
        var nextWaitLog = startedAt.Add(WaitLogInterval);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var message = await client.ReadResponseAsync(sessionId, requestMessageId, ct);

            if (message?.Content is not null)
            {
                var response = CirResponse.Parse(new System.Xml.Linq.XDocument(message.Content));

                // The provider has been observed answering one request with another's
                // reply: a GetRegistry filtered on one CIRID came back carrying a
                // different CIRID's entries, echoing a BODID we never sent. Accepting
                // that silently is the worst outcome available, because the answer is
                // well-formed and plausible -- an equivalence lookup simply fails to
                // find the sibling it was asking about, and the caller concludes the
                // two identities are unrelated when the registry says they are.
                //
                // So a reply is only ours if it echoes our BODID. Anything else is
                // dropped and the poll continues until the real answer arrives or the
                // deadline passes. A response that echoes nothing at all is accepted:
                // OriginalApplicationArea is optional, and refusing those would break
                // every provider that omits it.
                if (response.OriginalBodId is { Length: > 0 } echoed
                    && !string.Equals(echoed, correlationId, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "{ParticipantId} discarded a CIR response echoing {Echoed} while waiting " +
                        "for {CorrelationId}; it answers a different request.",
                        participant.ParticipantId, echoed, correlationId);

                    // Removed, or the same foreign reply is re-read on every poll and
                    // the wait can only end in a timeout.
                    await client.RemoveResponseAsync(sessionId, requestMessageId, ct);

                    await Task.Delay(NextPollDelay(emptyReads), ct);
                    emptyReads++;
                    continue;
                }

                logger.LogInformation(
                    "{ParticipantId} received {Verb} with {FaultCount} fault(s) [{CorrelationId}]: {Raw}",
                    participant.ParticipantId, response.Verb, response.Faults.Count,
                    correlationId, Truncate(response.RawXml, 4000));

                exchange.ResponseXml = response.RawXml;
                exchange.ResponseVerb = response.Verb;
                exchange.Faults = response.Faults.Select(f => $"{f.Kind}: {f.Detail}").ToList();
                exchange.Outcome = response.HasFaults ? "Faulted" : "Answered";
                exchange.AnsweredUtc = DateTimeOffset.UtcNow;

                await telemetry.SaveAsync(exchange, ct);

                await client.RemoveResponseAsync(sessionId, requestMessageId, ct);
                return response;
            }

            if (message is not null)
            {
                logger.LogError(
                    "CIR response {MessageId} could not be parsed as XML.", message.MessageId);
                await client.RemoveResponseAsync(sessionId, requestMessageId, ct);
                return null;
            }

            await Task.Delay(NextPollDelay(emptyReads), ct);
            emptyReads++;

            // Logged periodically so a long wait is visibly a wait rather than a
            // hang, and so a cold provider is distinguishable from a silent one.
            if (DateTimeOffset.UtcNow >= nextWaitLog)
            {
                logger.LogInformation(
                    "{ParticipantId} still waiting for a CIR response after {Seconds}s [{CorrelationId}]",
                    participant.ParticipantId,
                    (int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds, correlationId);

                nextWaitLog = DateTimeOffset.UtcNow.Add(WaitLogInterval);
            }
        }

        // A timeout most often means the topic is not one the provider subscribes
        // to, rather than that the provider is slow: an undelivered request looks
        // exactly like an unanswered one.
        exchange.Outcome = "NoResponse";
        exchange.WaitedSeconds = (int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds;

        // The case this record was added for: nothing came back, and the request XML
        // is now the only thing that can be handed to whoever owns the provider.
        // CancellationToken.None deliberately — a cancelled wait is exactly when the
        // evidence must still be written.
        await telemetry.SaveAsync(exchange, CancellationToken.None);

        logger.LogWarning(
            "{ParticipantId} timed out after {Seconds}s waiting for a CIR response on topic " +
            "{Topic} [{CorrelationId}]. Either the provider is not consuming this channel and " +
            "topic, or it never woke.",
            participant.ParticipantId, (int)timeout.TotalSeconds, topic, correlationId);

        return null;
    }
}
