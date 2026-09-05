/**
 * Client for the OIIE Sandbox API (Oiie.Sandbox.Api).
 *
 * Every call the app makes to the sandbox goes through here, so that URLs, the
 * admin-key header and response reshaping live in one place rather than being
 * spread through the components.
 *
 * In development, requests go to a relative /admin path and Vite proxies them to
 * the API (see vite.config.ts). Set VITE_SANDBOX_API to call a deployed instance
 * directly instead, in which case that instance's Sandbox:AllowedCorsOrigins must
 * name this app's origin.
 */

const BASE = (import.meta.env.VITE_SANDBOX_API ?? '').replace(/\/$/, '')

// Deployed sandboxes set Sandbox:AdminKey, and the guarded /admin routes are
// rejected without it.
//
// Supplied at runtime and held in sessionStorage rather than read from
// import.meta.env at build time. Vite inlines every VITE_-prefixed variable into
// the bundle, and this bundle is served from the sandbox's own public wwwroot --
// so a build-time key is a downloadable key, which defeats the reason the app is
// hosted same-origin in the first place. It also unlocks /admin/reset and
// channel deletion, which the middleware's exemption list deliberately withholds
// from anonymous callers.
//
// VITE_SANDBOX_ADMIN_KEY is still honoured as a fallback so that local
// development, where the key is usually absent entirely, keeps working unchanged.
const ADMIN_KEY_STORAGE = 'oiie.sandbox.admin_key'

/** The key currently in effect, if any. */
export function adminKey(): string {
  try {
    const stored = sessionStorage.getItem(ADMIN_KEY_STORAGE)
    if (stored) return stored
  } catch {
    // sessionStorage unavailable (private mode, blocked). Fall through.
  }

  return import.meta.env.VITE_SANDBOX_ADMIN_KEY ?? ''
}

/** Remember a key for this tab. Cleared when the tab closes. */
export function setAdminKey(key: string): void {
  try {
    if (key) sessionStorage.setItem(ADMIN_KEY_STORAGE, key)
    else sessionStorage.removeItem(ADMIN_KEY_STORAGE)
  } catch {
    // Not fatal: the call below will simply be rejected and prompt again.
  }
}

/** A digital twin: the plant a design belongs to. */
export interface ITwin {
  id: string
  code: string
  name: string
  description: string | null
  createdAt: string
}

/**
 * How far a segment has travelled. This is the engine's real lifecycle -- a
 * segment is authored as WorkInProgress and only reaches Published by being
 * included in a promoted named version, which is a release event rather than an
 * edit.
 */
export type TagMaturity = 'WorkInProgress' | 'Shared' | 'Published'

/**
 * An engineering segment: an instrument or item of equipment in a design.
 *
 * The API calls this a Tag, which is the process-industry term. Infrastructure
 * does not use "tag", so the UI says "segment" throughout and these wire types
 * keep the server's names -- the translation happens here and nowhere else.
 */
export interface Tag {
  // Numeric: ENG assigns segments a sequential local id, unlike the twin's GUID.
  id: number
  tagNumber: string
  federationId: string | null
  /** The iModel the element's data comes from. Always set. */
  iModelId: string
  serviceDescription: string | null
  unitNumber: string | null
  classKey: string | null
  rangeMinimum: number | null
  rangeMaximum: number | null
  controlAction: string | null
  pidReference: string | null
  maturity: TagMaturity
  publishedInVersionId: string | null
  updatedAt: string
}

export interface TagList {
  iTwinId: string
  count: number
  tags: Tag[]
}

export interface NewTag {
  tagNumber: string
  serviceDescription?: string
  unitNumber?: string
  classKey?: string
  rangeMinimum?: number
  rangeMaximum?: number
  controlAction?: string
  /**
   * The identity this entity already has elsewhere -- a tag register, a handover
   * sheet, an earlier project. Omit it and ENG mints one, which is correct for a
   * segment first drawn here. Ignored when editing: the identity is fixed for the
   * entity's lifetime.
   */
  federationId?: string

  /**
   * The iModel to author into -- the source of the element data. Required when
   * authoring, and on an edit it is the element's own model: ENG scopes the
   * upsert by model, so it is needed either way.
   */
  iModelId?: string

