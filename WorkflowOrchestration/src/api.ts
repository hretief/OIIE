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

// Deployed sandboxes set Sandbox:AdminKey, and /admin/* is rejected without it.
// Empty locally, where the endpoints are open.
const ADMIN_KEY = import.meta.env.VITE_SANDBOX_ADMIN_KEY ?? ''

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
}

export interface CreatedTag {
  id: number
  tagNumber: string
  federationId: string | null
  iTwinId: string
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
  const headers = new Headers(init?.headers)
  headers.set('Accept', 'application/json')

  if (init?.body) {
    headers.set('Content-Type', 'application/json')
  }

  if (ADMIN_KEY) {
    headers.set('x-sandbox-admin-key', ADMIN_KEY)
  }

  let response: Response

  try {
    response = await fetch(`${BASE}${path}`, { ...init, headers })
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

  if (!response.ok) {
    const failure = await describeFailure(response)
    throw new SandboxError(failure.message, response.status, failure.body)
  }

  return (await response.json()) as T
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
 * A reference-data class a participant can bind.
 *
 * Each repository holds its own model, mapped to CCOM as the common one, so this
 * differs per participant: ENG holds the full library including leaf classes,
 * REG-LOCATION deliberately holds less. Choosing from what a participant
 * actually holds is what stops a segment arriving at the registry unbound.
 */
export interface ClassDefinition {
  key: string
  name: string
  kind: 'Taxonomy' | 'Aspect'
  appliesTo: string
  /** Root first, e.g. ['rdl:Equipment', 'rdl:Instrument']. */
  chain: string[]
  /** Aspects apply alongside the taxonomy rather than instead of it. */
  isAspect: boolean
}

/** The classes a participant can bind. */
export function listClasses(
  participantId: string,
  signal?: AbortSignal,
): Promise<ClassDefinition[]> {
  return request<ClassDefinition[]>(
    `/admin/${encodeURIComponent(participantId)}/class-catalog`,
    { signal },
  )
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
}

/**
 * A location in the registry's authoritative model.
 *
 * The registry deliberately does not adopt the source's identifier: an ENG
 * segment becomes LOC-000412 here, which is precisely the identity problem the
 * CIR exists to solve.
 */
export interface RegLocation {
  id: number
  /** The registry's own code, e.g. LOC-000412. */
  locationCode: string
  name: string | null
  description: string | null
  /** As bound locally, which may be an ancestor of what the sender sent. */
  classKey: string | null
  /** Set when the sender classified more specifically than the registry understands. */
  requestedClassKey: string | null
  area: string | null
  /** Where it came from, retained so provenance survives the message archive. */
  sourceParticipant: string
  sourceIdentifier: string
  createdAt: string
  updatedAt: string
}

export interface ApprovalResult {
  approved: number
  rejected: number
  locationCodes: string[]
  correlationId: string | null
}

/** Proposals awaiting a stewardship decision. */
export function listStewardship(signal?: AbortSignal): Promise<StewardshipItem[]> {
  return request<StewardshipItem[]>('/admin/reg-location/stewardship', { signal })
}

/** The registry's authoritative locations. */
export function listLocations(signal?: AbortSignal): Promise<RegLocation[]> {
  return request<RegLocation[]>('/admin/reg-location/locations', { signal })
}

/**
 * Approve every proposal in the queue.
 *
 * The registry's release event: admits proposals to the authoritative model,
 * assigns LOC- codes, and republishes to the O&M channel. Like promotion this
 * takes no subset -- the endpoint approves the whole queue.
 */
export function approveStewardship(): Promise<ApprovalResult> {
  return request<ApprovalResult>('/admin/reg-location/approve', { method: 'POST' })
}

/**
 * One MMS light system, as LIGHT_SYSTEM_INVENTORY holds it.
 *
 * Each coded column carries both its raw id and the name resolved from MMS's
 * own reference tables. The pair matters: a null name next to a non-null id is
 * a dangling reference, which reads very differently from a null id.
 */
export interface MmsLocation {
  lightSystemId: number
  lightSystemName: string
  classCodeId: number
  classCode: string | null
  statusId: number | null
  status: string | null
  ownerId: number | null
  owner: string
}

/**
 * What MMS holds, scoped to one iTwin.
 *
 * The twin is resolved to an OWNER_ID through ws-CIR server-side rather than
 * matched against a column, because LIGHT_SYSTEM_INVENTORY has no iTwin column
 * and cannot be given one.
 *
 * resolved=false is not an error: it means the registry knows no MMS owner for
 * that twin. The reason explains which, and rows is then empty rather than
 * unfiltered -- returning everything would show one district's inventory to
 * another.
 */
export interface MmsInventory {
  twin: string | null
  resolved: boolean
  reason: string | null
  ownerId: number | null
  ownerName: string | null
  locations: MmsLocation[]
}

/** MMS's own inventory for a twin. Omitting the twin returns every row. */
export function listMmsLocations(
  iTwinId?: string,
  signal?: AbortSignal,
): Promise<MmsInventory> {
  const query = iTwinId ? `?twin=${encodeURIComponent(iTwinId)}` : ''
  return request<MmsInventory>(`/admin/mms/locations${query}`, { signal })
}
