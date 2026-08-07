# Integrating with the ws-ISBM Provider

Notes from building the ws-CIR channel binding against the ISBM Provider at
`isbm-func-*`. Two audiences: anyone writing another ISBM client, and whoever
maintains the ISBM Provider itself.

`isbm-roundtrip-sequence.puml` in this folder diagrams the full flow, annotated
with the traps described below.

---

## The wire contract, as confirmed against source

All nine provider-side routes, verified by reading `ProviderRequestFunctions.cs`,
`ConsumerRequestFunctions.cs` and `ConsumerPublicationFunctions.cs`. Guessing
these cost most of a session; do not guess again.

| Operation | Route |
|---|---|
| OpenProviderRequestSession | `POST provider-request-sessions` |
| ReadRequest | `GET sessions/{sessionId}/request` |
| RemoveRequest | `DELETE sessions/{sessionId}/request` |
| PostResponse | `POST sessions/{sessionId}/requests/{requestMessageId}/response` |
| CloseProviderRequestSession | `DELETE provider-request-sessions/{sessionId}` |
| OpenSubscriptionSession | `POST subscription-sessions` |
| ReadPublication | `GET sessions/{sessionId}/publication` |
| RemovePublication | `DELETE sessions/{sessionId}/publication` |
| CloseSubscriptionSession | `DELETE subscription-sessions/{sessionId}` |

Consumer side, for test harnesses:

| Operation | Route |
|---|---|
| OpenConsumerRequestSession | `POST consumer-request-sessions` |
| PostRequest | `POST sessions/{sessionId}/requests` |
| ReadResponse | `GET sessions/{sessionId}/requests/{requestMessageId}/response` |
| RemoveResponse | `DELETE sessions/{sessionId}/requests/{requestMessageId}/response` |
| ExpireRequest | `DELETE sessions/{sessionId}/requests/{messageId}` |
| CloseConsumerRequestSession | `DELETE consumer-request-sessions/{sessionId}` |
| OpenPublicationSession | `POST publication-sessions` |
| PostPublication | `POST sessions/{sessionId}/publications` |

**There is no shared `DELETE sessions/{id}`.** Closing is per session type, even
though every other session-scoped route is `sessions/{id}/…`.

`PostResponse` and `ReadResponse` now share the same shape — the request message
id is in the path for both. Do **not** also send it in the body; unknown members
are rejected.

### Bodies

`channelUri` travels in the **body** for session-open, not the path — a channel URI
contains slashes.

Send **only the members that session type declares**. The provider runs with
`UnmappedMemberHandling.Disallow`, so anything undeclared is a 400
`DeserializationError` naming the offending property and its JSON path.

**Topics belong only to sessions that filter what they read:**

| Session | Topics | Rationale |
|---|---|---|
| ProviderRequest | yes | which requests this provider will read |
| Subscription | yes | which publications this subscriber receives |
| ConsumerRequest | **no** | topics are supplied on each `PostRequest` |
| Publication | **no** | topics are supplied on each `PostPublication` |

That mirrors ws-ISBM itself, where `OpenConsumerRequestSession` takes only
`ChannelURI` and an optional `ListenerURL`.

```json
// provider-request-sessions, subscription-sessions
{ "channelUri": "/OIIE/CIR/Request", "topics": ["ws-CIR"] }

// consumer-request-sessions, publication-sessions
{ "channelUri": "/OIIE/CIR/Request" }
```

**`filterExpressions` is on subscription sessions and must be sent**, even when
empty. `ContentFilterEngine.Matches` reads it on every `ReadPublication`, and
omitting the member leaves it null rather than empty — which throws a
`NullReferenceException` and surfaces as a bare 500. An empty list means "match
everything"; an absent member means "crash".

```json
// subscription-sessions
{ "channelUri": "/OIIE/CIR/Publication", "topics": ["ws-CIR"], "filterExpressions": [] }
```

`expirationListenerUrl` is on subscription sessions only. Add `listenerUrl` when
using callback delivery rather than polling, and `securityToken` for secured
channels.