  /**
   * ENG's ECInstanceId, sent only when editing. Absent means "create". ENG
   * matches an existing element by this id, so an edit that omitted it would
   * insert a second element and be refused as a duplicate code.
   */
  elementId?: number
}

export interface CreatedTag {
  id: number
  tagNumber: string
  federationId: string | null
  iTwinId: string
  iModelId: string
  maturity: TagMaturity
}

/**
 * The outcome of a promotion attempt.
 *
 * Returned on both success and refusal -- a blocked promotion answers 422 with
 * this same shape, because "why it was refused" is the useful part and a bare
 * status code would throw it away.
 */
export interface PromotionResult {
  released: boolean
  namedVersionId: number
  name: string
  /** How many segments were considered, published or not. */
  tagCount: number
  /**
   * How many markers the release cut.
   *
   * Always 1 sandbox-backed, where a named version is scoped to the twin. ENG
   * pins a marker within one iModel's history, so a twin spanning several
   * models releases several markers and namedVersionId names only the first.
   */
  markerCount?: number
  /** One line per rule violation, naming the segment. Empty when released. */
  findings: string[]
}

/**
 * A failed call, carrying whatever the API explained about it.
 *
 * The sandbox answers a rejected write with a JSON body naming the cause -- a
 * duplicate segment number, a missing twin -- and that text is far more useful
 * to whoever is driving the screen than the status code, so it is preserved
 * rather than collapsed into "request failed".
 */
export class SandboxError extends Error {
  constructor(
    message: string,
    readonly status: number,
    /**
     * The parsed body, when there was one. A promotion refusal carries its
     * findings here, which the caller needs in full rather than flattened into
     * a single message string.
     */
    readonly body?: unknown,
  ) {
    super(message)
    this.name = 'SandboxError'
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await send(path, init)

  // A 401 means this route is guarded and the key we hold is absent or wrong.
  // Prompting here rather than at each call site means every guarded route gets
  // the same treatment, including the ones that are only reachable from a button
  // press deep in the UI.
  //
  // Retried once. A second 401 is a wrong key rather than a missing one, and
  // looping would trap the user in a prompt they cannot dismiss.
  if (response.status === 401 && !init?.signal?.aborted) {
    const supplied = await promptForAdminKey()

    if (supplied) {
      setAdminKey(supplied)
      const retried = await send(path, init)
      return await unwrap<T>(retried)
    }
  }

  return await unwrap<T>(response)
}

async function send(path: string, init?: RequestInit): Promise<Response> {
  const headers = new Headers(init?.headers)
  headers.set('Accept', 'application/json')

  if (init?.body) {
    headers.set('Content-Type', 'application/json')
  }

  const key = adminKey()

  if (key) {
    headers.set('x-sandbox-admin-key', key)
  }

  try {
    return await fetch(`${BASE}${path}`, { ...init, headers })
  } catch {
    // fetch only rejects when the request never completed: the API is not
    // running, or the dev proxy could not reach it. Worth saying plainly,
    // because it is the most common state while developing and it is not a
    // fault in the app.
    throw new SandboxError(
      'Could not reach the sandbox API. Is Oiie.Sandbox.Api running?',
      0,
    )
  }
}

async function unwrap<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const failure = await describeFailure(response)
    throw new SandboxError(failure.message, response.status, failure.body)
  }

  return (await response.json()) as T
}

/**
 * Ask for the admin key.
 *
 * Deliberately `window.prompt`: this is a sandbox operator action, not a user
 * journey, and a bespoke modal would need routing through App state to reach
 * every call site. Replace it if the key ever needs to be entered often enough
 * for the ceremony to matter.
 *
 * Overridable so a host can substitute its own dialog without this module
 * knowing about the UI.
 */
let askForKey: () => string | null = () =>
  window.prompt(
    'This action needs the sandbox admin key.\n\n' +
    'az keyvault secret show --vault-name mndot --name sandbox-admin-key-dev --query value -o tsv',
  )

export function setAdminKeyPrompt(prompt: () => string | null): void {
  askForKey = prompt
}

