/**
 * Bentley IMS (OAuth2 / OIDC) authentication.
 *
 * Authorization Code flow with PKCE. The app is a public client -- it ships to a
 * browser, so it can hold no secret -- which is exactly the case PKCE exists for:
 * the code alone is useless to an interceptor without the verifier, which never
 * leaves this origin.
 *
 * The whole flow lives in this one module so that App.tsx consumes a small
 * surface (currentUser / login / logout / initialize) and nothing else in the
 * app needs to know about tokens, claims or redirects.
 *
 * Configuration comes from Vite env vars rather than constants: the client id and
 * redirect URI differ between local dev and the deployed site, and both must match
 * the IMS registration exactly. See .env.example.
 */

// ─── Configuration ──────────────────────────────────────────────────────────

// Production IMS. The OIDC endpoints below are relative to this.
const AUTHORITY = (import.meta.env.VITE_IMS_AUTHORITY ?? 'https://ims.bentley.com').replace(/\/$/, '')

const CLIENT_ID = import.meta.env.VITE_IMS_CLIENT_ID ?? 'spa-cebaZUchzJ1VMRpl9f1bzz2Gl'

// Must match a redirect URI registered on the IMS client, character for
// character. The registration uses a dedicated /signin-oidc path rather than the
// app root, so the default is built from the origin plus that path.
const REDIRECT_URI = import.meta.env.VITE_IMS_REDIRECT_URI ?? `${window.location.origin}/signin-oidc`

// Where IMS returns the browser after sign-out; registered separately from the
// sign-in redirect. Landing here is intentional: the app has no offline
// presence, so the gate immediately sends the visitor back to Bentley sign-in.
const POST_LOGOUT_REDIRECT_URI = import.meta.env.VITE_IMS_POST_LOGOUT_REDIRECT_URI ?? `${window.location.origin}/signout-oidc`

// Only what the client registration actually grants. IMS rejects the entire
// authorize request with invalid_scope if any single scope is not granted, so
// requesting openid/profile/email speculatively fails the whole sign-in rather
// than degrading.
//
// The consequence is that there is no id_token, so identity comes from the
// userinfo endpoint instead -- see completeLogin. If the registration is later
// granted the OIDC scopes, adding them here restores the cheaper id_token path.
//
// offline_access is deliberately absent: a refresh token in browser storage is a
// long-lived credential and this is a demo sandbox.
const SCOPES = import.meta.env.VITE_IMS_SCOPES ?? 'itwin-platform'

const ENDPOINTS = {
  authorize: `${AUTHORITY}/connect/authorize`,
  token: `${AUTHORITY}/connect/token`,
  endSession: `${AUTHORITY}/connect/endsession`,
  userInfo: `${AUTHORITY}/connect/userinfo`,
}

/** True when a client id has been configured. */
export function isConfigured(): boolean {
  return CLIENT_ID.trim().length > 0
}

// ─── Types ──────────────────────────────────────────────────────────────────

export interface CurrentUser {
  sub: string
  name: string
  email: string
  organization: string
  roles: string[]
}

interface StoredSession {
  accessToken: string
  /** Absent unless the registration grants the openid scope. */
  idToken?: string
  /** Epoch milliseconds. Absolute, so a clock-independent comparison is possible. */
  expiresAt: number
  user: CurrentUser
}

// ─── Storage ────────────────────────────────────────────────────────────────
//
// sessionStorage, not localStorage: the token dies with the tab, which limits the
// window in which a stolen token is useful. Note this is still readable by any
// script on the origin -- acceptable for a sandbox demo, not for production,
// where a backend-for-frontend holding the token in an HttpOnly cookie is the
// right answer.

