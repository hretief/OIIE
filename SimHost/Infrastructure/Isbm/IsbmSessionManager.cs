using Oiie.Isbm.Client;

namespace SimHost.Infrastructure.Isbm;

/// <summary>
/// Obtains usable ISBM sessions, reusing stored ones and recovering from stale ids.
///
/// Two hazards this exists for.
///
/// First, a session is not immediately usable after open: the ISBM provider stores
/// session state in a Durable Entity, and reads are eventually consistent, so a
/// post or read issued straight after the open can fail against a session the
/// provider has not yet made visible. Confirming before use costs one extra call
/// and removes an intermittent failure that otherwise looks like a wire-shape bug.
///
/// Second, a stored session id outlives the provider that issued it. After a
/// provider redeploy the id is meaningless, and every subsequent call fails
/// identically. IsbmException.IsSessionProblem identifies that case, and the fix is
/// to discard and re-open rather than retry.
///
/// NOTE: the ws-CIR provider has its own SessionHelper.OpenAndConfirmAsync, which
/// is the version proven against a live provider. This should be replaced by it
/// once that code is available — the confirmation strategy below is derived from
/// the symptom, not from that implementation.
/// </summary>
public sealed class IsbmSessionManager(
    IIsbmClientAccessor clients,
    IIsbmSessionStoreAccessor stores,
    ILogger<IsbmSessionManager> logger)
{
    // Kept short deliberately. A session probe cannot distinguish "not visible yet"
    // from "no such route", so a long confirmation loop against a provider that does
    // not implement GET sessions/{id} simply wastes time — and waiting longer made
    // failures more frequent, not less, which suggests sessions are short-lived
    // rather than slow to appear. Recovery on first use carries the real weight.
    private const int ConfirmAttempts = 3;
    private static readonly TimeSpan ConfirmDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Returns a session that has been confirmed readable, opening one if no usable
    /// stored session exists.
    /// </summary>
    public async Task<string> GetOrOpenAsync(
        string participantId,
        IsbmSessionKind kind,
        string channelUri,
        IReadOnlyList<string> topics,
        CancellationToken ct = default)
    {
        var store = stores.For(participantId);

        var existing = await store.GetAsync(kind, channelUri, ct);
        if (existing is not null)
        {
            return existing;
        }

        var client = clients.For(participantId);

        var sessionId = kind switch
        {
            IsbmSessionKind.Publication => await client.OpenPublicationSessionAsync(channelUri, ct),
            IsbmSessionKind.Subscription => await client.OpenSubscriptionSessionAsync(channelUri, topics, ct),
            IsbmSessionKind.ConsumerRequest => await client.OpenConsumerRequestSessionAsync(channelUri, ct),
            IsbmSessionKind.ProviderRequest => await client.OpenProviderRequestSessionAsync(channelUri, topics, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        await ConfirmAsync(participantId, kind, sessionId, ct);
        await store.SaveAsync(kind, channelUri, sessionId, ct);

        return sessionId;
    }

    /// <summary>
    /// Runs an operation against a session, re-opening when the provider reports the
    /// session is unusable.
    ///
    /// This is where session reliability actually comes from, not from confirming
    /// after open. A session id can be stale for several unrelated reasons — the
    /// channel was recreated, the provider was redeployed, the session expired — and
    /// none of them are detectable in advance. Failing once and re-opening is both
    /// simpler and more reliable than trying to predict them.
    /// </summary>
    public async Task<T> WithSessionAsync<T>(
        string participantId,
        IsbmSessionKind kind,
        string channelUri,
        IReadOnlyList<string> topics,
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        const int attempts = 3;
        IsbmException? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var sessionId = await GetOrOpenAsync(participantId, kind, channelUri, topics, ct);

            try
            {
                return await operation(sessionId, ct);
            }
            catch (IsbmException ex) when (ex.IsSessionProblem)
            {
                last = ex;

                logger.LogWarning(
                    "Session {SessionId} for {ParticipantId} on {ChannelUri} is unusable " +
                    "({Fault}); attempt {Attempt} of {Attempts}.",
                    sessionId, participantId, channelUri,
                    ex.Fault ?? ex.Status.ToString(), attempt, attempts);

                // Discard the stored id so the next pass opens a fresh session
                // rather than retrying the same dead one.
                await stores.For(participantId).ClearAsync(kind, ct);

                if (attempt < attempts)
                {
                    await Task.Delay(ConfirmDelay * attempt, ct);
                }
            }
        }

        throw (Exception?)last ?? new InvalidOperationException(
            $"Could not obtain a usable {kind} session on {channelUri}.");
    }

    /// <summary>
    /// Polls a harmless read until the provider acknowledges the session exists.
    ///
    /// A read on an empty queue returns null, which is indistinguishable from
    /// success — that is the point. What is being waited on is the absence of a
    /// session fault, not the presence of a message.
    /// </summary>
    /// <summary>
    /// Waits until the provider acknowledges the session.
    ///
    /// Every kind is confirmed, not just those with a readable queue. Publication and
    /// consumer-request sessions have nothing to poll as a proxy, and a fixed delay
    /// instead of a check is a coin toss: the same code with the same delay succeeds
    /// on one run and fails on the next.
    /// </summary>
    private async Task ConfirmAsync(
        string participantId, IsbmSessionKind kind, string sessionId, CancellationToken ct)
    {
        var client = clients.For(participantId);

        for (var attempt = 1; attempt <= ConfirmAttempts; attempt++)
        {
            try
            {
                if (await client.SessionExistsAsync(sessionId, ct))
                {
                    // A readable queue gives a second, stronger signal: the session
                    // is not merely present but usable.
                    if (kind is IsbmSessionKind.Subscription)
                    {
                        await client.ReadPublicationAsync(sessionId, ct);
                    }
                    else if (kind is IsbmSessionKind.ProviderRequest)
                    {
                        await client.ReadRequestAsync(sessionId, ct);
                    }

                    if (attempt > 1)
                    {
                        logger.LogInformation(
                            "Session {SessionId} became visible after {Attempts} attempt(s).",
                            sessionId, attempt);
                    }

                    return;
                }
            }
            catch (IsbmException ex) when (ex.IsSessionProblem)
            {
                // Not yet visible. Expected on the first attempt or two.
            }

            if (attempt < ConfirmAttempts)
            {
                await Task.Delay(ConfirmDelay * attempt, ct);
            }
        }

        logger.LogWarning(
            "Session {SessionId} was not confirmed after {Attempts} attempts; proceeding anyway.",
            sessionId, ConfirmAttempts);
    }
}