async function promptForAdminKey(): Promise<string | null> {
  const supplied = askForKey()
  return supplied ? supplied.trim() : null
}

/**
 * Pull the most specific explanation the response offers, and keep the parsed
 * body alongside it.
 *
 * The body is read once: a Response stream cannot be consumed twice, so parsing
 * here and returning both is the only way the caller can have the structured
 * form as well as a message.
 */
async function describeFailure(
  response: Response,
): Promise<{ message: string; body?: unknown }> {
  const text = await response.text()

  if (!text) {
    return { message: `${response.status} ${response.statusText}` }
  }

  try {
    const parsed = JSON.parse(text) as Record<string, unknown>

    // The sandbox uses { error }, ProblemDetails uses { title, detail }.
    const message = parsed.error ?? parsed.detail ?? parsed.title

    if (typeof message === 'string' && message) {
      return { message, body: parsed }
    }

    return { message: `${response.status} ${response.statusText}`, body: parsed }
  } catch {
    // Not JSON. The raw body is still better than the status alone.
  }

  return { message: text.slice(0, 300) }
}

/**
 * The twins ENG holds designs for.
 *
 * May be empty on a fresh database: a twin is created implicitly by the first
 * write that names it, so nothing exists until something is authored.
 */
export function listTwins(signal?: AbortSignal): Promise<ITwin[]> {
  return request<ITwin[]>('/admin/eng/twins', { signal })
}

/**
 * ENG's segments within one twin.
 *
 * The twin is always passed explicitly. Omitting it would fall back to ENG's
 * default twin server-side, which silently shows the wrong plant's design
 * rather than failing -- the exact confusion the twin exists to prevent.
 */
export function listTags(iTwinId: string, signal?: AbortSignal): Promise<TagList> {
  return request<TagList>(
    `/admin/eng/tags?iTwinId=${encodeURIComponent(iTwinId)}`,
    { signal },
  )
}

/** Author a segment in a twin. Segment numbers are unique within their twin. */
export function createTag(iTwinId: string, tag: NewTag): Promise<CreatedTag> {
  return request<CreatedTag>('/admin/eng/tags', {
    method: 'POST',
    body: JSON.stringify({ ...tag, iTwinId }),
  })
}

/** An iModel as ENG holds it. */
export interface EngIModel {
  iModelId: string
  iTwinId: string
  code: string
  description: string | null
}

/**
 * The iModels ENG will accept segments against, for one twin.
 *
 * This is ENG's view rather than the platform's: a model the provider has never
 * been told about would be refused at authoring time, so offering it in the
 * picker would only produce a failure the user cannot act on.
 */
export function listIModels(iTwinId: string, signal?: AbortSignal): Promise<EngIModel[]> {
  return request<EngIModel[]>(
    `/admin/eng/imodels?iTwinId=${encodeURIComponent(iTwinId)}`,
    { signal },
  )
}

export interface SyncedIModel {
  recorded: boolean
  iModelId: string
  code: string
  detail?: string
}

/**
 * Records an iModel read from the platform so ENG will accept segments against it.
 *
 * The browser holds the IMS token and the sandbox does not, so the detail is
 * carried here rather than fetched server-side.
 */
export function syncIModel(
  iTwinId: string,
  iModelId: string,
  displayName: string | null,
  description: string | null,
): Promise<SyncedIModel> {
  return request<SyncedIModel>('/admin/eng/imodels/sync', {
    method: 'POST',
    body: JSON.stringify({ iModelId, iTwinId, displayName, description }),
  })
}

/**
 * A candidate federation id, for an operator with no register to copy one from.
 *
 * Asked of the server rather than generated here so a suggested identity is minted
 * by the same service and scheme ENG uses for its own. Nothing is reserved: this
 * answers "what would you have used", and the id is not real until a segment is
 * submitted carrying it.
 */
export function suggestFederationId(signal?: AbortSignal): Promise<{ federationId: string }> {
  return request<{ federationId: string }>('/admin/eng/federation-id/suggest', { signal })
}

/**
 * A candidate iTwin being brought into the sandbox.
 *
 * The platform's own fields are passed through rather than reduced to a name.
 * Type is what REG-LOCATION classifies the site by and names the boundary of
 * the twin -- District, Plant, Highway -- while class and subClass place the
 * twin on the lifecycle axis. The two are independent, so both are carried:
 * dropping either would mean the workflow could register a site it cannot
 * classify.
 */