const SESSION_KEY = 'oiie.ims.session'
const VERIFIER_KEY = 'oiie.ims.pkce_verifier'
const STATE_KEY = 'oiie.ims.state'
const NONCE_KEY = 'oiie.ims.nonce'
// Marks that a redirect to IMS has been started but has not yet produced a
// session. Without it a failed sign-in is indistinguishable from a first visit,
// and the gate redirects again forever -- the failure reason never gets on
// screen because the URL carrying it has already been scrubbed.
const ATTEMPT_KEY = 'oiie.ims.attempt'
// The IMS end-session URL, parked by logout() for the signed-out screen to call.
const END_SESSION_KEY = 'oiie.ims.end_session'

function readSession(): StoredSession | null {
  try {
    const raw = sessionStorage.getItem(SESSION_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as StoredSession
    // A token that expires in the next half minute is treated as already gone,
    // so a call cannot be started with a token that dies mid-flight.
    if (!parsed.expiresAt || parsed.expiresAt - Date.now() < 30_000) {
      sessionStorage.removeItem(SESSION_KEY)
      return null
    }
    return parsed
  } catch {
    sessionStorage.removeItem(SESSION_KEY)
    return null
  }
}

function writeSession(s: StoredSession): void {
  sessionStorage.setItem(SESSION_KEY, JSON.stringify(s))
}

// ─── PKCE helpers ───────────────────────────────────────────────────────────

/** Base64url per RFC 7636: standard base64 with +/ swapped and padding dropped. */
function base64Url(bytes: Uint8Array): string {
  let bin = ''
  bytes.forEach(b => { bin += String.fromCharCode(b) })
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

function randomString(byteLength = 32): string {
  const bytes = new Uint8Array(byteLength)
  crypto.getRandomValues(bytes)
  return base64Url(bytes)
}

/** S256 challenge. Plain is not offered: it provides no protection. */
async function challengeFor(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))
  return base64Url(new Uint8Array(digest))
}

// ─── Claim decoding ─────────────────────────────────────────────────────────

/**
 * Reads the id_token payload.
 *
 * Deliberately NOT a security check. The token arrived over TLS directly from the
 * IMS token endpoint in response to a request carrying our verifier, so it is
 * trusted by transport, not by inspection. Any API that receives this token
 * validates the signature itself; doing it here would be theatre.
 */
function decodeJwtPayload(jwt: string): Record<string, unknown> {
  const parts = jwt.split('.')
  if (parts.length < 2) throw new Error('Malformed id_token.')
  const payload = parts[1]!.replace(/-/g, '+').replace(/_/g, '/')
  const padded = payload.padEnd(payload.length + (4 - (payload.length % 4)) % 4, '=')
  return JSON.parse(decodeURIComponent(escape(atob(padded)))) as Record<string, unknown>
}

function str(claims: Record<string, unknown>, ...keys: string[]): string {
  for (const k of keys) {
    const v = claims[k]
    if (typeof v === 'string' && v.length > 0) return v
  }
  return ''
}

/** Shown when no claim carries a usable display name. */
const FALLBACK_NAME = 'Signed in'

/**
 * A display name, ignoring values that are really an email address.
 *
 * Bentley commonly sets the `name` claim to the user's email, which would make
 * the avatar header and the line beneath it show the same string. Rejecting
 * anything containing "@" lets the caller fall through to the given/family pair,
 * which carries the actual person's name.
 */
function displayName(claims: Record<string, unknown>, ...keys: string[]): string {
  const value = str(claims, ...keys)
  return value.includes('@') ? '' : value
}

/**
 * Maps IMS claims onto CurrentUser.
 *
 * IMS is not uniform about claim names -- they differ between the id_token, the
 * itwin-platform access token and the userinfo response -- so each field tries
 * the plausible spellings in preference order.
 *
 * The composed given/family pair is preferred over `name` because Bentley
 * populates `name` with the email address. Email remains the last resort so the
 * header shows something recognisable rather than "undefined" during a demo.
 */
