# SimHost

The sandbox. Four simulated participants — ENG, REG-LOCATION, MMS and
CMS — exchange CCOM BODs over the live ws-ISBM provider and resolve
identity through the live ws-CIR provider. It is the only thing in the repository
that proves the round trip, and the only place where the claim "the data arrived"
can be checked against rows rather than logs.

## What it demonstrates

An engineer creates a tag. A steward approves a location. A planner opens the
maintenance register and the location is there. Nobody in that story sees a BOD,
which is the point.

Underneath, the interesting behaviour is what happens to a property value as it
crosses three systems that hold different reference data:

| | Holds the class | Holds the definition | `Control action` arrives as |
|---|---|---|---|
| ENG | `rdl:TemperatureIndicatingController` | yes | authored |
| REG-LOCATION | no — binds at `rdl:Instrument` | yes | **mapped**, classification degraded |
| MMS | no reference data at all | no | **unmapped**, retained |

The value is never dropped. A receiver that discards what it cannot classify
makes federation an all-or-nothing proposition, and it is not one — MMS holds
the value, flagged, ready for the day someone gives it a definition.

## Running it

```pwsh
cd SimHost
dotnet run --launch-profile SimHost
```

The launch profile is `SimHost`. There is no `https` profile, and passing a
profile name that does not exist **silently skips**
`ASPNETCORE_ENVIRONMENT=Development`, so `appsettings.Development.json` is never
loaded and every database connection fails with
`Configuration 'Sandbox:Environment' is not set.` The error does not mention the
launch profile, so it is worth ruling out first.

Copy `appsettings.Development.json.template` and fill in the SQL and Key Vault
values before the first run.

## The UI

`/` lists the configured participants. Each one carries a **Repository contents**
expander showing what is actually in that participant's schema: its own domain
tables first, the infrastructure tables every participant carries grouped below.
Tables and columns are read from the EF model rather than a maintained list, so a
table added to `ParticipantDbContext` appears without a second edit. Rows are
capped at 100 with the true count shown, since a grid that stops silently at its
limit invites the conclusion that the table ends there; the capped read is
ordered by primary key, because an unordered `TOP` may return a different 100
each time and a view meant to show what is stored cannot afford that. Reads run
as the participant's own contained SQL user, so a table the participant cannot
read reports as unreadable rather than being quietly skipped — under the
isolation this sandbox exists to demonstrate, that is a finding worth seeing.

`/runs` lists scenario runs and starts new ones. `/runs/{id}` opens a run across
four tabs, polling every two seconds while it is still executing.

- **Identity** — federation ids with the participant codes clustered under them.
  One identity, three codes, one minter. Competing minters and missing identities
  are flagged rather than smoothed over.
- **Results** — steps and assertions, with severity. Concerns are separated from
  failures because a degraded classification is expected behaviour, not a defect.
- **Message flow** — each BOD as a sender/receiver row. Every row links to a
  detail page showing the source record, the BOD XML, the resulting records and
  the provenance trail, in that order.
- **Persisted data** — what each participant's schema actually contains for the
  run's identities, read straight out of SQL. This is the tab that answers "did
  it really arrive", and it distinguishes *no rows* from *store unreadable*,
  because those are different problems.

Pages are static-rendered unless they declare otherwise. Anything with a click
handler needs `@rendermode InteractiveServer` on the page or the component, or
the markup renders and the handler never fires — the control looks fine and does
nothing, with no error anywhere to say why.

## Resetting

`/runs` carries **Reset** and **Day zero**, both behind a confirmation. They call
the same admin endpoints the test scripts use:

```pwsh
Invoke-RestMethod -Method Post https://localhost:7180/admin/reset
Invoke-RestMethod -Method Post https://localhost:7180/admin/reset/day-zero
Invoke-RestMethod -Method Post https://localhost:7180/admin/schema/reset
```

`reset` closes ISBM sessions, purges the sandbox's own channels, truncates
participant tables and reloads the class fixtures. It never deletes
`/OIIE/CIR/Request` — that channel belongs to the CIR provider and holds its
long-lived provider-request session; deleting it breaks the provider for
everyone.

`schema/reset` drops and recreates the participant schemas. Needed after adding
a table, because `EnsureTablesAsync` only checks for a sentinel and cannot
migrate a schema that already exists.

Reset output reports class and property counts per participant. Seeing
REG-LOCATION at 3 property definitions and MMS at 0 confirms the asymmetry
survived the reload; equal counts mean a fixture leaked.