export interface AddITwin {
  iTwinId: string
  displayName?: string | null
  number?: string | null
  description?: string | null
  class?: string | null
  subClass?: string | null
  type?: string | null
  status?: string | null
  parentITwinId?: string | null
}

/**
 * The outcome of adding an iTwin, in two parts.
 *
 * Registered and announced are separate because they fail separately. A twin
 * can be in the sandbox while the SyncSites publication never happened -- the
 * usual cause being that the ENG Functions host is not running -- and the
 * screen needs to say which of the two it got.
 */
export interface AddITwinResult {
  iTwinId: string
  code: string
  name: string
  registered: boolean
  announced: boolean
  /** Why the announcement did not happen. Null when it did. */
  detail: string | null
}

/**
 * Bring an existing platform iTwin into the sandbox.
 *
 * This is the entry point to SyncSites: registering the twin causes ENG to
 * announce it, and REG-LOCATION to establish the Scope, Item and Serial that
 * later segments need in order to have anywhere to land.
 */
export function addITwin(twin: AddITwin): Promise<AddITwinResult> {
  return request<AddITwinResult>('/admin/eng/itwins/add', {
    method: 'POST',
    body: JSON.stringify(twin),
  })
}

/**
 * Publish the design: promote a Named Version.
 *
 * This is the release event. The ENG repository is an iModel that segments
 * enrich incrementally; publication gathers every segment in the twin that is
 * not already Published into one Named Version, puts it through a validation
 * gate, and only a passing gate marks them Published and queues the outbox row
 * carrying a SyncSegments BOD to REG-LOCATION.
 *
 * There is no subset: a Named Version is the design as it stands, so one
 * failing segment holds back the batch.
 *
 * A refusal is returned rather than thrown. The API answers 422 with the same
 * result shape, and being told which segments are unclassified is an ordinary
 * outcome of trying to publish -- not an error in the sense that a dropped
 * connection is.
 */
export async function promote(iTwinId: string, name: string): Promise<PromotionResult> {
  try {
    return await request<PromotionResult>('/admin/eng/promote', {
      method: 'POST',
      body: JSON.stringify({ name, iTwinId }),
    })
  } catch (err) {
    if (err instanceof SandboxError && err.status === 422 && isPromotionResult(err.body)) {
      return err.body
    }

    throw err
  }
}

function isPromotionResult(body: unknown): body is PromotionResult {
  return (
    typeof body === 'object' &&
    body !== null &&
    'released' in body &&
    'findings' in body
  )
}

// ─── Reference data ──────────────────────────────────────────────────────────

/**
 * A class an ENG element can be created on.
 *
 * ENG's EC metadata, flattened. There is no taxonomy chain and no aspect
 * concept here: ENG models inheritance between classes but the catalog does not
 * walk it, and ClassModifier controls instantiability rather than whether a
 * class applies alongside another. The fields that used to describe those
 * things came from the Sandbox's own reference-data library, which has been
 * removed -- each participant now provisions its own classes in its own store.
 */
export interface ClassDefinition {
  /**
   * The fully qualified EC class name, e.g. 'ENG.Streetlight'.
   *
   * This is the key because it is what round trips: an element read back
   * reports FullyQualifiedECClassName, and a picker whose value did not match
   * it would show a stored class as absent from reference data.
   */
  key: string
  name: string
  /** The id the write path needs, so the name need not be resolved twice. */
  ecClassId: number
}

/**
 * The classes the deployed ENG app can store an element against.
 *
 * ENG's EC metadata (ENG.*), whose keys are the only ones a write to the
 * deployed app will resolve. The Sandbox used to serve a second, parallel
 * catalog from its own reference data (rdl:*), but that library had no writer
 * once BOD handling moved into the engines and has been removed.
 */
export function listEngElementClasses(
  signal?: AbortSignal,
): Promise<ClassDefinition[]> {
  return request<ClassDefinition[]>('/admin/eng/element-class-catalog', { signal })
}

