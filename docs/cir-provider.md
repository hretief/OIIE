# ws-CIR Provider

A REST and OAGIS-BOD implementation of the OpenO&M **ws-CIR 1.0** specification
(Web Service Common Interoperability Registry), hosted on Azure Functions
(.NET 10 isolated worker) over Azure SQL, with a ws-ISBM channel binding.

All eleven ws-CIR services are implemented. The Annex A message model is
implemented and verified against the published BOD schemas. BODs flow over
ws-ISBM in both request-response and publication patterns.

---

## What ws-CIR actually is

The card catalogue holds no books. A CIR server stores **no asset data** — no
equipment specs, no maintenance history. It stores only the answer to *"are these
two records talking about the same thing?"*

Three systems each have a record for the same physical valve:

| IdInSource | SourceId | CIRID |
|---|---|---|
| BBHV0013 | Asset Register | 8B907609-5955-4694-B244-107B0101F22F |
| BBHV-13 | Operations Center | 8B907609-5955-4694-B244-107B0101F22F |
| BB-HV-0013 | CMMS | 8B907609-5955-4694-B244-107B0101F22F |

Entries sharing a CIRID are declared equivalent. Nothing is inferred —
equivalence is always **asserted**, and staying true over time is the caller's
problem, not the registry's.

Two SourceIDs mean different things and this trips people up. On a **Category**,
`SourceID` names the *scheme* that defines the classification (`MIMOSA OSA-EAI V3`,
`ISA 95-2000 EquipmentModel`). On an **Entry**, `SourceID` names the *system* that
holds the record (`CMMS`, `Asset Register`).

### CIRID is not an ISBN

`Entry.Cirid` is settable on `CreateRegistry`, so a caller can supply its own
authoritative UUID rather than accepting a server-minted one. That is the
recommended pattern: seed the asset register's entry **first** with its UUID, and
because §3.1.2 gives precedence to the existing entry's CIRID, no later alias
registration can displace it.

The CIRID is a *cluster* identifier, not an authority identifier — closer to an
OCLC control number or a VIAF cluster ID than an ISBN. Two CIR servers over the
same plant will mint entirely different CIRIDs, and `UpdateEntryCIRID` exists
precisely so they can be collapsed later.

---

## Layout

```
CIR/
├── CirProvider/              the Function App project
│   ├── Application/          ports: ICirStore, IBodDispatcher, IIsbmClient, listener
│   ├── Domain/               ws-CIR data model (§2), faults (§3.3), Bod/ catalogue
│   ├── Functions/            HTTP and timer triggers
│   ├── Infrastructure/
│   │   ├── Bod/              OAGIS envelope read/write
│   │   ├── Isbm/             ws-ISBM REST client, SQL session store
│   │   └── Sql/              SQL adapter, schema DDL, filter translator
│   └── Middleware/           faults → RFC 9457 problem+json
├── Testing/                  test scripts and Bruno collection
├── deploy/                   provision.ps1, upgrade-packages.ps1
├── docs/                     integration notes + sequence diagram
├── infra/                    main.bicep + modules
└── NuGet.config              clears inherited feeds — see Gotchas
```

Ports and adapters throughout: `ICirStore` is the only thing that touches SQL,
and the BOD and ISBM layers were added without changing it.

---

## Deploy

```powershell
cd "D:\Working\OpenO&M\Common Interoperability Registry\CIR"

# After extracting an archive — RemoteSigned blocks unsigned scripts otherwise.
Get-ChildItem -Recurse -Include *.ps1 | Unblock-File

# Once, so Visual Studio opens cleanly.
dotnet new sln -n CirProvider
dotnet sln add .\CirProvider\CirProvider.csproj

az login
.\deploy\provision.ps1 -ResourceGroup <rg> -Location <region> `
    -SqlServerMode existing -ExistingSqlServerName <server>