## iTwins

ENG holds its design data per iTwin, so a tag number identifies an instrument
*within a plant* rather than across the estate. Two twins can each hold a
`TIC-500` and they remain two distinct tags with distinct identities.

```
GET  /admin/eng/twins
POST /admin/eng/twins              { iTwinId, code, name, description }
GET  /admin/eng/tags?iTwinId=...
```

The twin comes from the request body, an `x-itwin-id` header, or ENG's default
twin when neither is supplied — which is why the routes that predate the twin
dimension, and the scenarios that drive them, are unchanged.

Isolation is enforced by EF global query filters rather than by remembering a
`WHERE` clause at each call site, so a read cannot accidentally cross plants.
`FederationId` is deliberately left globally unique: it is minted per tag and is
the correlator MMS and CIR resolve on, so scoping it by twin would break identity
resolution between participants.

Only ENG is twin-scoped. REG-LOCATION, MMS and CMS are not, and the
handover scenarios run entirely in the default twin.

The isolation behaviour is exercised by Bruno requests 20-28 under
`testing/bruno/sandbox/OiieSandbox`, not by a YAML scenario.

## Scenarios

`Scenarios/*.yaml`, run from the UI or over HTTP. Files are named for the OpenO&M
scenario they realise; the use case each belongs to is recorded in the file's
`useCase:` field as a cross-reference.

- `sc01-design-release.yaml` — Scenario 1 (UC01): ENG releases a named version and
  the tag lands in REG-LOCATION's stewardship queue. It stops there. REG-LOCATION is
  a release gate, so the scenario closes by asserting that nothing reached MMS —
  early design data is a proposal, not something operations should be planning
  against. Design relationships travel on this leg too and are retained *unresolved*,
  against the sender's tag numbers, because the registry has no codes of its own to
  state them with until the endpoints are approved.
- `sc02-operations-release.yaml` — Scenario 2 (UC01): a steward approves the
  proposal, which is what actually releases it to MMS, followed by CIR registration
  and resolution. Approval also resolves the retained relationships to the registry's
  own codes and republishes them, so the topology reaches operations with the
  locations it names. Requires `sc01-design-release` and does not reset, because it
  consumes the queue that scenario leaves behind. Run on its own it fails on its
  first assertion rather than silently approving nothing.
- `sc01-greenfield-allocation.yaml` — Scenario 1 (UC02): the same publish, but with
  the code allocated by the identity service rather than authored in the file. Re-run
  safe: it asserts relative code sequences rather than literal `P-001`, so it does not
  depend on being the first run against the database.
- `sc11-asset-install.yaml` — Scenario 11 (UC05): MMS publishes asset install and
  removal events to CMS. Requires `sc02-operations-release` and does not
  reset. It does not perform the handover itself: authoring a tag and approving a
  proposal are engineering acts, and a maintenance process has no business doing
  them. Run without the handover, it fails on its first assertion with no wait.

Assertions carry a severity. `bod_valid` failing is a defect;
`classification_degraded` firing at REG-LOCATION is the scenario working.

Two things `sc11-asset-install` asserts that are easy to lose in a refactor. Publication is
triggered by planner **sign-off**, not by completion, so the scenario asserts
`message_not_received` while the order sits completed. And the receiver stores
both the install and the removal rather than overwriting a "currently installed"
field — an overwriting model cannot afterwards say the asset ever ran there,
which destroys the service interval the feed exists to provide.

## Layout

```
Application/
  Bods/            CCOM attribute and property mapping, builder contracts
  Classification/  class binding, narrowing rules, property ingestion
  Cir/             registration and identity resolution
  Identity/        code allocation - codes are not identities
  Inbox/Outbox/    the message pumps
  Scenarios/       runner, launcher, and the read-side services behind the UI
Domain/            per-participant entities, plus shared classification types
Personalities/     ENG, REG-LOCATION and MMS handlers and builders
PersonalityPacks/  participant config and fixtures - see the README there
Components/        the Blazor UI
Scenarios/         scenario definitions
```

## Adding a participant

See [`PersonalityPacks/README.md`](PersonalityPacks/README.md). Two of the steps
are easy to miss and fail quietly: the property ingestion call in the handler,
and the case in `MessageTransformService.ReadRecordsAsync`. Without the first the
participant stores only the CCOM spine; without the second the UI reports it
holds no record, which reads as a finding rather than the omission it is.