function userFromClaims(claims: Record<string, unknown>): CurrentUser {
  const email = str(claims, 'email', 'preferred_username', 'upn', 'unique_name')
  const rolesRaw = claims['role'] ?? claims['roles']
  const roles = Array.isArray(rolesRaw)
    ? rolesRaw.filter((r): r is string => typeof r === 'string')
    : typeof rolesRaw === 'string' ? [rolesRaw] : []

  const composed = [str(claims, 'given_name', 'first_name'), str(claims, 'family_name', 'last_name')]
    .filter(Boolean)
    .join(' ')

  return {
    sub: str(claims, 'sub', 'user_id', 'userid'),
    name: composed || displayName(claims, 'name', 'display_name') || email || FALLBACK_NAME,
    email,
    organization: str(claims, 'org_name', 'organization', 'org', 'ultimate_site') || 'Bentley Systems',
    roles,
  }
}

// ─── Login ──────────────────────────────────────────────────────────────────

// Set once a redirect to IMS has been committed. React StrictMode invokes
// effects twice in development, and a second login() would mint a fresh
// state/verifier over the pair the outgoing authorize URL was built from --
// the callback would then fail the state check every time. Module scope is the
// right lifetime: it lasts until the document is replaced by the navigation.
let loginStarted = false

/**
 * Sends the browser to the Bentley sign-in page.
 *
 * Returns a promise that never resolves in practice, because navigation replaces
 * the document. Callers should treat it as a terminal action.
 */
export async function login(): Promise<void> {
  if (!isConfigured()) throw new Error('VITE_IMS_CLIENT_ID is not set.')
  if (loginStarted) return
  loginStarted = true

  const verifier = randomString()
  const state = randomString(16)

  sessionStorage.setItem(VERIFIER_KEY, verifier)
  sessionStorage.setItem(STATE_KEY, state)
  sessionStorage.setItem(ATTEMPT_KEY, '1')

  const params = new URLSearchParams({
    client_id: CLIENT_ID,
    redirect_uri: REDIRECT_URI,
    response_type: 'code',
    scope: SCOPES,
    state,
    code_challenge: await challengeFor(verifier),
    code_challenge_method: 'S256',
  })

  // nonce is an OIDC parameter bound to the id_token. Sending it on a plain
  // OAuth2 request buys nothing, and some servers object to parameters that
  // cannot apply, so it is only included when openid is actually requested.
  if (SCOPES.split(/\s+/).includes('openid')) {
    const nonce = randomString(16)
    sessionStorage.setItem(NONCE_KEY, nonce)
    params.set('nonce', nonce)
  }

  window.location.assign(`${ENDPOINTS.authorize}?${params.toString()}`)
}

// ─── Callback ───────────────────────────────────────────────────────────────

/** True when the current URL looks like an IMS redirect back to us. */
function hasAuthResponse(): boolean {
  const q = new URLSearchParams(window.location.search)
  return q.has('code') || q.has('error')
}

/**
 * Removes OAuth params so a refresh cannot replay an already-consumed code, and
 * returns to the app root.
 *
 * The root matters: IMS sends the browser to /signin-oidc (and /signout-oidc),
 * which are callback URLs rather than app routes. Leaving the address bar on
 * them would mean a reload lands on a path the app does not render.
 */
function scrubUrl(): void {
  window.history.replaceState({}, document.title, '/')
}

/**
 * Resolves identity when no id_token was issued.
 *
 * Tries the access token's own claims first. Bentley issues the itwin-platform
 * access token as a JWT carrying name/email, so this needs no network call and
 * works without the openid scope. Reading it is safe for display purposes only:
 * it is not a security decision, and the token came over TLS straight from the
 * token endpoint.
 *
 * Falls back to the userinfo endpoint for the case where the access token is
 * opaque. Note userinfo is an OIDC endpoint and generally requires the openid
 * scope, so it is expected to fail for this registration.
 *
 * A total failure is not fatal to the session: the access token still works for
 * API calls, so the app signs in with a placeholder rather than refusing entry
 * over a cosmetic detail.
 */
