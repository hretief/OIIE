/**
 * Client for the Bentley iTwin Platform (api.bentley.com).
 *
 * Kept separate from api.ts on purpose: that module talks to our own sandbox
 * with an admin key, this one talks to Bentley with the signed-in user's IMS
 * bearer token. The two have different hosts, different auth and different
 * failure modes, so mixing them would make both harder to reason about.
 */

import * as auth from './auth'

const BASE = (import.meta.env.VITE_ITWIN_API ?? 'https://api.bentley.com').replace(/\/$/, '')

/**
 * An iTwin as the platform returns it.
 *
 * Only the fields the context banner needs are modelled. The platform returns
 * a good deal more (dataCenterLocation, parentId, status, ...) and pinning all
 * of it here would mean churn every time the service adds a property.
 */
export interface ITwinSummary {
  id: string
  /** Friendly name, e.g. "MnDOT District 3". Optional server-side. */
  displayName: string | null
  /** Short code, e.g. "MNDOT-01". Optional server-side. */
  number: string | null
  class: string | null
  subClass: string | null
}

interface FavoritesResponse {
  iTwins?: ITwinSummary[]
}

/**
 * The platform pages at 100 by default and caps a page at 1000. Asking for the
 * maximum keeps the common case to a single request without assuming the whole
 * tenant fits in one.
 */
const PAGE_SIZE = 1000

/**
 * A hard ceiling on paging, so a mistake here cannot spin against the platform
 * indefinitely. At this page size it allows far more twins than the sandbox
 * will ever hold.
 */
const MAX_PAGES = 20

/**
 * The platform versions its representations through Accept rather than the URL.
 * v1 is what the favorites endpoint documents today; without it the service
 * answers with a older shape that omits fields we read.
 */
const ACCEPT = 'application/vnd.bentley.itwin-platform.v1+json'

async function request<T>(path: string, signal?: AbortSignal): Promise<T> {
  const token = auth.accessToken()
  if (!token) throw new Error('Not signed in to Bentley IMS')

  const res = await fetch(`${BASE}${path}`, {
    signal,
    headers: {
      Accept: ACCEPT,
      // The platform expects the raw token prefixed with Bearer; auth.ts stores
      // it without the scheme.
      Authorization: `Bearer ${token}`,
    },
  })

  if (!res.ok) {
    // 401 here almost always means the token expired rather than that the app
    // is misconfigured, so it is worth saying which of the two it is.
    if (res.status === 401) throw new Error('Bentley session expired — sign in again')
    if (res.status === 403) throw new Error('Bentley token lacks itwin-platform access')
    throw new Error(`iTwin API ${res.status}: ${(await res.text()).slice(0, 200)}`)
  }

  return await res.json() as T
}

/**
 * The iTwins the signed-in user has marked as favorite.
 *
 * Favorites are per-user server-side state, so this needs no filtering here --
 * the endpoint only ever returns the caller's own.
 */
export async function listFavorites(signal?: AbortSignal): Promise<ITwinSummary[]> {
  const body = await request<FavoritesResponse>('/itwins/favorites', signal)
  return body.iTwins ?? []
}

/**
 * Every iTwin the signed-in user can see, of one subClass.
 *
 * Favorites are per-user curation, so a twin the sandbox depends on is only
 * there if someone happened to star it -- which makes the banner depend on
 * personal state rather than on what exists. This asks the platform for the
 * twins themselves instead.
 *
 * subClass is filtered server-side rather than after the fact: it is what the
 * platform indexes on, and filtering here would mean paging through every
 * project and account twin in the tenant to discard them.
 */
export async function listITwins(
  subClass = 'Asset',
  signal?: AbortSignal,
): Promise<ITwinSummary[]> {
  const collected: ITwinSummary[] = []

  for (let page = 0; page < MAX_PAGES; page++) {
    const query = new URLSearchParams({
      subClass,
      $top: String(PAGE_SIZE),
      $skip: String(page * PAGE_SIZE),
    })

    const body = await request<FavoritesResponse>(`/itwins?${query}`, signal)
    const found = body.iTwins ?? []
    collected.push(...found)

    // A short page is the last page. The platform returns no total, so this is
    // the only signal that the collection is exhausted.
    if (found.length < PAGE_SIZE) break
  }

  return collected
}