```

Idempotent — re-run after any change. Omit `-SqlServerMode` for a greenfield SQL
server. Takes 4–6 minutes; the SQL work dominates.

**Named `provision.ps1`, not `deploy.ps1`.** The project template reserves
`deploy/deploy.ps1` for a post-build step taking `-TargetDir`, invoked by an
`Exec` task in the csproj. Do not rename this back.

The script does what Bicep cannot: opens a SQL firewall rule for the address your
TDS traffic actually presents as, creates the contained database user for the
managed identity and grants its roles, and sets `COMPATIBILITY_LEVEL = 170`
(required for `REGEXP_LIKE`). Then it builds and publishes.

### Flags

| Flag | When |
|---|---|
| `-SkipPublish` | infrastructure only, no code deploy |
| `-EnableIsbm` / `-DisableIsbm` | turn the ISBM listener on or off deliberately |
| `-IsbmBaseUrl`, `-IsbmApiKey` | point at the ws-ISBM provider |
| `-IsbmTopic <name>` | topic the CIR listens on (default `ws-CIR`) |
| `-DotnetVersion 8.0` | roll the runtime back |
| `-SqlAdminObjectId`, `-SqlAdminPrincipalType` | when `az ad signed-in-user` cannot resolve you |
| `-BaseName` | prefix for generated resource names (default `cir`) |
| `-PlanSku` | App Service plan SKU (default `B1`). `Y1` = Consumption — see *Hosting the ISBM poller* |
| `-IsbmPollSchedule` | NCRONTAB expression for `IsbmPoll` (default `*/15 * * * * *`) |

### Verify

```powershell
Invoke-RestMethod "https://<app>.azurewebsites.net/api/health"

.\Testing\test-cir.ps1 -FunctionApp <app> -ResourceGroup <rg>
.\Testing\test-isbm-roundtrip.ps1 -CirApp <app> -IsbmApp <isbm-app> -ResourceGroup <rg>
.\Testing\conformance-cir.ps1 -FunctionApp <app> -ResourceGroup <rg> -OutputPath .\conformance-statement.md
```

Health returns `status: healthy` and `sql: true`. The first call after an idle
period takes 30–60 s while the serverless database resumes.

### Changing the .NET version

Package resolution is framework-aware, so edit `TargetFramework` in the csproj
**first**, then:

```powershell
.\deploy\upgrade-packages.ps1
```

That refreshes every reference to its latest stable, excludes the pinned
Application Insights packages, and fails if a 3.x resolution creeps in. See
Gotchas.

### Enabling the ISBM binding

```powershell
$isbmKey = az functionapp keys list -g <rg> -n <isbm-app> --query functionKeys.default -o tsv

.\deploy\provision.ps1 -ResourceGroup <rg> -Location <region> `
    -SqlServerMode existing -ExistingSqlServerName <server> `
    -EnableIsbm -IsbmBaseUrl "https://<isbm-app>.azurewebsites.net/api" -IsbmApiKey $isbmKey
```

Subsequent runs **carry the ISBM settings forward** — the preflight reads them off
the deployed app, so a plain redeploy will not silently disable the listener.

The two channels must exist on the ISBM provider first, with the right types:

```powershell
Invoke-RestMethod "$isbm/channels" -Method Post -Headers $h -ContentType application/json -Body (@{
    channelUri = '/OIIE/CIR/Request'; channelType = 'Request' } | ConvertTo-Json)
Invoke-RestMethod "$isbm/channels" -Method Post -Headers $h -ContentType application/json -Body (@{
    channelUri = '/OIIE/CIR/Publication'; channelType = 'Publication' } | ConvertTo-Json)