async function resolveIdentity(accessToken: string): Promise<CurrentUser> {
  try {
    const user = userFromClaims(decodeJwtPayload(accessToken))
    if (user.name !== FALLBACK_NAME) return user
  } catch {
    // Opaque (non-JWT) access token. Fall through to userinfo.
  }

  try {
    const res = await fetch(ENDPOINTS.userInfo, {
      headers: { Authorization: `Bearer ${accessToken}` },
    })
    if (!res.ok) throw new Error(`userinfo returned ${res.status}`)
    return userFromClaims(await res.json() as Record<string, unknown>)
  } catch {
    return { sub: '', name: FALLBACK_NAME, email: '', organization: 'Bentley Systems', roles: [] }
  }
}

async function completeLogin(): Promise<StoredSession> {
  const q = new URLSearchParams(window.location.search)

  const error = q.get('error')
  if (error) {
    const description = q.get('error_description')
    scrubUrl()
    throw new Error(description ? `${error}: ${description}` : error)
  }

  const code = q.get('code')
  const returnedState = q.get('state')
  const expectedState = sessionStorage.getItem(STATE_KEY)
  const verifier = sessionStorage.getItem(VERIFIER_KEY)
  const expectedNonce = sessionStorage.getItem(NONCE_KEY)

  sessionStorage.removeItem(STATE_KEY)
  sessionStorage.removeItem(VERIFIER_KEY)
  sessionStorage.removeItem(NONCE_KEY)

  if (!code) throw new Error('No authorization code in the redirect.')

  // state mismatch means this response was not initiated by this tab. Refusing
  // it is the CSRF defence, so it is a hard failure rather than a retry.
  if (!expectedState || returnedState !== expectedState) {
    scrubUrl()
    throw new Error('Authorization state mismatch. The sign-in was not started by this browser tab.')
  }
  if (!verifier) {
    scrubUrl()
    throw new Error('Missing PKCE verifier. The sign-in could not be completed.')
  }

  const body = new URLSearchParams({
    grant_type: 'authorization_code',
    client_id: CLIENT_ID,
    redirect_uri: REDIRECT_URI,
    code,
    code_verifier: verifier,
  })

  const res = await fetch(ENDPOINTS.token, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body,
  })

  if (!res.ok) {
    const text = await res.text().catch(() => '')
    scrubUrl()
    throw new Error(`Token exchange failed (${res.status}). ${text}`.trim())
  }

  const token = await res.json() as {
    access_token: string
    id_token?: string
    expires_in?: number
  }

  // With only itwin-platform granted there is no id_token, so identity comes
  // from the access token's own claims instead. When the OIDC scopes are granted
  // the id_token is preferred: it carries the nonce that proves this response
  // answers our request.
  let user: CurrentUser
  if (token.id_token) {
    const claims = decodeJwtPayload(token.id_token)
    if (expectedNonce && typeof claims['nonce'] === 'string' && claims['nonce'] !== expectedNonce) {
      scrubUrl()
      throw new Error('Nonce mismatch. The token does not correspond to this sign-in request.')
    }
    user = userFromClaims(claims)
  } else {
    user = await resolveIdentity(token.access_token)
  }

  const session: StoredSession = {
    accessToken: token.access_token,
    idToken: token.id_token,
    expiresAt: Date.now() + (token.expires_in ?? 3600) * 1000,
    user,
  }

  writeSession(session)
  scrubUrl()
  return session
}

// ─── Public surface ─────────────────────────────────────────────────────────

export type AuthState =
  | { status: 'loading' }
  | { status: 'authenticated'; user: CurrentUser }
  | { status: 'unauthenticated' }
  | { status: 'error'; message: string }

let initialization: Promise<AuthState> | null = null

/**
 * Resolves auth on startup: finishes a redirect if we are mid-flow, otherwise
 * restores an existing session.
 *
 * Does not itself redirect to IMS. The caller decides that, so that a
 * configuration error can be shown instead of an infinite redirect loop.
 *
 * The result is memoised because an authorization code may be exchanged exactly
 * once. React StrictMode runs effects twice in development, and without this the
 * second run would find the code already consumed and the PKCE verifier already
 * cleared, reporting a spurious failure over a sign-in that actually succeeded.
 */