`MessageContent` is `{ mediaType, inlineContent, payloadRef }`:

```json
{
  "topics": ["ws-CIR"],
  "expiry": "P1D",
  "messageContent": {
    "mediaType": "application/xml",
    "inlineContent": "<ProcessRegistry …/>"
  }
}
```

- **`mediaType`, not `contentType`.** It is `required`, so a wrong name throws
  during deserialisation.
- **`inlineContent`, not `content`.** It is *optional*, so a wrong name is worse:
  the post returns **200 with a messageId** and the payload is silently discarded.
  The failure surfaces two hops later on read.
- **`payloadRef`** is the claim-check path for large payloads, stored in blob.
  Not dereferenced by this client — detected and reported instead.

---

## Sessions are not usable immediately after opening — fixed, but design for it

**Resolved in the target provider:** `SessionHelper.OpenAndConfirmAsync` now polls
the Durable Entity until it is confirmed open before returning the session id, and
`GetValidatedSessionAsync` reports three distinct conditions instead of one.

Kept here because the race is inherent to any provider that acknowledges an open
before committing state, and because it cost most of a day to diagnose. The
original symptom:

```csharp
if (entity?.State is not { IsOpen: true, Metadata.SessionType: … } state)
    return await req.FaultAsync(IsbmFaultException.Session(status: 422));
```

A not-yet-committed entity fails that check **identically to a wrong session
type**, and the message says `"Session does not exist or wrong type."` We spent
several rounds investigating session types before realising it was a race.

Settling took more than six seconds in practice, and over fifteen on a cold app.

Client-side mitigation remains in `IsbmBodListener.ReadWithSessionRecoveryAsync`
as a safety net: a session opened moments ago gets a few retries with backoff; a
session problem on a session we did *not* just open is treated as real and the
stored id is discarded. That distinction matters — without it, a race produces a
new session every poll and leaks them on the broker, each still accumulating
messages nobody reads.

Detect session faults by **fault name**, not status code. `{"fault":"Session"}`
arrives as 422, which is otherwise indistinguishable from a validation failure.

---

## Session ids must be persisted

Function hosts recycle \u2014 freely on Consumption, and still on deployment or scale
events with Always On. Re-opening per poll leaks sessions.
`cir.IsbmSession` holds one row per session kind; `POST /api/isbm/reset` closes
and forgets them when the broker has been reset and the stored ids are stale.

---

## Nothing pushes an ISBM message to you \u2014 the host must stay resident

ISBM delivery is pull-only. A request posted to a channel is not delivered; it
waits until a consumer reads it. On the CIR that consumer is the `IsbmPoll`
timer function, so **the availability of the whole ISBM path is the availability
of the timer**.

Two ways this fails silently, both of which we hit:

1. **The host is not resident.** On a Consumption (`Y1`) plan the timer only runs
   while the host happens to be warm. Provision `B1` or higher with `alwaysOn`
   \u2014 which is what `infra/main.bicep` now defaults to.
2. **The timer never indexed.** `[TimerTrigger("%Isbm__PollSchedule%")]` fails to
   resolve, the WebJobs indexer disables the function, and the app otherwise
   starts and serves HTTP normally. The setting is now the flat
   `IsbmPollSchedule`. See the CIR README gotcha.

The symptom is identical in both cases and points nowhere useful: the requester
posts successfully, gets a session and a message id, and then times out waiting
for a response. Manually calling `POST /api/isbm/drain` makes the exchange
complete instantly \u2014 **that is the diagnostic**. If a drain fixes it, the
message path is fine and the poller is not running. Check App Insights for
`IsbmPoll` requests before investigating anything else.

---

## Poison messages must be removed

An unreadable message at the head of a queue blocks everything behind it. Early on
this client returned `null` on a parse failure, the listener read that as "queue
empty", and the drain reported `idle: true` with no errors — while two posted
messages sat unread behind a poison one.