```

`-IsbmApiKey` is the Azure **function key** for the ISBM app. It is *not* the
ws-ISBM channel security token (§2.2) — that is `Isbm__SecurityToken`, and is only
needed for secured channels.

### Hosting the ISBM poller

**Read this before choosing a plan.** The ISBM binding is not request-driven. A
CIR request arrives as a message on an ISBM channel, and nothing calls the CIR to
tell it so. The only thing that moves that message is the `IsbmPoll` timer
function. If the host is not resident, the timer does not fire, the request sits
in the queue, and the consumer times out with no error anywhere — the CIR looks
healthy the whole time.

So `provision.ps1` defaults to **`-PlanSku B1` with `alwaysOn: true`**. Do not
change this in production without understanding the consequence:

| Plan | Always On | Poller behaviour |
|---|---|---|
| `B1` and above (default) | `true` | Timer fires on schedule. Correct for production. |
| `Y1` (Consumption) | forced `false` by the template | Timer only fires while the host happens to be warm. Requests are delivered late or not at all. Demo use only. |
| `EP1`+ (Elastic Premium) | `true` | Also correct; use when VNet integration or larger scale-out is needed. |

The template derives the tier from the SKU (`Y1`→Dynamic, `EP*`→ElasticPremium,
otherwise Basic) and forces `alwaysOn: false` on `Y1` because Azure rejects it
otherwise.

The poll interval is the app setting **`IsbmPollSchedule`** — a 6-field NCRONTAB
expression, default every 15 seconds. Note the flat name. It is deliberately
*not* `Isbm__PollSchedule`; see Gotchas.

To verify the poller is actually alive after a deploy — this is the single most
useful check on this app:

```powershell
# The function must be listed, and it must show timerTrigger.
az functionapp function list -g <rg> -n <app> --query "[].{name:name,trigger:config.bindings[0].type}" -o table

# App Insights: IsbmPoll should appear roughly on the poll interval.
az monitor app-insights query --app <ai> --analytics-query \
  "requests | where name == 'IsbmPoll' | summarize count() by bin(timestamp, 5m) | order by timestamp desc"
```

No `IsbmPoll` rows means the function is not running, regardless of what the
portal shows as "Enabled".

---

## Endpoint map

| Clause | Operation | Method | Route |
|---|---|---|---|
| §3.1.1 | CreateRegistry | POST | `/api/registries` |
| §3.1.2 | CreateEquivalentEntries | POST | `/api/equivalent-entries` |
| §3.1.3 | UpdateRegistry | PUT | `/api/registries` |
| §3.1.4 | UpdateEntryCIRID | POST | `/api/cirids/replace` |
| §3.1.5 | DeleteRegistry | DELETE | `/api/registries/{id}` |
| §3.1.6 | DeleteCategory | DELETE | `/api/categories` |
| §3.1.7 | DeleteEntries | POST | `/api/entries/batch-delete` |
| §3.1.8 | DeleteProperties | POST | `/api/properties/batch-delete` |
| §3.2.1 | GetRegistry | POST | `/api/queries/registry` |
| §3.2.2 | GetEquivalentEntries | POST | `/api/queries/equivalent-entries` |
| §3.2.3 | GetEntriesByCIRID | GET | `/api/entries?cirid=` |
| Annex A | Post a BOD | POST | `/api/bods` |
| Annex A | BOD catalogue | GET | `/api/bods/catalogue` |
| — | Health | GET | `/api/health` (anonymous) |
| — | ISBM status / drain / reset | GET/POST | `/api/isbm/status`, `/drain`, `/reset` |

Queries are POST because their inputs are five-part composite keys and regex
filters. The spec's own `SourceID` examples contain spaces and `#`, so nothing
composite goes in a path.

---

## Design decisions

**Surrogate keys, natural unique indexes.** `BIGINT IDENTITY` primary keys carry
the foreign keys; the spec's composite natural keys are unique constraints.
`ON DELETE CASCADE` gives DeleteRegistry and DeleteCategory their required
cascades by construction rather than in application code.

**Atomicity is structural.** §3.1 forbids partial writes when a fault is raised.
Every command runs in one `ReadCommitted` transaction over a single connection,
and the test suite asserts it: a two-element batch whose second member faults must
leave the first uncommitted.

**Wildcards are anchored.** §4's POSIX subset is implicitly anchored at both ends,
but `REGEXP_LIKE` is a substring match by default. Every pattern is wrapped as
`^(...)$`. Without this, `Alpha` matches `Alpha One` and the implementation is
non-conformant *while appearing to work* — the single easiest thing to get wrong
in this spec.

**Filter semantics.** §3.2.1: the four filter types AND together; multiple filters
**of the same type** OR together regardless of which `Filter` element carried them;
an absent type is logical TRUE. `CirFilterTranslator` collects by type first for
that reason. `PropertyFilter` key and value must co-occur in the *same*
`PropertyValue`, not merely both appear somewhere.