// ─── REG-LOCATION ────────────────────────────────────────────────────────────

/** Where a proposal stands with the steward. */
export type StewardshipState = 'Proposed' | 'Approved' | 'Rejected'

/**
 * A segment proposed to the registry, awaiting a stewardship decision.
 *
 * REG-LOCATION is a governance gate, not a relay: arrival is not acceptance.
 * These rows are what ENG published, held until a steward admits them to the
 * authoritative model.
 */
export interface StewardshipItem {
  id: number
  /** Who proposed it, e.g. ENG. */
  sourceParticipant: string
  /** The identity the sender asserted, carried through unchanged. */
  sourceIdentifier: string
  proposedName: string | null
  /** The class the sender named. */
  requestedClassKey: string | null
  /** What the registry could actually bind. Null when it bound nothing. */
  boundClassKey: string | null
  /** True when bound to an ancestor rather than the class the sender named. */
  classDegraded: boolean
  propertiesMapped: number
  propertiesUnmapped: number
  state: StewardshipState
  createdAt: string
  /** The iTwin the sender asserted, e.g. ENG:02c9fdd8-... */
  assertedContext: string | null
  /** The asserted iTwin on its own, for scoping the queue to one twin. */
  contextIdInSource: string | null
}

export interface ApprovalResult {
  approved: number
  rejected: number
  locationCodes: string[]
  correlationId: string | null
}

/**
 * Which stewardship rows to fetch.
 *
 * 'all' is the registry's own wording for no state filter, not a client-side
 * convenience: the server has to be told to include decided rows, because the
 * default is the working queue.
 */
export type StewardshipFilter = 'Proposed' | 'Approved' | 'all'

/**
 * Proposals awaiting a stewardship decision, scoped to one iTwin.
 *
 * Omitting the twin returns every context. The UI always passes one: the queue is
 * shown beside a twin selector, and an unfiltered queue would list another twin's
 * proposals under the twin on screen.
 *
 * state defaults server-side to Proposed. Ask for 'all' to see what was approved
 * beside what is still outstanding.
 */
export function listStewardship(
  iTwinId?: string,
  state?: StewardshipFilter,
  signal?: AbortSignal,
): Promise<StewardshipItem[]> {
  const params = new URLSearchParams()
  if (iTwinId) params.set('twin', iTwinId)
  if (state) params.set('state', state)

  const query = params.size > 0 ? `?${params}` : ''
  return request<StewardshipItem[]>(`/admin/reg-location/stewardship${query}`, { signal })
}

/**
 * Approve proposals, either a chosen subset or the whole queue.
 *
 * The registry's release event: admits proposals to the authoritative model,
 * assigns LOC- codes, and republishes to the O&M channel. Passing no ids
 * approves everything, which is what the batch scenarios rely on; a steward
 * working through the queue names the ones they have satisfied themselves about.
 */
export function approveStewardship(proposalIds?: number[]): Promise<ApprovalResult> {
  return request<ApprovalResult>('/admin/reg-location/approve', {
    method: 'POST',
    body: JSON.stringify({ proposalIds: proposalIds ?? null }),
  })
}

// ─── Day zero ────────────────────────────────────────────────────────────────

/**
 * What a day-zero reset did. Only the counts the operator can act on are
 * modelled; the endpoint returns richer per-participant detail that the UI has
 * no use for.
 */
export interface DayZeroResult {
  sessionsClosed: number
  channels: unknown[]
  participants: unknown[]
  /**
   * Things the reset could not do for itself, in prose. Chiefly that the CIR
   * provider must be told to re-open its sessions, and that the CIR registry
   * keeps its CIRIDs. Surfaced verbatim rather than interpreted: they are
   * warnings about state outside this deployment's reach.
   */
  actionRequired: string[]
}

/**
 * Tear the sandbox back to day zero: close sessions, rebuild every ISBM
 * channel, drop and reseed each participant's schema.
 *
 * Slow by nature — it deletes and recreates broker channels — so callers should
 * expect this to run for a while rather than treating a delay as a hang.
 */
export function resetDayZero(): Promise<DayZeroResult> {
  return request<DayZeroResult>('/admin/reset/day-zero', { method: 'POST' })
}