`IsbmMessage.Content` is now nullable and `RawContent` always carries the payload.
An unparseable message is **removed** and reported in the drain report's
`discarded` array with a 200-character preview. That array is what identified the
`inlineContent` bug.

---

## Channels do not provision their Service Bus entities

`POST /channels` writes a SQL row and stops. No `CreateQueueAsync` or
`CreateTopicAsync` exists in the provider — only `CreateSubscriptionAsync`.

Entity naming, from `Infrastructure/EntityNaming.cs`, is the first 8 bytes of the
SHA-256 of the channel URI as hex:

| Channel type | Entities |
|---|---|
| Request | `req-<hash>` queue **and** `resp-<hash>` topic |
| Publication | `pub-<hash>` topic |

```powershell
function Get-IsbmHash([string]$uri) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($uri))
    return -join ($bytes[0..7] | ForEach-Object { $_.ToString('x2') })
}
```

Consequence: a channel created through the API has no backing entity. Sessions
open fine (SQL and Durable Entities), channel lookups succeed, and **every post
fails** — a state no client can detect or work around. The namespace we worked in
had orphaned entity sets from deleted channels alongside a channel with no topic.

---

## Recommendations for the ISBM Provider

Ordered by how much client time each would save.

1. ~~**`UnmappedMemberHandling.Disallow` on `JsonSerializerOptions`.**~~
   **Implemented.** Unknown members now return 400 `DeserializationError` naming
   the property and its JSON path — which immediately caught this client sending
   `expirationListenerUrl` to a session type that does not declare it. Exactly the
   class of silent failure that hid the `content` / `inlineContent` bug for four
   rounds. Clients must now be precise, which is the right trade.
2. ~~**Catch `JsonException` in the fault middleware.**~~ **Implemented** — A `required` member failing
   during model binding bypasses the global handler, so the client gets a bare 500
   with an empty body. Four wrong theories were eliminated before App Insights
   gave the answer. Every other failure path returns clean problem+json.
3. ~~**Split `"Session does not exist or wrong type."`**~~ **Implemented** — into distinct faults, or at
   least distinct messages, for: no such session, session closed, wrong type.
4. ~~**Make `OpenSession` synchronous.**~~ **Implemented** — Read the entity back before returning, or
   persist sessions to SQL — already on the backlog, and this is the concrete
   argument for it.
5. ~~**Have `CreateChannel` provision its entities**~~ **Implemented** — via the
   `ServiceBusAdministrationClient` already injected, and `DeleteChannel` remove
   them. Fault if provisioning fails, so the problem surfaces at channel-creation
   time.
6. ~~**Reject `MessageContent` with neither `inlineContent` nor `payloadRef`.**~~ **Implemented** — There
   is no legitimate empty message.
7. ~~**Align `PostResponse`**~~ **Implemented** —
   to match the read route and ISBM's own model, where `PostResponse` takes the
   request message ID as a first-class parameter.
8. **Publish an OpenAPI document.** *(outstanding)*
9. **Null-guard `ContentFilterEngine.Matches`.** A subscription session opened
   without `filterExpressions` leaves the member null, and `Matches` dereferences
   it on every `ReadPublication` — a bare 500 on a read path that has nothing to
   do with filtering. Treat null as an empty list: no filter, match everything.
   Same reasoning as rejecting empty `MessageContent`, in the opposite direction.
10. **Broaden the fault middleware beyond `JsonException`.** Fix #4 handles
    deserialisation, but any other unhandled exception still reaches the client as
    an empty-bodied 500. That is what hid this one. A catch-all that logs and
    returns a structured 500 with a correlation id would make every future
    failure self-diagnosing without exposing internals. Every field name and route this client got
   wrong would have come from a spec. ISBM 2.1 publishes one — generating yours and
   diffing against it would also show where the implementation has diverged from
   the standard, which matters more than client convenience: a conformant
   third-party client will send what the specification says, not what these DTOs
   happen to accept.

Six of these are **discoverability** problems rather than logic bugs. The provider
does the right thing internally; a client cannot tell what it wants, and when it
guesses wrong the feedback is absent, misleading, or silent.