**Fault names are verbatim.** §5 conformance is declared against them, so
`CirFaultCode` values are the spec's spellings and appear unchanged in the
`faults[]` member of the problem+json body and in BOD fault elements.

**Azure SQL over Table Storage or Postgres.** CIRID lookup is a secondary index
across all registries, which Table Storage cannot do without a hand-maintained
inverse index in a different partition. `UpdateEntryCIRID` fans out across
arbitrary partitions and cannot be made atomic there. Postgres was a close call
until Azure SQL gained native `REGEXP_LIKE` (GA November 2025), which removed the
one structural advantage it had.

---

## Spec interpretations

§5 item 6 requires interpretations to be stated. These are also emitted in the
generated conformance statement.

**§3.2.3** says the existing Entry is not returned. The input is a bare CIRID, so
there is no specified Entry to exclude — the sentence appears to be carried over
from §3.2.2. All Entries carrying the CIRID are returned.

**§3.1.2** does not say what happens when the existing Entry and the supplied
Entry carry *different* CIRIDs. The existing CIRID wins and the supplied value is
discarded, consistent with the stated precedence. Merging clusters is left to
§3.1.4, which is explicit rather than implicit.

**§3.1.3** is a snapshot replace, so omitted attributes are **cleared**. Two
qualifications: children that are not supplied are left alone rather than deleted
(a separate Delete family exists, and the alternative makes partial updates
impossible), and CIRID is preserved when omitted (§3.1.4 is a dedicated operation
for it, and §3.1.2 treats it as server-managed correlation state).

---

## Defects found in the ws-CIR 1.0 package

Worth reporting to `github.com/mimosa-org/ws-cir` — the specification is a
Candidate Standard and explicitly unstable.

1. **`GetRegistry.xsd` and `GetEquivalentEntries.xsd` declare `<xs:element ref="oa:Process"/>`**
   where the Annex A catalogue lists their verb as **Get**, and where the
   request/response pairing makes them Get/Show. This implementation accepts
   either; see `BodVerbElements`.
2. **`GetEntriesByCIRID.xsd` and `ShowEntriesByCIRID.xsd` are missing** from the
   BOD package. Both appear in the catalogue; no file references `EntriesByCIRID`.
   The established pattern is followed (`cir:GetEntriesByCIRID` /
   `cir:GetEntriesByCIRIDResponse`) but is unverified.
3. **§3.2.3's exclusion clause is unreachable** — see interpretations above.

### Faults on unreadable requests

A recognised BOD that cannot be processed — malformed noun, missing mandatory
element, unexpected error — is answered with its **response BOD carrying a
fault**, never discarded silently. A sender cannot distinguish a discarded
request from a provider that is asleep, so silence is the worst available
behaviour.

The specification has no fault code for "your document was unreadable", and the
reply must use a code the response BOD's schema declares. The first declared code
is used, with the real cause in the `Description`. Over ISBM, a request whose
*response* could not be posted is deliberately left queued for retry, since that
failure is the provider's rather than the sender's.

### Known non-conformance in fault elements

The NotFound and Duplicate faults declare a **mandatory identifier child**
(`RegistryIdentifier`, `CategoryIdentifier`, `EntryIdentifier`,
`PropertyIdentifier`). This implementation emits only `Description`, because the
store reports faults as a code and a message and the identifier would have to be
reconstructed. Faults are therefore schema-valid in name and ordering but
incomplete in content.

---

## Element names that differ from the JSON binding

The XML element names follow the ws-CIR service definition schema, which is not
symmetric with the camelCase JSON binding. Two traps:

| Type | Schema element | JSON |
|---|---|---|
| **Category** | `CategorySourceID` | `sourceId` |
| Entry | `SourceID` | `sourceId` |
| CategoryFilter | `CategorySourceID` | `sourceId` |
| EntryFilter | `SourceID` | `sourceId` |

A client that writes `<SourceID>` inside `<Category>` produces a document that
fails schema validation, and the element is `minOccurs="1"`. The reader accepts
`SourceID` on Category as a fallback, but emits `CategorySourceID`.