export function initialize(): Promise<AuthState> {
  initialization ??= resolveAuthState()
  return initialization
}

async function resolveAuthState(): Promise<AuthState> {
  if (!isConfigured()) {
    return { status: 'error', message: 'VITE_IMS_CLIENT_ID is not set. Bentley IMS sign-in cannot start.' }
  }

  if (hasAuthResponse()) {
    try {
      const session = await completeLogin()
      sessionStorage.removeItem(ATTEMPT_KEY)
      return { status: 'authenticated', user: session.user }
    } catch (err) {
      sessionStorage.removeItem(ATTEMPT_KEY)
      return { status: 'error', message: err instanceof Error ? err.message : String(err) }
    }
  }

  const existing = readSession()
  if (existing) {
    sessionStorage.removeItem(ATTEMPT_KEY)
    return { status: 'authenticated', user: existing.user }
  }

  // Back at the app with no session and no authorization response, yet a sign-in
  // was already attempted. IMS rejected the request without returning an error we
  // could read, so redirecting again would just repeat it. Stop and say so.
  if (sessionStorage.getItem(ATTEMPT_KEY)) {
    sessionStorage.removeItem(ATTEMPT_KEY)
    return {
      status: 'error',
      message: 'Bentley IMS ended the sign-in without returning an authorization code. '
        + 'This usually means the requested scopes or the redirect URI do not match the client registration.',
    }
  }

  return { status: 'unauthenticated' }
}

/**
 * The bearer token for Bentley REST / iModel calls.
 *
 * Null when signed out or expired; callers must handle that rather than sending
 * "Bearer null".
 */
export function accessToken(): string | null {
  return readSession()?.accessToken ?? null
}

/** Path of the app's own signed-out screen. */
export const SIGNED_OUT_PATH = '/signout-oidc'

/**
 * The IMS end-session URL.
 *
 * post_logout_redirect_uri is deliberately omitted: IMS only honours it when it
 * can tie the return to a session via id_token_hint, and this client is granted
 * only itwin-platform, so no id_token is ever issued. Sending it regardless makes
 * IMS abandon the request with "the page expired early", which then poisons the
 * next sign-in.
 */
function endSessionUrl(idToken?: string): string {
  const params = new URLSearchParams({ client_id: CLIENT_ID })
  if (idToken) {
    params.set('id_token_hint', idToken)
    params.set('post_logout_redirect_uri', POST_LOGOUT_REDIRECT_URI)
  }
  return `${ENDPOINTS.endSession}?${params.toString()}`
}

/**
 * Ends the session.
 *
 * Local state is cleared first so that even if the IMS call fails the app is not
 * left holding a token it considers valid.
 *
 * Rather than navigating to IMS -- which would strand the user on Bentley's own
 * signed-off page with no way back -- the browser goes to the app's own
 * signed-out screen, which ends the IMS session from there and offers a link
 * back in. See SignedOutScreen in App.tsx.
 */
export function logout(): void {
  const session = readSession()
  sessionStorage.removeItem(SESSION_KEY)
  sessionStorage.removeItem(VERIFIER_KEY)
  sessionStorage.removeItem(STATE_KEY)
  sessionStorage.removeItem(NONCE_KEY)
  sessionStorage.removeItem(ATTEMPT_KEY)

  // Handed to the signed-out screen through storage because the navigation
  // below discards everything else in memory.
  sessionStorage.setItem(END_SESSION_KEY, endSessionUrl(session?.idToken))

  window.location.assign(SIGNED_OUT_PATH)
}

/**
 * The IMS end-session URL parked by logout(), consumed once.
 *
 * Removed on read so that a reload of the signed-out screen does not repeat the
 * end-session call.
 */
export function takeEndSessionUrl(): string | null {
  const url = sessionStorage.getItem(END_SESSION_KEY)
  if (url) sessionStorage.removeItem(END_SESSION_KEY)
  return url
}