Fault elements carry their detail in a **`Description` child**, not as element
text — `<DuplicateEntryFault><Description>…</Description></DuplicateEntryFault>`.
A client reading `.InnerText` sees the same string either way, but one reading
child elements sees nothing if the detail is written as text.

## Annex A notes

Acknowledge and Respond BODs have **no noun element**. Their DataArea is the verb
followed by fault elements, each named for the fault and repeatable, in the
`xs:sequence` order the schema declares. The catalogue's "CreateRegistry faults"
names the operation whose faults these are, not a wrapper.

The three fault orders are **not consistent with each other and not
alphabetical**, so they are copied per BOD rather than derived:

| BOD | Declared order |
|---|---|
| AcknowledgeRegistry | CreateRegistryFault, CreateCategoryFault, DuplicateEntryFault, DuplicatePropertyFault |
| AcknowledgeEquivalentEntries | EntryNotFoundFault, RegistryNotFoundFault, CategoryNotFoundFault, DuplicateEntryFault |
| RespondRegistry | RegistryNotFoundFault, CategoryNotFoundFault, EntryNotFoundFault, PropertyNotFoundFault |

Note `DuplicatePropertyFault` is **not** declared for AcknowledgeEquivalentEntries.

Other envelope details, all confirmed against `Meta.xsd`:

- `oa:ApplicationArea` is an **OAGIS** element; `cir:DataArea` is a ws-CIR element.
  Mixed namespaces in one envelope.
- `OriginalApplicationArea` belongs to `ResponseVerbType`, so it sits **inside**
  the verb element, not beside the ApplicationArea.
- `ApplicationAreaType` child order is fixed: Sender, Receiver, CreationDateTime,
  Signature, BODID, UserArea.
- `RequestVerbType` requires **at least one** `oa:Expression`, so a bare
  `<oa:Get/>` is not schema-valid.
- `acknowledgeCode` / `responseCode` are `ResponseCodeContentType`:
  **`Always` | `OnError` | `Never`**, unioned with `normalizedString` — so
  arbitrary vendor codes are legal and must not be rejected. Unknown values fall
  back to `Always`. `Never` suppresses the response even on fault; `OnError`
  emits one only on fault.
- `recordSet*` and `maxItems` paging attributes are deliberately ignored; Annex A
  excludes result paging because of the nested result structure.

---

## Conformance

Generated by `Testing/conformance-cir.ps1`. Conformance under ws-CIR is
**declarative**, not pass/fail: §5 requires an assessment to be qualified by six
items, the last being an explicit statement of non-conformance.

| Item | Status |
|---|---|
| 1. Command Services | Supported (8/8) |
| 2. Query Services | Supported (3/3) |
| 3. Wildcard Specification | Supported, verified empirically |
| 4. SOAP 1.1 / 1.2 | **Not supported** — REST binding only |
| 5. Specific BODs | Supported, including the ws-ISBM channel binding |
| 6. Statement | Partial conformance; item 4 plus the interpretations above |

Item 4 is structural. It is not claimable without a second binding.

---

## Gotchas

Things that cost real time. See `docs/isbm-integration-notes.md` for the
ISBM-specific ones.

### Azure Functions and .NET

- **`FunctionsApplication.CreateBuilder` requires Worker *and* Worker.Sdk on 2.x.**
  Mixing with 1.x produces `CS0234` on `Microsoft.Azure.Functions.Worker.Builder`.
- **Pin `Microsoft.ApplicationInsights` to the 2.x line.** 3.x removed
  `ITelemetryInitializer` from that namespace, and `Worker.ApplicationInsights`
  still binds to it. A 3.x resolution kills the worker at startup with a
  `TypeLoadException`, the build is clean, the deploy succeeds, and the only
  symptom is `Function host is not running`. `upgrade-packages.ps1` excludes them
  and fails if a 3.x resolution appears.
- **`NuGet.config` clears inherited feeds.** A machine-wide private feed returning
  401 degrades to an `NU1900` warning under `dotnet build` but fails
  `dotnet add package` outright.
- **Bicep app settings are declarative** — omitting one *removes* it. This silently
  erased the ISBM binding on a redeploy, which is why `provision.ps1` now reads
  existing settings and carries them forward.
- **Configuration binding appends to collections**, it does not replace them. A
  `List<T>` with a non-empty initialiser plus a matching config key yields
  duplicates. `IsbmOptions.Topics` defaults to empty; `EffectiveTopics` supplies
  the fallback.
- **`.NET 10` is unavailable on Linux Consumption only.** Windows Consumption is
  fine. `provision.ps1` rejects that combination up front.
- **`%...%` in a trigger attribute resolves against a *literal* app-setting name,
  not the configuration tree.** `[TimerTrigger("%Isbm__PollSchedule%")]` does not
  fall back to the `Isbm:PollSchedule` section — the WebJobs indexer fails with
  `Error indexing method 'Functions.IsbmPoll'. '%Isbm__PollSchedule%' does not
  resolve to a value.` and *disables that function*. Everything else keeps
  working, `/api/health` returns healthy, and `az functionapp function list`
  still shows the function as enabled. The only evidence is the trace log and the
  absence of `IsbmPoll` requests in App Insights. The setting is therefore named
  flat: **`IsbmPollSchedule`**. Keep any setting referenced from a binding
  attribute flat, and keep `local.settings.json`, `infra/main.bicep` and the
  deploy scripts in agreement — a manual fix in the portal is erased by the next
  deploy, because Bicep app settings are declarative.

### PowerShell

- **`Invoke-WebRequest` returns `byte[]`** for content types it does not classify
  as text — including `application/problem+json`. Decode before parsing, or the
  parse fails silently and every field reads as empty.
- **`@() | ConvertTo-Json` returns `$null`.** Piping an empty array sends zero
  items. Use `-InputObject`.
- **`az` exit codes do not throw** under `$ErrorActionPreference = 'Stop'`. Check
  `$LASTEXITCODE`, or a failed deployment cascades into meaningless downstream
  errors.
- **`Select-String` has no `-Recurse`.** Pipe from `Get-ChildItem`.
- **`Unblock-File` after extracting** any archive, or `RemoteSigned` blocks the
  scripts.

### ws-ISBM

- **Send only the JSON members that session type declares.** The provider runs
  with `UnmappedMemberHandling.Disallow`, so an undeclared property is a 400
  `DeserializationError`. Topics belong only to ProviderRequest and Subscription
  sessions — ConsumerRequest and Publication supply topics per message instead.
- **Field names that bite:** `mediaType` not `contentType` (required, fails loudly);
  `inlineContent` not `content` (optional, so a wrong name returns 200 and
  silently discards the payload).
- **Send `filterExpressions: []` on subscription sessions.** Absent is not the
  same as empty — the provider's content filter dereferences it on every read.
- **Sessions are not usable immediately after opening.** See
  `docs/isbm-integration-notes.md`.

### Testing

- **Scope every `GetRegistry` assertion to a fixture registry.** A CIR is a shared
  registry server; assertions that assume an otherwise-empty database break as
  soon as anyone else stores anything. This bit us with a leftover `OGI-Pilot`
  fixture producing doubled counts.
- **Assert the negative wildcard case.** `System .` returning 2 proves the regex
  works; `System` returning 0 proves it is anchored. Only the pair together
  demonstrates §4 conformance.

---

## Backlog

- OpenAPI document for the REST binding — nothing exists to generate from, since
  ws-CIR defines only WSDL and BODs. This is the artifact to contribute back if
  the REST binding is worth standardising.
- CI running `test-cir.ps1` and `conformance-cir.ps1` against a scratch resource
  group. Would have caught the Application Insights 3.x break before Azure did.
- Migrate to OpenTelemetry (`AddOpenTelemetry().UseFunctionsWorkerDefaults()`),
  removing the classic App Insights pin.
- `payloadRef` dereferencing for claim-checked ISBM payloads. Detected and
  reported today, not fetched. Needs blob read access.
- Move the ISBM API key into Key Vault, as SQL-auth mode already does for its
  connection string.
- Verify the fault element *content* model against
  `xsd/CommonInteroperabilityRegistry.xsd`, which was not available. The detail is
  currently emitted as element text.
