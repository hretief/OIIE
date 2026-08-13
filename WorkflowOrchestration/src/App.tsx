import { useCallback, useEffect, useRef, useState } from 'react'
import * as api from './api'
import * as auth from './auth'
import type { CurrentUser } from './auth'

// ─── Types ────────────────────────────────────────────────────────────────────

interface ITwin {
  uuid: string
  shortName: string
  fullName: string
}

// The signed-in identity now comes from Bentley IMS; see auth.ts. CurrentUser is
// imported rather than declared here so the token is the single source of truth.

// Two initials read better than one in a small circle. IMS display names are
// not guaranteed to have a surname, hence the filter rather than a split that
// assumes two parts.
//
// An email is handled separately: it is the fallback when IMS supplies no name
// claim, and splitting it on whitespace would yield a single letter. The local
// part is split on its usual separators instead, so "hennie.retief@..." gives HR
// rather than H.
function initialsOf(name: string): string {
  const source = name.includes('@')
    ? name.split('@')[0]!.split(/[._-]+/)
    : name.split(/\s+/)
  return source.filter(Boolean).slice(0, 2).map(p => p[0]!.toUpperCase()).join('')
}



// ─── iTwin registry ───────────────────────────────────────────────────────────

/**
 * Twins come from the sandbox (GET /admin/eng/twins), not from a list here.
 *
 * The API calls them { id, code, name }; these screens were written against
 * { uuid, shortName, fullName } and SC11/SC04 still match on shortName, so the
 * shape is mapped once here rather than renamed throughout.
 */
function toITwin(twin: api.ITwin): ITwin {
  return {
    uuid: twin.id,
    // Code is optional server-side. Falling back to a short id keeps the twin
    // selectable rather than rendering a blank button.
    shortName: twin.code || twin.id.slice(0, 8),
    fullName: twin.name || twin.description || twin.code || twin.id,
  }
}

type WorkflowId = 'SC01' | 'SC02' | 'SC11' | 'SC04' | 'SC05'
type PersonaId = 'ENG' | 'REG' | 'MMS' | 'RELIABILITY' | 'CONSTRUCT' | 'REG_ASSET' | 'GIS'

// SC01 — Segment lifecycle
type SegmentStatus = 'draft' | 'published' | 'validated' | 'approved' | 'stored_mms' | 'stored_rel'

interface Segment {
  uuid: string
  shortName: string
  fullName: string
  segmentType: string
  registrationSite: string
  created: string
  status: SegmentStatus
  storedMms: boolean
  storedRel: boolean
  storedGis: boolean
}

// SC04 — As-Built asset lifecycle
type AsBuiltStatus = 'draft' | 'eng_published' | 'construct_published' | 'registered' | 'om_published'

interface AsBuiltAsset {
  uuid: string
  equipmentTag: string
  description: string
  equipmentClass: string
  manufacturer: string
  modelNumber: string
  serialNumber: string
  installationSite: string
  installDate: string
  created: string
  status: AsBuiltStatus
  registeredOm: boolean
  // SC05 — which O&M systems have taken the registered asset. Separate flags
  // rather than one "published" bit, for the same reason segments carry three:
  // the demo has to show each system taking it up independently.
  omMms: boolean
  omRel: boolean
  omGis: boolean
}

// SC11 — Asset update lifecycle
type UpdateStatus = 'pending' | 'published' | 'rel_updated' | 'reg_updated'

interface AssetUpdate {
  id: string
  segmentUuid: string
  segmentShortName: string
  iTwinShortName: string
  assetType: string
  serialNumber: string
  installedBy: string
  installedAt: string
  status: UpdateStatus
  relUpdated: boolean
  regUpdated: boolean
}

// ─── Personas ────────────────────────────────────────────────────────────

// Each persona carries two names.
//
// `label` / `fullLabel` are the OIIE role names. They match the participant ids
// the sandbox uses on the wire, so they stay exactly as they are — renaming them
// would decouple what is on screen from what is in the logs and the message
// archive, which is the one thing that makes a round trip explainable.
//
// `alias` / `aliasFull` are the client's own application names, and are what the
// UI shows. The demo is more persuasive when a reviewer sees the systems they
// actually operate rather than a vocabulary they would have to learn first.
//
// Hard-coded for now, pending the admin page that makes this configurable. Held
// on the persona record rather than in a separate lookup so a new persona cannot
// be added with a name but no alias.
const PERSONAS: {
  id: PersonaId
  label: string
  fullLabel: string
  alias: string
  aliasFull: string
  accent: string
  dimBg: string
  glowBg: string
  borderColor: string
}[] = [
  { id: 'ENG', label: 'ENG', fullLabel: 'Engineering Design', alias: 'BIC', aliasFull: 'Infrastructure Cloud', accent: '#3b82f6', dimBg: 'rgba(59,130,246,0.12)', glowBg: 'rgba(59,130,246,0.25)', borderColor: 'rgba(59,130,246,0.4)' },
  { id: 'REG', label: 'REG-LOCATION', fullLabel: 'Functional Location Registry', alias: 'EIS', aliasFull: 'Engineering Information System', accent: '#f59e0b', dimBg: 'rgba(245,158,11,0.12)', glowBg: 'rgba(245,158,11,0.25)', borderColor: 'rgba(245,158,11,0.4)' },
  { id: 'MMS', label: 'MMS', fullLabel: 'Maintenance Management System', alias: 'TAMS', aliasFull: 'Transportation Asset Management System', accent: '#10b981', dimBg: 'rgba(16,185,129,0.12)', glowBg: 'rgba(16,185,129,0.25)', borderColor: 'rgba(16,185,129,0.4)' },
  // REL keeps its own name: the client had no separate application for it.
  { id: 'RELIABILITY', label: 'REL', fullLabel: 'Reliability Engineering', alias: 'REL', aliasFull: 'Reliability Engineering', accent: '#a78bfa', dimBg: 'rgba(167,139,250,0.12)', glowBg: 'rgba(167,139,250,0.25)', borderColor: 'rgba(167,139,250,0.4)' },
  { id: 'CONSTRUCT', label: 'CONSTRUCT', fullLabel: 'Construction Management System', alias: 'SYNCHRO', aliasFull: 'Construction Management System', accent: '#fb923c', dimBg: 'rgba(251,146,60,0.12)', glowBg: 'rgba(251,146,60,0.25)', borderColor: 'rgba(251,146,60,0.4)' },
  { id: 'REG_ASSET', label: 'REG-ASSET', fullLabel: 'O&M Asset Registry', alias: 'AR', aliasFull: 'O&M Asset Registry', accent: '#22d3ee', dimBg: 'rgba(34,211,238,0.12)', glowBg: 'rgba(34,211,238,0.25)', borderColor: 'rgba(34,211,238,0.4)' },
  // GIS takes part in no workflow step yet, so it will not appear in the sidebar
  // until one names it. Listed here so the alias is ready when it does.
  { id: 'GIS', label: 'GIS', fullLabel: 'Geographic Information System', alias: 'ESRI', aliasFull: 'Geospatial Mapping Software', accent: '#84cc16', dimBg: 'rgba(132,204,22,0.12)', glowBg: 'rgba(132,204,22,0.25)', borderColor: 'rgba(132,204,22,0.4)' },
]

// ─── Workflow step definitions ────────────────────────────────────────────────

interface WorkflowStep {
  num: number
  persona: PersonaId
  label: string
  description: string
  action?: string
}

// SC01 stops at REG-LOCATION deliberately.
//
// Design data at this stage is a proposal about a plant that may not be built as
// drawn, so it is not admitted to operations by an engineer publishing it. The
// registry is the release gate, and a steward's approval is what lets it travel
// onward -- which is SC02. The sandbox scenario file makes the same point with
// negative assertions that nothing reached MMS during SC01.
const SC01_STEPS: WorkflowStep[] = [
  // Step 1 is backed by the sandbox: authoring happens in the ENG SEGMENTS panel
  // below, which posts to /admin/eng/tags. It carries no action button, because
  // a segment number has to be chosen and a button cannot ask for one.
  { num: 1, persona: 'ENG',         label: 'Author Segments',      description: 'Enrich the iModel with segments — writes to the sandbox' },
  { num: 2, persona: 'ENG',         label: 'Publish Design',        description: 'Release pending segments as a Named Version — goes to EIS' },
  { num: 3, persona: 'REG',         label: 'Receive Segments',      description: 'Incoming design proposals from BIC land in the stewardship queue' },
  { num: 4, persona: 'REG',         label: 'Validate Segments',     description: 'Verify segments meet regulatory requirements',            action: 'VALIDATE' },
]

// SC02 is the second leg of the same handover: the act SC01 withholds.
//
// A steward approving is a different kind of event from an engineer promoting.
// Promotion is a statement about design maturity; approval is a release decision
// about what operations is allowed to see. Approval mints location codes, and
// only then can the design travel on.
//
// REL is reached over a different channel from MMS, not the same one. REG-LOCATION
// publishes to /OandM (engineering provisioning, ISO18435 D0.2) which MMS
// subscribes to; om-reliability subscribes to /OandM-Events (operational events,
// D1.3) which MMS publishes. Keeping the two apart is what stops one bus path
// merging two distinct information domains.
//
// GIS sits with MMS on /OandM rather than with REL on /OandM-Events. It consumes
// the approved location itself — the spatial extent of a segment is provisioning
// data, settled at approval — not the operational events MMS goes on to raise
// about it. Same channel as MMS, therefore, and for the same reason.
const SC02_STEPS: WorkflowStep[] = [
  { num: 1, persona: 'REG',         label: 'Review Proposals',      description: 'Segments proposed by BIC await a stewardship decision' },
  { num: 2, persona: 'REG',         label: 'Approve Segments',      description: 'Approve — mints location codes and releases to O&M',      action: 'APPROVE' },
  { num: 3, persona: 'MMS',         label: 'Receive Segments',      description: 'Approved locations arrive on the O&M channel' },
  { num: 4, persona: 'MMS',         label: 'Store as Assets',       description: 'Commit locations as TAMS functional location records',     action: 'STORE ASSETS' },
  { num: 5, persona: 'GIS',         label: 'Receive Segments',      description: 'Approved locations arrive on the O&M channel' },
  { num: 6, persona: 'GIS',         label: 'Store as Features',     description: 'Commit locations as ESRI geospatial features',              action: 'STORE ASSETS' },
  { num: 7, persona: 'RELIABILITY', label: 'Receive Segments',      description: 'Segment data reaches reliability via the O&M events channel' },
  { num: 8, persona: 'RELIABILITY', label: 'Store as Assets',       description: 'Register segments in Reliability asset records',          action: 'STORE ASSETS' },
]

const SC11_STEPS: WorkflowStep[] = [
  { num: 1, persona: 'MMS',         label: 'Install Asset',         description: 'Install a physical asset into a segment record',          action: 'INSTALL ASSET' },
  { num: 2, persona: 'MMS',         label: 'Publish Update',        description: 'Broadcast asset installation to subscribers',             action: 'PUBLISH UPDATE' },
  { num: 3, persona: 'RELIABILITY', label: 'Receive Update',        description: 'Incoming TAMS asset installation update' },
  { num: 4, persona: 'RELIABILITY', label: 'Update Records',        description: 'Update own records with fitted asset + serial number',    action: 'UPDATE RECORDS' },
  { num: 5, persona: 'REG',         label: 'Receive Update',        description: 'Incoming TAMS asset installation update' },
  { num: 6, persona: 'REG',         label: 'Update Segment Records', description: 'Update segment records with fitted asset data',          action: 'UPDATE RECORDS' },
]

const SC04_STEPS: WorkflowStep[] = [
  { num: 1, persona: 'ENG',       label: 'Author As-Built Record',       description: 'Create as-built engineering asset record',                        action: 'CREATE AS-BUILT' },
  { num: 3, persona: 'ENG',       label: 'Publish As-Built Design',      description: 'Release as-built engineering data to Construction',              action: 'PUBLISH AS-BUILT' },
  { num: 4, persona: 'CONSTRUCT', label: 'Receive As-Built Data',        description: 'Incoming as-built engineering data from BIC' },
  { num: 5, persona: 'CONSTRUCT', label: 'Publish Constructed Asset',    description: 'Publish constructed asset & installation data to AR',     action: 'PUBLISH ASSET' },
  { num: 6, persona: 'REG_ASSET', label: 'Receive Asset Data',           description: 'Incoming serialized equipment asset data from SYNCHRO' },
  { num: 7, persona: 'REG_ASSET', label: 'Register O&M Asset',           description: 'Register asset in the O&M Asset Registry',                       action: 'REGISTER ASSET' },
]

// SC05 picks up exactly where SC04 stops. SC04 ends with AR holding a registered
// asset; SC05 is the act of releasing that asset to the systems that operate and
// maintain it.
//
// The three consumers are deliberately the same trio as SC02, because this is the
// same handover shape one level down: SC02 hands over the location a thing sits
// in, SC05 hands over the serialised thing itself. TAMS maintains it, REL builds
// its reliability history, ESRI places it on the map.
//
// Only assets AR has actually registered can travel — the guard in handleSC05
// enforces that, so the demo cannot skip SC04 and still show a result here.
const SC05_STEPS: WorkflowStep[] = [
  { num: 1, persona: 'REG_ASSET', label: 'Review Registered Assets', description: 'Assets registered in the O&M Asset Registry, ready for release' },
  { num: 2, persona: 'REG_ASSET', label: 'Publish to O&M',          description: 'Release registered assets to the O&M systems',            action: 'PUBLISH TO O&M' },
  { num: 3, persona: 'MMS',       label: 'Receive Asset Data',      description: 'Registered assets arrive on the O&M channel' },
  { num: 4, persona: 'MMS',       label: 'Store as Assets',         description: 'Commit assets as TAMS maintainable equipment records', action: 'STORE ASSETS' },
  { num: 5, persona: 'RELIABILITY', label: 'Receive Asset Data',    description: 'Asset data reaches reliability via the O&M events channel' },
  { num: 6, persona: 'RELIABILITY', label: 'Store as Assets',       description: 'Register assets in Reliability asset records',           action: 'STORE ASSETS' },
  { num: 7, persona: 'GIS',       label: 'Receive Asset Data',      description: 'Registered assets arrive on the O&M channel' },
  { num: 8, persona: 'GIS',       label: 'Store as Features',       description: 'Commit assets as ESRI geospatial features',              action: 'STORE ASSETS' },
]

const WORKFLOW_STEPS: Record<WorkflowId, WorkflowStep[]> = { SC01: SC01_STEPS, SC02: SC02_STEPS, SC11: SC11_STEPS, SC04: SC04_STEPS, SC05: SC05_STEPS }

/**
 * Who takes part, in the order the data travels.
 *
 * Derived from the steps so the two cannot disagree: a persona with a step is a
 * participant by definition, and the sidebar showing anyone else would offer a
 * view with nothing in it.
 *
 * Intended to become configuration. Until then this is the single place a
 * workflow's cast is decided.
 */
const WORKFLOW_PERSONAS: Record<WorkflowId, PersonaId[]> = Object.fromEntries(
  (Object.entries(WORKFLOW_STEPS) as [WorkflowId, WorkflowStep[]][]).map(
    ([id, steps]) => [id, [...new Set(steps.map(s => s.persona))]],
  ),
) as Record<WorkflowId, PersonaId[]>

// The OIIE scenario ids (SC01, SC02...) are kept: they are how these exchanges
// are referred to in the specification, and a reviewer checking the demo against
// it needs them. Only the participant names inside the sentence are aliased.
const WORKFLOW_DESCRIPTION: Record<WorkflowId, string> = {
  SC01: 'Publish As-Designed / As-Built Engineering Network / Segment / Tag data from BIC to EIS',
  SC02: 'Publish As-Designed / As-Built Engineering Network / Segment / Tag data from EIS to O&M',
  SC04: 'Publish As-Built Engineering Asset data from BIC, SYNCHRO to AR',
  SC05: 'Publish As-Built Engineering Asset data from AR to O&M',
  SC11: 'Publish Asset Removal / Installation events from TAMS to EIS and O&M',
}

// ─── Seed data ────────────────────────────────────────────────────────────────

function makeUuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
    const r = Math.random() * 16 | 0
    return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16)
  })
}

const SEED_SEGMENTS: Segment[] = [
  { uuid: 'a1b2c3d4-0001-4e5f-8a9b-c0d1e2f30001', shortName: 'PUMP-TRAIN-A',    fullName: 'Primary Pump Train — Section A',           segmentType: 'Rotating Equipment', registrationSite: 'RTM-REFINERY', created: '2026-08-10 09:14', status: 'draft',     storedMms: false, storedRel: false, storedGis: false },
  { uuid: 'a1b2c3d4-0002-4e5f-8a9b-c0d1e2f30002', shortName: 'VALVE-CTRL-3B',   fullName: 'Control Valve Assembly — Loop 3B',         segmentType: 'Control Element',    registrationSite: 'RTM-REFINERY', created: '2026-08-10 10:32', status: 'draft',     storedMms: false, storedRel: false, storedGis: false },
  { uuid: 'a1b2c3d4-0003-4e5f-8a9b-c0d1e2f30003', shortName: 'COMP-STAGE-2',    fullName: 'Compressor Stage 2 — High Pressure Train', segmentType: 'Rotating Equipment', registrationSite: 'HBG-CHEMICALS', created: '2026-08-11 08:55', status: 'published', storedMms: false, storedRel: false, storedGis: false },
  { uuid: 'a1b2c3d4-0004-4e5f-8a9b-c0d1e2f30004', shortName: 'HEX-SHELL-07',    fullName: 'Shell & Tube Heat Exchanger — Unit 07',   segmentType: 'Static Equipment',   registrationSite: 'HBG-CHEMICALS', created: '2026-08-11 14:10', status: 'validated', storedMms: false, storedRel: false, storedGis: false },
  { uuid: 'a1b2c3d4-0005-4e5f-8a9b-c0d1e2f30005', shortName: 'TANK-STORAGE-F',  fullName: 'Floating Roof Storage Tank — Farm F',     segmentType: 'Static Equipment',   registrationSite: 'BRG-OFFSHORE', created: '2026-08-12 07:20', status: 'approved',  storedMms: false, storedRel: false, storedGis: false },
]

const SEED_UPDATES: AssetUpdate[] = [
  { id: 'UPD-001', segmentUuid: 'a1b2c3d4-0005-4e5f-8a9b-c0d1e2f30005', segmentShortName: 'TANK-STORAGE-F', iTwinShortName: 'BRG-OFFSHORE', assetType: 'Level Transmitter', serialNumber: 'SN-LT-20847', installedBy: 'R. Torres', installedAt: '2026-08-12 11:05', status: 'pending', relUpdated: false, regUpdated: false },
]


const ASSET_TEMPLATES = [
  { assetType: 'Level Transmitter',   serial: 'SN-LT-' },
  { assetType: 'Pressure Sensor',     serial: 'SN-PS-' },
  { assetType: 'Flow Meter',          serial: 'SN-FM-' },
  { assetType: 'Temperature Element', serial: 'SN-TE-' },
]

const SEED_ASBUILT: AsBuiltAsset[] = [
  { uuid: 'b2c3d4e5-0001-4f6a-9b0c-d1e2f3a40001', equipmentTag: 'P-1001A', description: 'Centrifugal Pump — Cooling Water Service A', equipmentClass: 'Pump', manufacturer: 'Flowserve', modelNumber: 'DVSH-300', serialNumber: 'FLW-2026-00412', installationSite: 'RTM-REFINERY', installDate: '2026-07-15', created: '2026-08-05 10:00', status: 'draft', registeredOm: false, omMms: false, omRel: false, omGis: false },
  { uuid: 'b2c3d4e5-0002-4f6a-9b0c-d1e2f3a40002', equipmentTag: 'E-2003', description: 'Shell & Tube Heat Exchanger — Feed Preheat', equipmentClass: 'Heat Exchanger', manufacturer: 'Alfa Laval', modelNumber: 'TS-6M', serialNumber: 'ALF-2026-00188', installationSite: 'HBG-CHEMICALS', installDate: '2026-07-22', created: '2026-08-06 09:30', status: 'eng_published', registeredOm: false, omMms: false, omRel: false, omGis: false },
  { uuid: 'b2c3d4e5-0003-4f6a-9b0c-d1e2f3a40003', equipmentTag: 'K-3002', description: 'Reciprocating Compressor — Gas Injection', equipmentClass: 'Compressor', manufacturer: 'Burckhardt', modelNumber: 'L-B20H', serialNumber: 'BUC-2026-00073', installationSite: 'BRG-OFFSHORE', installDate: '2026-08-01', created: '2026-08-07 14:15', status: 'construct_published', registeredOm: false, omMms: false, omRel: false, omGis: false },
  // Already registered by AR, so SC05 can be demonstrated on its own without
  // first walking SC04 end to end. One per site, so the scenario has something
  // to show whichever site is selected.
  { uuid: 'b2c3d4e5-0004-4f6a-9b0c-d1e2f3a40004', equipmentTag: 'P-1002B', description: 'Centrifugal Pump — Cooling Water Service B', equipmentClass: 'Pump', manufacturer: 'Flowserve', modelNumber: 'DVSH-300', serialNumber: 'FLW-2026-00519', installationSite: 'RTM-REFINERY', installDate: '2026-07-18', created: '2026-08-08 08:20', status: 'registered', registeredOm: true, omMms: false, omRel: false, omGis: false },
  { uuid: 'b2c3d4e5-0005-4f6a-9b0c-d1e2f3a40005', equipmentTag: 'RX-0201', description: 'Jacketed Reactor — Polymerisation Train 2', equipmentClass: 'Vessel', manufacturer: 'ERGIL', modelNumber: 'JR-4500', serialNumber: 'ERG-2026-00231', installationSite: 'HBG-CHEMICALS', installDate: '2026-07-29', created: '2026-08-08 11:05', status: 'registered', registeredOm: true, omMms: false, omRel: false, omGis: false },
  { uuid: 'b2c3d4e5-0006-4f6a-9b0c-d1e2f3a40006', equipmentTag: 'G-5001', description: 'Gas Turbine Generator — Main Power', equipmentClass: 'Turbine', manufacturer: 'Siemens Energy', modelNumber: 'SGT-400', serialNumber: 'SIE-2026-00094', installationSite: 'BRG-OFFSHORE', installDate: '2026-08-03', created: '2026-08-08 15:40', status: 'registered', registeredOm: true, omMms: false, omRel: false, omGis: false },
]

const NEW_ASBUILT_TEMPLATES = [
  { equipmentTag: 'V-4010',  description: 'Horizontal Pressure Vessel — Flash Drum',     equipmentClass: 'Vessel',         manufacturer: 'ERGIL',      modelNumber: 'HV-900', installationSite: 'RTM-REFINERY' },
  { equipmentTag: 'AG-1005', description: 'Top-Entry Agitator — Reaction Vessel RX-02',  equipmentClass: 'Agitator',       manufacturer: 'Mixel',      modelNumber: 'TT-120', installationSite: 'HBG-CHEMICALS' },
  { equipmentTag: 'FT-2201', description: 'Electromagnetic Flow Meter — Produced Water', equipmentClass: 'Instrumentation', manufacturer: 'Endress+Hauser', modelNumber: 'Promag-W', installationSite: 'BRG-OFFSHORE' },
  { equipmentTag: 'MV-5503', description: 'Motor-Operated Gate Valve — HP Bypass',       equipmentClass: 'Valve',          manufacturer: 'Velan',      modelNumber: 'F-7150', installationSite: 'RTM-REFINERY' },
]

let abCounter = 4
let segCounter = 6
let updCounter = 2

// ─── Small components ─────────────────────────────────────────────────────────

function Pill({ label, color }: { label: string; color: string }) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', padding: '2px 8px', borderRadius: '3px', background: color + '22', color, fontFamily: 'var(--font-mono)', fontSize: '10px', fontWeight: 600, letterSpacing: '0.08em', lineHeight: '18px' }}>
      {label}
    </span>
  )
}

// Signed-in user, top right. Sits inside the 52px header, so the control is
// sized to fit that band rather than setting its own height.
//
// The menu is absolutely positioned against a relative wrapper instead of being
// portalled: the header is the last thing painted in its stacking context, so a
// plain z-index is enough and a portal would only add ceremony.
function UserMenu({ user }: { user: CurrentUser }) {
  const [open, setOpen] = useState(false)
  const [hov, setHov] = useState(false)
  const [logoutHov, setLogoutHov] = useState(false)
  const wrapRef = useRef<HTMLDivElement>(null)

  // Close on outside click and on Escape. Both are registered only while open,
  // so the app carries no listeners in its resting state.
  useEffect(() => {
    if (!open) return
    function onDown(e: MouseEvent) {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false)
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const row = (label: string, value: string) => (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
      <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.1em' }}>{label}</span>
      <span style={{ fontFamily: 'var(--font-mono)', fontSize: '11px', color: 'var(--text-secondary)', wordBreak: 'break-all' }}>{value}</span>
    </div>
  )

  return (
    <div ref={wrapRef} style={{ position: 'relative', flexShrink: 0 }}>
      <button
        onClick={() => setOpen(o => !o)}
        onMouseEnter={() => setHov(true)}
        onMouseLeave={() => setHov(false)}
        title={user.name}
        aria-haspopup="menu"
        aria-expanded={open}
        style={{
          display: 'flex', alignItems: 'center', gap: 8, background: open || hov ? 'rgba(255,255,255,0.08)' : 'transparent',
          border: `1px solid ${open ? 'var(--border-mid)' : 'var(--border-subtle)'}`, borderRadius: '4px',
          padding: '4px 8px 4px 4px', cursor: 'pointer', transition: 'all 0.13s',
        }}>
        <span style={{ width: 24, height: 24, borderRadius: '50%', background: 'linear-gradient(135deg, #3b82f6, #10b981)', color: '#fff', display: 'flex', alignItems: 'center', justifyContent: 'center', fontFamily: 'var(--font-mono)', fontSize: '10px', fontWeight: 700, letterSpacing: '0.04em', flexShrink: 0 }}>
          {initialsOf(user.name)}
        </span>
        <span style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', fontWeight: 600, letterSpacing: '0.08em', color: open || hov ? 'var(--text-primary)' : 'var(--text-muted)', whiteSpace: 'nowrap' }}>
          {user.name.toUpperCase()}
        </span>
        <span style={{ fontSize: '8px', color: 'var(--text-muted)', transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.13s' }}>▼</span>
      </button>

      {open && (
        <div role="menu" style={{ position: 'absolute', top: 'calc(100% + 8px)', right: 0, width: 260, background: 'var(--bg-surface)', border: '1px solid var(--border-mid)', borderRadius: '6px', boxShadow: '0 10px 30px rgba(0,0,0,0.45)', zIndex: 50, overflow: 'hidden' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '14px', borderBottom: '1px solid var(--border-subtle)' }}>
            <span style={{ width: 34, height: 34, borderRadius: '50%', background: 'linear-gradient(135deg, #3b82f6, #10b981)', color: '#fff', display: 'flex', alignItems: 'center', justifyContent: 'center', fontFamily: 'var(--font-mono)', fontSize: '12px', fontWeight: 700, flexShrink: 0 }}>
              {initialsOf(user.name)}
            </span>
            <div style={{ minWidth: 0 }}>
              <div style={{ fontFamily: 'var(--font-mono)', fontSize: '12px', fontWeight: 700, color: 'var(--text-primary)' }}>{user.name}</div>
              {/* Suppressed when the name already is the email: IMS sometimes has
                  no name claim beyond the address, and repeating it on both lines
                  reads like a rendering fault. */}
              {user.email && user.email !== user.name && (
                <div style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)', overflow: 'hidden', textOverflow: 'ellipsis' }}>{user.email}</div>
              )}
            </div>
          </div>

          <div style={{ padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: 11, borderBottom: '1px solid var(--border-subtle)' }}>
            {row('ORGANIZATION', user.organization)}
            {row('IMS SUBJECT', user.sub)}
            {/* IMS does not always issue role claims, so an empty list is a
                normal outcome rather than a fault; say so instead of showing
                an unexplained gap. */}
            <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
              <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.1em' }}>ENTITLEMENTS</span>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                {user.roles.length > 0
                  ? user.roles.map(r => <Pill key={r} label={r} color="#3b82f6" />)
                  : <span style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>none in token</span>}
              </div>
            </div>
          </div>

          {/* Ends the IMS session, not just the local one. Because the app has
              no offline presence, IMS returns the browser here and the gate
              immediately sends it back to the Bentley sign-in page. */}
          <button
            role="menuitem"
            onClick={() => auth.logout()}
            onMouseEnter={() => setLogoutHov(true)}
            onMouseLeave={() => setLogoutHov(false)}
            style={{ width: '100%', display: 'flex', alignItems: 'center', gap: 9, background: logoutHov ? 'rgba(239,68,68,0.10)' : 'transparent', border: 'none', borderTop: '1px solid var(--border-subtle)', padding: '11px 14px', fontFamily: 'var(--font-mono)', fontSize: '11px', fontWeight: 700, letterSpacing: '0.1em', color: logoutHov ? '#ef4444' : 'var(--text-secondary)', cursor: 'pointer', textAlign: 'left', transition: 'all 0.13s' }}>
            <span style={{ fontSize: '12px', lineHeight: 1 }}>⇥</span>
            LOG OUT
          </button>
        </div>
      )}
    </div>
  )
}

function Btn({ label, onClick, accent, dimBg, borderColor, disabled = false, ghost = false }: { label: string; onClick: () => void; accent: string; dimBg: string; borderColor: string; disabled?: boolean; ghost?: boolean }) {
  const [hov, setHov] = useState(false)
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      onMouseEnter={() => setHov(true)}
      onMouseLeave={() => setHov(false)}
      style={{
        background: disabled ? 'transparent' : ghost ? (hov ? dimBg : 'transparent') : (hov ? accent : dimBg),
        border: `1px solid ${disabled ? 'var(--border-subtle)' : hov ? accent : borderColor}`,
        borderRadius: '4px',
        color: disabled ? 'var(--text-muted)' : ghost ? (hov ? accent : 'var(--text-secondary)') : (hov ? '#fff' : accent),
        fontFamily: 'var(--font-mono)',
        fontSize: '11px',
        fontWeight: 600,
        letterSpacing: '0.06em',
        padding: '7px 16px',
        cursor: disabled ? 'not-allowed' : 'pointer',
        transition: 'all 0.13s ease',
        whiteSpace: 'nowrap',
        boxShadow: hov && !disabled && !ghost ? `0 0 14px ${dimBg}` : 'none',
      }}
    >
      {label}
    </button>
  )
}

function Toast({ message, accent }: { message: string; accent: string }) {
  return (
    <div style={{ position: 'fixed', bottom: 28, left: '50%', transform: 'translateX(-50%)', background: 'var(--bg-panel)', border: `1px solid ${accent}66`, borderRadius: '6px', padding: '9px 20px', color: accent, fontFamily: 'var(--font-mono)', fontSize: '12px', fontWeight: 500, letterSpacing: '0.04em', boxShadow: '0 4px 32px rgba(0,0,0,0.5)', zIndex: 1000, pointerEvents: 'none', animation: 'fadeInUp 0.2s ease' }}>
      {message}
    </div>
  )
}

// ─── Pipeline banner ──────────────────────────────────────────────────────────

// One colour per persona, shared by the banner and the sidebar step list. Was
// duplicated inline in both, and SC04's CONSTRUCT and REG_ASSET were missing
// from each copy, so those steps rendered with an undefined colour.
const PERSONA_COLOR: Record<PersonaId, string> = {
  ENG: '#3b82f6', REG: '#f59e0b', MMS: '#10b981', RELIABILITY: '#a78bfa', CONSTRUCT: '#fb923c', REG_ASSET: '#22d3ee', GIS: '#84cc16',
}

/**
 * The client-facing short name for a persona.
 *
 * The banner previously rendered the raw PersonaId, which is why REG_ASSET
 * appeared with an underscore. Going through the persona table fixes that and
 * keeps every on-screen name coming from one place.
 */
function personaAlias(id: PersonaId): string {
  return PERSONAS.find(p => p.id === id)?.alias ?? id
}

function PipelineBanner({ steps, activePersona }: { steps: WorkflowStep[]; activePersona: PersonaId }) {
  const pMap = PERSONA_COLOR
  return (
    <div style={{ display: 'flex', alignItems: 'center', overflowX: 'auto', paddingBottom: 2 }}>
      {steps.map((step, i) => {
        const color = pMap[step.persona]
        const isMine = step.persona === activePersona
        return (
          <div key={i} style={{ display: 'flex', alignItems: 'center' }}>
            <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 3, opacity: isMine ? 1 : 0.35 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 5 }}>
                <div style={{ width: 7, height: 7, borderRadius: '50%', background: isMine ? color : 'var(--text-muted)', flexShrink: 0 }} />
                <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: isMine ? color : 'var(--text-muted)', letterSpacing: '0.06em', whiteSpace: 'nowrap' }}>{personaAlias(step.persona)}</span>
              </div>
              <span style={{ fontFamily: 'var(--font-mono)', fontSize: '8px', color: isMine ? 'var(--text-secondary)' : 'var(--text-muted)', whiteSpace: 'nowrap', maxWidth: 90, textAlign: 'center', lineHeight: 1.3 }}>{step.label}</span>
            </div>
            {i < steps.length - 1 && <div style={{ width: 20, height: 1, background: 'var(--border-mid)', margin: '0 4px', marginBottom: 8, flexShrink: 0 }} />}
          </div>
        )
      })}
    </div>
  )
}

// ─── Steps panel ─────────────────────────────────────────────────────────────

function StepsPanel({ steps, persona, accent, dimBg, borderColor, onAction, selectedCount }: {
  steps: WorkflowStep[]; persona: PersonaId; accent: string; dimBg: string; borderColor: string; onAction: (a: string) => void; selectedCount: number
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      {steps.filter(s => s.persona === persona).map((step, i) => (
        <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 14, background: 'var(--bg-panel)', border: '1px solid var(--border-subtle)', borderRadius: '5px', padding: '12px 16px' }}>
          <div style={{ width: 26, height: 26, borderRadius: '50%', background: dimBg, border: `1.5px solid ${borderColor}`, display: 'flex', alignItems: 'center', justifyContent: 'center', fontFamily: 'var(--font-mono)', fontSize: '10px', fontWeight: 700, color: accent, flexShrink: 0 }}>
            {step.num}
          </div>
          <div style={{ flex: 1 }}>
            <div style={{ fontSize: '12px', fontWeight: 600, color: 'var(--text-primary)', marginBottom: 2 }}>{step.label}</div>
            <div style={{ fontSize: '11px', color: 'var(--text-muted)' }}>{step.description}</div>
          </div>
          {step.action && (
            <Btn label={step.action} onClick={() => onAction(step.action!)} accent={accent} dimBg={dimBg} borderColor={borderColor} disabled={selectedCount === 0 && step.action !== 'INSTALL ASSET'} />
          )}
        </div>
      ))}
    </div>
  )
}

// ─── Inbox tables ─────────────────────────────────────────────────────────────

const SEG_STATUS_COLOR: Record<SegmentStatus, string> = {
  draft: '#6b7280', published: '#3b82f6', validated: '#f59e0b', approved: '#10b981', stored_mms: '#10b981', stored_rel: '#a78bfa',
}
const SEG_STATUS_LABEL: Record<SegmentStatus, string> = {
  draft: 'DRAFT', published: 'PUBLISHED', validated: 'VALIDATED', approved: 'APPROVED', stored_mms: 'STORED', stored_rel: 'STORED',
}

// The sandbox's own segment lifecycle. Only three states, and only the engine
// moves a segment between them.
const MATURITY_COLOR: Record<api.TagMaturity, string> = {
  WorkInProgress: '#6b7280', Shared: '#f59e0b', Published: '#10b981',
}
const MATURITY_LABEL: Record<api.TagMaturity, string> = {
  WorkInProgress: 'WIP', Shared: 'SHARED', Published: 'PUBLISHED',
}

/**
 * What the publication gate will refuse, if anything.
 *
 * Mirrors EngService.Validate. Held here so the table can warn before a release
 * is attempted rather than after: a Named Version is all-or-nothing, so one
 * unclassified segment blocks every other segment in the iModel, and finding
 * that out only at publish time is a poor trade.
 *
 * A duplicate of server-side rules, and therefore capable of drifting from them.
 * The gate remains the authority: this is a hint, and the findings returned by a
 * refused promotion are the truth.
 */
function gateFindings(seg: api.Tag): string[] {
  const missing: string[] = []
  if (!seg.classKey) missing.push('class key')
  if (!seg.serviceDescription) missing.push('service description')
  return missing
}

const AB_STATUS_COLOR: Record<AsBuiltStatus, string> = {
  draft: '#6b7280', eng_published: '#3b82f6', construct_published: '#fb923c', registered: '#22d3ee', om_published: '#10b981',
}
const AB_STATUS_LABEL: Record<AsBuiltStatus, string> = {
  draft: 'DRAFT', eng_published: 'BIC PUBLISHED', construct_published: 'SYNCHRO PUB', registered: 'REGISTERED', om_published: 'O&M PUBLISHED',
}

const UPD_STATUS_COLOR: Record<UpdateStatus, string> = {
  pending: '#f59e0b', published: '#3b82f6', rel_updated: '#a78bfa', reg_updated: '#10b981',
}
const UPD_STATUS_LABEL: Record<UpdateStatus, string> = {
  pending: 'PENDING', published: 'PUBLISHED', rel_updated: 'REL UPDATED', reg_updated: 'REG UPDATED',
}

function Checkbox({ checked, onToggle, accent }: { checked: boolean; onToggle: () => void; accent: string }) {
  return (
    <div onClick={onToggle} style={{ width: 14, height: 14, borderRadius: '3px', border: `1.5px solid ${checked ? accent : 'var(--border-mid)'}`, background: checked ? accent : 'transparent', display: 'flex', alignItems: 'center', justifyContent: 'center', cursor: 'pointer', transition: 'all 0.1s', flexShrink: 0 }}>
      {checked && <svg width="9" height="7" viewBox="0 0 9 7" fill="none"><path d="M1 3.5L3.5 6L8 1" stroke="#fff" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" /></svg>}
    </div>
  )
}

function TRow({ children, isSel, onToggle, dimBg }: { children: React.ReactNode; isSel: boolean; onToggle: () => void; dimBg: string }) {
  const [hov, setHov] = useState(false)
  return (
    <tr onClick={onToggle} onMouseEnter={() => setHov(true)} onMouseLeave={() => setHov(false)} style={{ background: isSel ? dimBg : hov ? 'var(--bg-hover)' : 'transparent', cursor: 'pointer', transition: 'background 0.1s', borderBottom: '1px solid var(--border-subtle)' }}>
      {children}
    </tr>
  )
}

const TH = ({ children }: { children: string }) => (
  <th style={{ padding: '8px 8px', textAlign: 'left', fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.1em', fontWeight: 600, whiteSpace: 'nowrap' }}>{children}</th>
)

function SegmentTable({ segments, selected, onToggle, onToggleAll, accent, dimBg }: { segments: Segment[]; selected: Set<string>; onToggle: (id: string) => void; onToggleAll: () => void; accent: string; dimBg: string }) {
  const allSel = segments.length > 0 && segments.every(s => selected.has(s.uuid))
  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', minWidth: 820 }}>
        <thead>
          <tr style={{ borderBottom: '1px solid var(--border-mid)' }}>
            <th style={{ padding: '8px 14px', width: 36 }}><Checkbox checked={allSel} onToggle={onToggleAll} accent={accent} /></th>
            <TH>UUID</TH>
            <TH>SHORT NAME</TH>
            <TH>FULL NAME</TH>
            <TH>SEGMENT TYPE</TH>
            <TH>iTwin</TH>
            <TH>CREATED</TH>
            <TH>STATUS</TH>
          </tr>
        </thead>
        <tbody>
          {segments.length === 0 ? (
            <tr><td colSpan={8} style={{ padding: '48px', textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: '11px', color: 'var(--text-muted)' }}>
              <div style={{ opacity: 0.4, fontSize: 24, marginBottom: 8 }}>◎</div>No items in inbox
            </td></tr>
          ) : segments.map(seg => (
            <TRow key={seg.uuid} isSel={selected.has(seg.uuid)} onToggle={() => onToggle(seg.uuid)} dimBg={dimBg}>
              <td style={{ padding: '9px 14px' }}><Checkbox checked={selected.has(seg.uuid)} onToggle={() => onToggle(seg.uuid)} accent={accent} /></td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: accent, fontWeight: 500, opacity: 0.75 }}>{seg.uuid.slice(0, 8)}…</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '11px', color: accent, fontWeight: 600 }}>{seg.shortName}</td>
              <td style={{ padding: '9px 8px', fontSize: '11px', color: 'var(--text-primary)', maxWidth: 240 }}>{seg.fullName}</td>
              <td style={{ padding: '9px 8px', fontSize: '11px', color: 'var(--text-secondary)' }}>{seg.segmentType}</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>{seg.registrationSite}</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>{seg.created}</td>
              <td style={{ padding: '9px 8px' }}><Pill label={SEG_STATUS_LABEL[seg.status]} color={SEG_STATUS_COLOR[seg.status]} /></td>
            </TRow>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/**
 * ENG's segments in the selected twin.
 *
 * The ENG repository is an iModel: design-tool data aggregated under an iTwin,
 * which these segments enrich. Adding one is ordinary, incremental work -- the
 * deliberate act is publishing them as a Named Version.
 *
 * "Segment" throughout the UI; the API calls the same record a Tag, which is the
 * process-industry word for it. Infrastructure does not use "tag", so the screens
 * say segment and only the wire types in api.ts keep the server's name.
 *
 * The maturity column is the engine's own lifecycle rather than a status this
 * app maintains: a segment is authored WorkInProgress and only becomes Published
 * by being included in a promoted Named Version.
 */
function SegmentTableLive({ segments, accent, dimBg, loading, error, selectedId, onSelect }: {
  segments: api.Tag[]
  accent: string
  dimBg: string
  loading: boolean
  error: string | null
  selectedId: number | null
  onSelect: (segment: api.Tag) => void
}) {
  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', minWidth: 880 }}>
        <thead>
          <tr style={{ borderBottom: '1px solid var(--border-mid)' }}>
            <TH>SEGMENT NUMBER</TH>
            <TH>SERVICE DESCRIPTION</TH>
            <TH>UNIT</TH>
            <TH>CLASS</TH>
            <TH>RANGE</TH>
            <TH>FEDERATION ID</TH>
            <TH>MATURITY</TH>
          </tr>
        </thead>
        <tbody>
          {error ? (
            <tr><td colSpan={7} style={{ padding: '32px', textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: '11px', color: '#f87171' }}>
              {error}
            </td></tr>
          ) : loading ? (
            <tr><td colSpan={7} style={{ padding: '48px', textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: '11px', color: 'var(--text-muted)' }}>
              loading segments&hellip;
            </td></tr>
          ) : segments.length === 0 ? (
            <tr><td colSpan={7} style={{ padding: '48px', textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: '11px', color: 'var(--text-muted)' }}>
              <div style={{ opacity: 0.4, fontSize: 24, marginBottom: 8 }}>◎</div>No segments in this twin yet
            </td></tr>
          ) : segments.map(seg => (
            <SegmentRow
              key={seg.id}
              seg={seg}
              accent={accent}
              dimBg={dimBg}
              isSelected={seg.id === selectedId}
              onSelect={() => onSelect(seg)}
            />
          ))}
        </tbody>
      </table>
    </div>
  )
}

function SegmentRow({ seg, accent, dimBg, isSelected, onSelect }: {
  seg: api.Tag
  accent: string
  dimBg: string
  isSelected: boolean
  onSelect: () => void
}) {
  const [hover, setHover] = useState(false)
  const missing = gateFindings(seg)

  return (
    <tr
      onClick={onSelect}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title="Load into the form to edit"
      style={{
        borderBottom: '1px solid var(--border-subtle)',
        background: isSelected ? dimBg : hover ? 'var(--bg-hover)' : 'transparent',
        cursor: 'pointer',
        transition: 'background 0.1s',
      }}
    >
      <td style={{ padding: '9px 14px', fontFamily: 'var(--font-mono)', fontSize: '11px', color: accent, fontWeight: 600 }}>
        {seg.tagNumber}
      </td>
      <td style={{ padding: '9px 8px', fontSize: '11px', color: seg.serviceDescription ? 'var(--text-primary)' : '#f59e0b', maxWidth: 260 }}>
        {seg.serviceDescription || 'missing'}
      </td>
      <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-secondary)' }}>{seg.unitNumber || '—'}</td>
      <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: seg.classKey ? 'var(--text-muted)' : '#f59e0b' }}>
        {seg.classKey || 'missing'}
      </td>
      <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>
        {seg.rangeMinimum !== null && seg.rangeMaximum !== null ? `${seg.rangeMinimum}–${seg.rangeMaximum}` : '—'}
      </td>
      <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>{seg.federationId ? `${seg.federationId.slice(0, 8)}…` : '—'}</td>
      <td style={{ padding: '9px 8px', whiteSpace: 'nowrap' }}>
        <Pill label={MATURITY_LABEL[seg.maturity]} color={MATURITY_COLOR[seg.maturity]} />
        {/* Flagged on the row rather than only at publish time: this one segment
            would hold back the whole Named Version. */}
        {missing.length > 0 && (
          <span
            title={`Blocks publication — ${missing.join(' and ')} required`}
            style={{ marginLeft: 6, fontFamily: 'var(--font-mono)', fontSize: '9px', color: '#f59e0b', fontWeight: 700 }}
          >
            ⚠ BLOCKS
          </span>
        )}
      </td>
    </tr>
  )
}

/**
 * Publishing the design: promoting a Named Version.
 *
 * Not a bare button, because promotion needs a version name and that name is
 * the operator's to choose -- it is how this release is identified afterwards.
 *
 * The ENG repository is an iModel, which belongs to an iTwin. Design tools feed
 * it, and segments are added to enrich it -- individually, continuously, and
 * without ceremony. Publication is the separate, deliberate act: the pending
 * segments are released together as one Named Version.
 *
 * So promotion is all-or-nothing over the twin by design, not by limitation.
 * This panel therefore reports how many segments are pending rather than
 * working from a table selection: per-row checkboxes would promise a partial
 * release that a Named Version is not.
 */
function PublishDesignPanel({ accent, dimBg, pendingCount, busy, result, onPromote, onDismiss }: {
  accent: string
  dimBg: string
  pendingCount: number
  busy: boolean
  result: api.PromotionResult | null
  onPromote: (name: string) => void
  onDismiss: () => void
}) {
  const [name, setName] = useState('')

  const canSubmit = name.trim().length > 0 && pendingCount > 0 && !busy

  function submit() {
    if (!canSubmit) return
    onPromote(name.trim())
  }

  return (
    <section>
      <div style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.14em', marginBottom: 10, display: 'flex', alignItems: 'center', gap: 10 }}>
        <span>PUBLISH DESIGN</span>
        <span style={{ background: dimBg, color: accent, padding: '1px 8px', borderRadius: '3px', fontWeight: 600 }}>
          {pendingCount} PENDING
        </span>
        <span style={{ color: 'var(--text-muted)', fontSize: '8px', letterSpacing: '0.1em', marginLeft: 4 }}>
          LIVE — POST /admin/eng/promote
        </span>
      </div>

      <div style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-subtle)', borderRadius: '6px', padding: '12px 14px' }}>
        {/* Stated on screen because the absence of per-row selection is a
            deliberate property of a Named Version, and looks like a missing
            feature otherwise. */}
        <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: 12, lineHeight: 1.5 }}>
          A Named Version releases every pending segment in this iModel as one batch.
          Segments are enriched individually; they are published together.
        </div>
        <div style={{ display: 'flex', gap: 10, alignItems: 'flex-end', flexWrap: 'wrap' }}>
          <label style={{ display: 'flex', flexDirection: 'column', gap: 4, width: 200 }}>
            <span style={{ fontFamily: 'var(--font-mono)', fontSize: '8px', color: 'var(--text-muted)', letterSpacing: '0.1em' }}>VERSION NAME *</span>
            <input
              value={name}
              placeholder="Rev-A"
              onChange={e => setName(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') submit() }}
              style={{ background: 'var(--bg-panel)', border: '1px solid var(--border-mid)', borderRadius: '3px', color: 'var(--text-primary)', fontFamily: 'var(--font-mono)', fontSize: '11px', padding: '5px 8px', outline: 'none' }}
            />
          </label>
          <button
            onClick={submit}
            disabled={!canSubmit}
            title={
              pendingCount === 0
                ? 'Every segment in this twin is already published'
                : canSubmit ? 'Promote a named version and release it to REG-LOCATION' : 'Enter a version name first'
            }
            style={{
              background: canSubmit ? dimBg : 'transparent',
              border: `1px solid ${canSubmit ? accent : 'var(--border-mid)'}`,
              borderRadius: '3px',
              color: canSubmit ? accent : 'var(--text-muted)',
              cursor: canSubmit ? 'pointer' : 'not-allowed',
              fontFamily: 'var(--font-mono)',
              fontSize: '10px',
              fontWeight: 600,
              letterSpacing: '0.1em',
              padding: '6px 16px',
            }}
          >
            {busy ? 'PUBLISHING…' : 'PUBLISH DESIGN'}
          </button>
          {!busy && pendingCount === 0 && (
            <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', paddingBottom: 6 }}>
              nothing pending — author a segment first
            </span>
          )}
          {!busy && pendingCount > 0 && name.trim().length === 0 && (
            <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', paddingBottom: 6 }}>
              enter a version name
            </span>
          )}
        </div>

        {/* A refusal is the interesting case: the gate names the segments that
            are not ready, and those lines are what tell the user what to fix. */}
        {result && !result.released && (
          <div style={{ marginTop: 12, padding: '10px 12px', background: 'rgba(245,158,11,0.08)', border: '1px solid rgba(245,158,11,0.35)', borderRadius: '3px' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: result.findings.length ? 8 : 0 }}>
              <span style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', color: '#f59e0b', fontWeight: 700, letterSpacing: '0.08em' }}>
                NOT RELEASED
              </span>
              <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)' }}>
                validation gate blocked “{result.name}”
              </span>
              <button onClick={onDismiss} style={{ marginLeft: 'auto', background: 'none', border: 'none', color: 'var(--text-muted)', cursor: 'pointer', fontSize: '10px', fontFamily: 'var(--font-mono)' }}>DISMISS</button>
            </div>
            {result.findings.map((f, i) => (
              <div key={i} style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-primary)', padding: '3px 0' }}>
                • {f}
              </div>
            ))}
          </div>
        )}

        {result && result.released && (
          <div style={{ marginTop: 12, padding: '10px 12px', background: 'rgba(16,185,129,0.08)', border: '1px solid rgba(16,185,129,0.35)', borderRadius: '3px', display: 'flex', alignItems: 'center', gap: 8 }}>
            <span style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', color: '#10b981', fontWeight: 700, letterSpacing: '0.08em' }}>RELEASED</span>
            <span style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-primary)' }}>
              “{result.name}” published {result.tagCount} segment(s) — SyncSegments queued for REG-LOCATION
            </span>
            <button onClick={onDismiss} style={{ marginLeft: 'auto', background: 'none', border: 'none', color: 'var(--text-muted)', cursor: 'pointer', fontSize: '10px', fontFamily: 'var(--font-mono)' }}>DISMISS</button>
          </div>
        )}
      </div>
    </section>
  )
}

/**
 * REG-LOCATION's stewardship queue: what ENG published, awaiting a decision.
 *
 * Arrival is not acceptance. The registry is a governance gate, so these rows
 * exist in a holding area rather than in the authoritative model, and stay there
 * until a steward admits them.
 *
 * The class columns are the point of the review. A segment can arrive classified
 * more specifically than the registry understands, in which case it is bound to
 * an ancestor instead -- accepted, but with fidelity lost. That is a decision a
 * steward should see rather than have quietly made for them, so degraded
 * bindings and unmapped properties are called out rather than hidden behind a
 * row count.
 */
function StewardshipTable({ items, accent, loading, error }: {
  items: api.StewardshipItem[]
  accent: string
  loading: boolean
  error: string | null
}) {
  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', minWidth: 880 }}>
        <thead>
          <tr style={{ borderBottom: '1px solid var(--border-mid)' }}>
            <TH>SOURCE ID</TH>
            <TH>FROM</TH>
            <TH>PROPOSED NAME</TH>
            <TH>REQUESTED CLASS</TH>
            <TH>BOUND CLASS</TH>
            <TH>PROPERTIES</TH>
            <TH>STATE</TH>
          </tr>
        </thead>
        <tbody>
          {error ? (
            <tr><td colSpan={7} style={{ padding: '32px', textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: '11px', color: '#f87171' }}>{error}</td></tr>
          ) : loading ? (
            <tr><td colSpan={7} style={{ padding: '48px', textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: '11px', color: 'var(--text-muted)' }}>loading queue&hellip;</td></tr>
          ) : items.length === 0 ? (
            <tr><td colSpan={7} style={{ padding: '48px', textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: '11px', color: 'var(--text-muted)' }}>
              <div style={{ opacity: 0.4, fontSize: 24, marginBottom: 8 }}>◎</div>
              Nothing proposed — publish a Named Version from ENG
            </td></tr>
          ) : items.map(item => (
            <tr key={item.id} style={{ borderBottom: '1px solid var(--border-subtle)' }}>
              <td style={{ padding: '9px 14px', fontFamily: 'var(--font-mono)', fontSize: '11px', color: accent, fontWeight: 600 }}>{item.sourceIdentifier}</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>{item.sourceParticipant}</td>
              <td style={{ padding: '9px 8px', fontSize: '11px', color: 'var(--text-primary)', maxWidth: 220 }}>{item.proposedName || '—'}</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>{item.requestedClassKey || '—'}</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: item.boundClassKey ? 'var(--text-secondary)' : '#f59e0b' }}>
                {item.boundClassKey || 'unbound'}
                {/* Bound to an ancestor: accepted, but the sender was more
                    specific than this registry can represent. */}
                {item.classDegraded && (
                  <span title="Bound to an ancestor of the class the sender named" style={{ marginLeft: 6, color: '#f59e0b', fontWeight: 700 }}>↓ DEGRADED</span>
                )}
              </td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>
                {item.propertiesMapped} mapped
                {item.propertiesUnmapped > 0 && (
                  <span style={{ color: '#f59e0b' }}>, {item.propertiesUnmapped} unmapped</span>
                )}
              </td>
              <td style={{ padding: '9px 8px' }}>
                <Pill label={item.state.toUpperCase()} color={STEWARDSHIP_COLOR[item.state] ?? '#6b7280'} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

const STEWARDSHIP_COLOR: Record<string, string> = {
  Proposed: '#f59e0b', Approved: '#10b981', Rejected: '#ef4444',
}

/**
 * Which segments the ENG table shows.
 *
 * Defaults to pending rather than everything. Published segments are done: they
 * are in a Named Version and the registry has them, so leaving them in view
 * grows the table without adding anything to act on. What matters while
 * authoring is what has not gone out yet.
 */
type MaturityFilter = 'Pending' | 'Published' | 'All'

const MATURITY_FILTERS: MaturityFilter[] = ['Pending', 'Published', 'All']

function matchesFilter(seg: api.Tag, filter: MaturityFilter): boolean {
  if (filter === 'All') return true
  if (filter === 'Published') return seg.maturity === 'Published'
  // Pending is everything a Named Version would pick up, which is the same rule
  // PromoteAsync applies -- not merely WorkInProgress, so a Shared segment is
  // not hidden from the person about to publish it.
  return seg.maturity !== 'Published'
}

/**
 * Authoring a segment.
 *
 * A form rather than a button that pushes a canned template. Authoring is a
 * design decision the user is making -- the segment number in particular is
 * theirs to choose, and it must be unique within the twin -- so the app asks
 * instead of inventing one. This is the difference between driving the sandbox
 * and replaying a script through it.
 */
function SegmentForm({ accent, dimBg, busy, error, editing, classes, onSubmit, onDismissError, onCancelEdit }: {
  accent: string
  dimBg: string
  busy: boolean
  error: string | null
  /** The segment being edited, or null when authoring a new one. */
  editing: api.Tag | null
  /** What ENG can bind. Chosen from rather than typed. */
  classes: api.ClassDefinition[]
  onSubmit: (segment: api.NewTag) => void
  onDismissError: () => void
  onCancelEdit: () => void
}) {
  const [segmentNumber, setSegmentNumber] = useState('')
  const [serviceDescription, setServiceDescription] = useState('')
  const [unitNumber, setUnitNumber] = useState('')
  const [classKey, setClassKey] = useState('')

  // Loading a row fills every field, including the ones that are already fine.
  //
  // This is not merely convenient: POST /admin/eng/tags is an upsert that
  // assigns all editable fields unconditionally, so a payload omitting a field
  // blanks it. Submitting a partial edit would clear whatever was not shown.
  // Keying on the id means picking a different row re-seeds the form, while
  // typing within one row does not.
  useEffect(() => {
    setSegmentNumber(editing?.tagNumber ?? '')
    setServiceDescription(editing?.serviceDescription ?? '')
    setUnitNumber(editing?.unitNumber ?? '')
    setClassKey(editing?.classKey ?? '')
  }, [editing?.id])

  const canSubmit = segmentNumber.trim().length > 0 && !busy

  // Advisory only. The server decides, but naming the gap here means the user
  // learns it while the fields are in front of them rather than at publish time.
  const willBlock = [
    classKey.trim() ? null : 'class key',
    serviceDescription.trim() ? null : 'service description',
  ].filter((x): x is string => x !== null)

  function submit() {
    if (!canSubmit) return

    onSubmit({
      // tagNumber is the wire field: the API's name for this, kept only here at
      // the boundary so the rest of the UI can speak in segments.
      tagNumber: segmentNumber.trim(),
      serviceDescription: serviceDescription.trim() || undefined,
      unitNumber: unitNumber.trim() || undefined,
      classKey: classKey.trim() || undefined,
    })

    // Only when authoring. After an edit the fields stay as submitted, so the
    // result is visible against the row that was just changed.
    if (!editing) {
      // The number is cleared because it must be unique; the rest is kept, since
      // segments are usually authored in runs that share a unit and a class.
      setSegmentNumber('')
    }
  }

  const field = (label: string, value: string, set: (v: string) => void, placeholder: string, width: number, readOnly = false) => (
    <label style={{ display: 'flex', flexDirection: 'column', gap: 4, width }}>
      <span style={{ fontFamily: 'var(--font-mono)', fontSize: '8px', color: 'var(--text-muted)', letterSpacing: '0.1em' }}>{label}</span>
      <input
        value={value}
        placeholder={placeholder}
        readOnly={readOnly}
        onChange={e => set(e.target.value)}
        onKeyDown={e => { if (e.key === 'Enter') submit() }}
        style={{ background: readOnly ? 'var(--bg-surface)' : 'var(--bg-panel)', border: '1px solid var(--border-mid)', borderRadius: '3px', color: readOnly ? 'var(--text-muted)' : 'var(--text-primary)', fontFamily: 'var(--font-mono)', fontSize: '11px', padding: '5px 8px', outline: 'none' }}
      />
    </label>
  )

  return (
    <div style={{ padding: '12px 14px', borderBottom: '1px solid var(--border-subtle)', background: 'var(--bg-panel)' }}>
      {editing && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 10 }}>
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: accent, fontWeight: 700, letterSpacing: '0.1em' }}>
            EDITING {editing.tagNumber}
          </span>
          {editing.maturity === 'Published' && (
            // Worth saying before the edit, not after: this is a real state
            // change that pulls the segment back into the pending set.
            <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: '#f59e0b' }}>
              published — saving returns it to WIP for the next Named Version
            </span>
          )}
          <button
            onClick={onCancelEdit}
            style={{ marginLeft: 'auto', background: 'none', border: '1px solid var(--border-mid)', borderRadius: '3px', color: 'var(--text-secondary)', cursor: 'pointer', fontSize: '9px', fontFamily: 'var(--font-mono)', letterSpacing: '0.1em', padding: '3px 10px' }}
          >
            CANCEL
          </button>
        </div>
      )}

      <div style={{ display: 'flex', gap: 10, alignItems: 'flex-end', flexWrap: 'wrap' }}>
        {/* Read-only while editing: the number is the key the upsert matches on,
            so changing it would silently author a second segment rather than
            rename this one. */}
        {field('SEGMENT NUMBER *', segmentNumber, setSegmentNumber, 'TIC-106', 150, editing !== null)}
        {field('SERVICE DESCRIPTION', serviceDescription, setServiceDescription, 'Top temperature control', 220)}
        {field('UNIT', unitNumber, setUnitNumber, '101', 70)}
        {/* Chosen, not typed. A key ENG cannot bind produces a proposal that
            arrives at the registry unbound or degraded, which is only visible
            after publication -- far too late to be useful. */}
        <label style={{ display: 'flex', flexDirection: 'column', gap: 4, width: 300 }}>
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: '8px', color: 'var(--text-muted)', letterSpacing: '0.1em' }}>CLASS KEY</span>
          <select
            value={classKey}
            onChange={e => setClassKey(e.target.value)}
            style={{ background: 'var(--bg-panel)', border: '1px solid var(--border-mid)', borderRadius: '3px', color: classKey ? 'var(--text-primary)' : 'var(--text-muted)', fontFamily: 'var(--font-mono)', fontSize: '11px', padding: '5px 8px', outline: 'none' }}
          >
            <option value="">— none —</option>
            {/* A stored key ENG no longer holds would otherwise not be in the
                list, so the select would fall back to "none" and a save would
                silently strip it. Kept as an option, marked, so the loss is a
                choice rather than an accident. */}
            {classKey && !classes.some(c => c.key === classKey) && (
              <option value={classKey}>{classKey} (not in BIC reference data)</option>
            )}
            {classes.filter(c => !c.isAspect).map(c => (
              // Indented by depth so the taxonomy is legible: an instrument is a
              // kind of equipment, and the picker should show that rather than
              // presenting a flat list of unrelated options.
              <option key={c.key} value={c.key}>
                {`${'\u00a0\u00a0'.repeat(Math.max(0, c.chain.length - 1))}${c.name}`}
              </option>
            ))}
          </select>
        </label>
        <button
          onClick={submit}
          disabled={!canSubmit}
          title={canSubmit ? (editing ? 'Save changes to this segment' : 'Author this segment in the selected twin') : 'Enter a segment number first'}
          style={{
            background: canSubmit ? dimBg : 'transparent',
            border: `1px solid ${canSubmit ? accent : 'var(--border-mid)'}`,
            borderRadius: '3px',
            color: canSubmit ? accent : 'var(--text-muted)',
            cursor: canSubmit ? 'pointer' : 'not-allowed',
            fontFamily: 'var(--font-mono)',
            fontSize: '10px',
            fontWeight: 600,
            letterSpacing: '0.1em',
            padding: '6px 16px',
          }}
        >
          {busy ? 'SAVING…' : editing ? 'UPDATE SEGMENT' : 'CREATE SEGMENT'}
        </button>
        {/* The button is disabled until there is a segment number, which on an
            empty form looks indistinguishable from a button that does nothing. */}
        {!canSubmit && !busy && (
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', paddingBottom: 6 }}>
            enter a segment number
          </span>
        )}
        {canSubmit && willBlock.length > 0 && (
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: '#f59e0b', paddingBottom: 6 }}>
            ⚠ without {willBlock.join(' and ')} this blocks publication
          </span>
        )}
      </div>
      {error && (
        <div
          onClick={onDismissError}
          style={{ marginTop: 10, padding: '6px 10px', background: 'rgba(248,113,113,0.1)', border: '1px solid rgba(248,113,113,0.35)', borderRadius: '3px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: '#f87171', cursor: 'pointer' }}
        >
          {error}
        </div>
      )}
    </div>
  )
}

function UpdateTable({ updates, selected, onToggle, onToggleAll, accent, dimBg }: { updates: AssetUpdate[]; selected: Set<string>; onToggle: (id: string) => void; onToggleAll: () => void; accent: string; dimBg: string }) {
  const allSel = updates.length > 0 && updates.every(u => selected.has(u.id))
  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', minWidth: 760 }}>
        <thead>
          <tr style={{ borderBottom: '1px solid var(--border-mid)' }}>
            <th style={{ padding: '8px 14px', width: 36 }}><Checkbox checked={allSel} onToggle={onToggleAll} accent={accent} /></th>
            <TH>UPDATE ID</TH>
            <TH>SEGMENT UUID</TH>
            <TH>SHORT NAME</TH>
            <TH>ASSET TYPE</TH>
            <TH>SERIAL #</TH>
            <TH>INSTALLED BY</TH>
            <TH>DATE</TH>
            <TH>STATUS</TH>
          </tr>
        </thead>
        <tbody>
          {updates.length === 0 ? (
            <tr><td colSpan={9} style={{ padding: '48px', textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: '11px', color: 'var(--text-muted)' }}>
              <div style={{ opacity: 0.4, fontSize: 24, marginBottom: 8 }}>◎</div>No items in inbox
            </td></tr>
          ) : updates.map(upd => (
            <TRow key={upd.id} isSel={selected.has(upd.id)} onToggle={() => onToggle(upd.id)} dimBg={dimBg}>
              <td style={{ padding: '9px 14px' }}><Checkbox checked={selected.has(upd.id)} onToggle={() => onToggle(upd.id)} accent={accent} /></td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '11px', color: accent, fontWeight: 600 }}>{upd.id}</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)', opacity: 0.75 }}>{upd.segmentUuid.slice(0, 8)}…</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '11px', color: 'var(--text-primary)', fontWeight: 500 }}>{upd.segmentShortName}</td>
              <td style={{ padding: '9px 8px', fontSize: '11px', color: 'var(--text-secondary)' }}>{upd.assetType}</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '11px', color: 'var(--text-muted)' }}>{upd.serialNumber}</td>
              <td style={{ padding: '9px 8px', fontSize: '11px', color: 'var(--text-muted)' }}>{upd.installedBy}</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>{upd.installedAt}</td>
              <td style={{ padding: '9px 8px' }}><Pill label={UPD_STATUS_LABEL[upd.status]} color={UPD_STATUS_COLOR[upd.status]} /></td>
            </TRow>
          ))}
        </tbody>
      </table>
    </div>
  )
}

// showOm is off for SC04, where nothing has reached O&M yet and the column would
// be three empty dashes on every row.
function AsBuiltTable({ assets, selected, onToggle, onToggleAll, accent, dimBg, showOm = false }: { assets: AsBuiltAsset[]; selected: Set<string>; onToggle: (id: string) => void; onToggleAll: () => void; accent: string; dimBg: string; showOm?: boolean }) {
  const allSel = assets.length > 0 && assets.every(a => selected.has(a.uuid))
  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', minWidth: 900 }}>
        <thead>
          <tr style={{ borderBottom: '1px solid var(--border-mid)' }}>
            <th style={{ padding: '8px 14px', width: 36 }}><Checkbox checked={allSel} onToggle={onToggleAll} accent={accent} /></th>
            <TH>UUID</TH>
            <TH>EQUIP. TAG</TH>
            <TH>DESCRIPTION</TH>
            <TH>CLASS</TH>
            <TH>MANUFACTURER</TH>
            <TH>MODEL</TH>
            <TH>SERIAL #</TH>
            <TH>INSTALL DATE</TH>
            <TH>SITE</TH>
            <TH>STATUS</TH>
            {showOm && <TH>O&M UPTAKE</TH>}
          </tr>
        </thead>
        <tbody>
          {assets.length === 0 ? (
            <tr><td colSpan={showOm ? 12 : 11} style={{ padding: '48px', textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: '11px', color: 'var(--text-muted)' }}>
              <div style={{ opacity: 0.4, fontSize: 24, marginBottom: 8 }}>◎</div>No items in inbox
            </td></tr>
          ) : assets.map(a => (
            <TRow key={a.uuid} isSel={selected.has(a.uuid)} onToggle={() => onToggle(a.uuid)} dimBg={dimBg}>
              <td style={{ padding: '9px 14px' }}><Checkbox checked={selected.has(a.uuid)} onToggle={() => onToggle(a.uuid)} accent={accent} /></td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: accent, opacity: 0.7 }}>{a.uuid.slice(0, 8)}…</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '11px', color: accent, fontWeight: 600 }}>{a.equipmentTag}</td>
              <td style={{ padding: '9px 8px', fontSize: '11px', color: 'var(--text-primary)', maxWidth: 220 }}>{a.description}</td>
              <td style={{ padding: '9px 8px', fontSize: '11px', color: 'var(--text-secondary)' }}>{a.equipmentClass}</td>
              <td style={{ padding: '9px 8px', fontSize: '11px', color: 'var(--text-secondary)' }}>{a.manufacturer}</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>{a.modelNumber}</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '11px', color: 'var(--text-muted)' }}>{a.serialNumber}</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>{a.installDate}</td>
              <td style={{ padding: '9px 8px', fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>{a.installationSite}</td>
              <td style={{ padding: '9px 8px' }}><Pill label={AB_STATUS_LABEL[a.status]} color={AB_STATUS_COLOR[a.status]} /></td>
              {showOm && (
                <td style={{ padding: '9px 8px', display: 'flex', gap: 4 }}>
                  {a.omMms && <Pill label="TAMS" color="#f59e0b" />}
                  {a.omRel && <Pill label="REL" color="#a78bfa" />}
                  {a.omGis && <Pill label="ESRI" color="#34d399" />}
                  {!a.omMms && !a.omRel && !a.omGis && <span style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>—</span>}
                </td>
              )}
            </TRow>
          ))}
        </tbody>
      </table>
    </div>
  )
}

// ─── Main App ─────────────────────────────────────────────────────────────────

// A full-screen status panel, used for every pre-authenticated state so the
// unauthenticated app never flashes a partial workspace.
function AuthScreen({ title, detail, tone = 'neutral', action }: { title: string; detail: string; tone?: 'neutral' | 'error'; action?: { label: string; onClick: () => void } }) {
  const accent = tone === 'error' ? '#ef4444' : '#3b82f6'
  return (
    <div style={{ minHeight: '100vh', background: 'var(--bg-base)', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24 }}>
      <div style={{ width: 420, maxWidth: '100%', background: 'var(--bg-surface)', border: '1px solid var(--border-mid)', borderRadius: '8px', padding: '28px', textAlign: 'center' }}>
        <div style={{ width: 26, height: 26, borderRadius: '6px', background: 'linear-gradient(135deg, #3b82f6, #10b981)', margin: '0 auto 18px' }} />
        <div style={{ fontFamily: 'var(--font-mono)', fontSize: '13px', fontWeight: 700, letterSpacing: '0.1em', color: 'var(--text-primary)', marginBottom: 10 }}>
          {title}
        </div>
        <div style={{ fontFamily: 'var(--font-mono)', fontSize: '11px', color: tone === 'error' ? accent : 'var(--text-muted)', lineHeight: 1.6, wordBreak: 'break-word' }}>
          {detail}
        </div>
        {action && (
          <button
            onClick={action.onClick}
            style={{ marginTop: 20, background: 'rgba(255,255,255,0.08)', border: '1px solid var(--border-mid)', borderRadius: '4px', padding: '8px 18px', fontFamily: 'var(--font-mono)', fontSize: '11px', fontWeight: 700, letterSpacing: '0.1em', color: 'var(--text-primary)', cursor: 'pointer' }}>
            {action.label}
          </button>
        )}
      </div>
    </div>
  )
}

/**
 * The app's own signed-out screen.
 *
 * The IMS end-session call is made from here, in a hidden iframe rather than by
 * navigating: navigating would land on Bentley's signed-off page and strand the
 * user there, which is the whole problem this screen exists to avoid. The iframe
 * lets the SSO session be ended properly while the user stays on our page with a
 * way back in.
 *
 * The sign-in link is a real navigation to the app root rather than a call to
 * login(), so the visitor arrives in a clean document and goes through the
 * normal gate.
 */
function SignedOutScreen() {
  const [endSessionUrl] = useState(() => auth.takeEndSessionUrl())

  return (
    <>
      {endSessionUrl && (
        <iframe
          src={endSessionUrl}
          title="Bentley IMS sign-out"
          style={{ display: 'none' }}
        />
      )}
      <AuthScreen
        title="SIGNED OUT"
        detail="Your Bentley IMS session has ended."
        action={{ label: 'SIGN IN AGAIN', onClick: () => window.location.assign('/') }}
      />
    </>
  )
}

/**
 * Decides whether anything is rendered at all.
 *
 * The app has no offline presence: an unauthenticated visitor is sent straight to
 * the Bentley sign-in page rather than being shown a landing screen. The one
 * exception is a configuration or sign-in error, which is displayed instead of
 * redirecting -- bouncing to IMS on a failed sign-in would produce an infinite
 * redirect loop and no way to read the reason.
 */
export default function App() {
  // Checked before any auth work: this path is the post-logout landing, so it
  // must not trigger the redirect-to-IMS that the gate applies everywhere else.
  const signedOut = window.location.pathname === auth.SIGNED_OUT_PATH

  const [state, setState] = useState<auth.AuthState>({ status: 'loading' })

  useEffect(() => {
    if (signedOut) return
    let cancelled = false
    auth.initialize().then(s => { if (!cancelled) setState(s) })
    return () => { cancelled = true }
  }, [signedOut])

  useEffect(() => {
    // Redirect only from the settled unauthenticated state, never from loading,
    // so a slow token exchange cannot be interrupted by a premature navigation.
    if (!signedOut && state.status === 'unauthenticated') void auth.login()
  }, [signedOut, state.status])

  if (signedOut) {
    return <SignedOutScreen />
  }

  if (state.status === 'loading') {
    return <AuthScreen title="WORKFLOW ORCHESTRATOR" detail="Checking your Bentley IMS session…" />
  }

  if (state.status === 'unauthenticated') {
    return <AuthScreen title="SIGNING IN" detail="Redirecting to Bentley IMS…" />
  }

  if (state.status === 'error') {
    return (
      <AuthScreen
        title="SIGN-IN FAILED"
        detail={state.message}
        tone="error"
        action={auth.isConfigured() ? { label: 'TRY AGAIN', onClick: () => void auth.login() } : undefined}
      />
    )
  }

  return <Workspace user={state.user} />
}

function Workspace({ user }: { user: CurrentUser }) {
  // Twins are fetched, so there is no twin at all until the first response.
  // Null is the honest starting value; the screen renders a loading state
  // rather than pretending a plant is selected.
  const [twins, setTwins] = useState<ITwin[]>([])
  const [activeTwin, setActiveTwin] = useState<ITwin | null>(null)
  const [twinsError, setTwinsError] = useState<string | null>(null)
  const [twinsLoading, setTwinsLoading] = useState(true)
  const [workflow, setWorkflow] = useState<WorkflowId>('SC01')
  const [persona, setPersona] = useState<PersonaId>('ENG')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [segments, setSegments] = useState<Segment[]>(SEED_SEGMENTS)
  const [updates, setUpdates] = useState<AssetUpdate[]>(SEED_UPDATES)
  const [asBuilt, setAsBuilt] = useState<AsBuiltAsset[]>(SEED_ASBUILT)
  const [assetIdx, setAssetIdx] = useState(0)
  const [newAbIdx, setNewAbIdx] = useState(0)
  const [toast, setToast] = useState<{ msg: string; accent: string } | null>(null)

  // ── Real sandbox data ────────────────────────────────────────────────────
  // ENG's segments in the selected twin. Unlike the seeded arrays above, these
  // are the sandbox's own records: authoring one here is a write the rest of the
  // OIIE ecosystem can see. Named engSegments because `segments` above is the
  // seeded mock list and the two must not be confused.
  const [engSegments, setEngSegments] = useState<api.Tag[]>([])
  const [engLoading, setEngLoading] = useState(false)
  const [engError, setEngError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  // Kept apart from engError: a failed create must not blank the table that is
  // still showing perfectly good rows.
  const [createError, setCreateError] = useState<string | null>(null)
  // The last promotion outcome, released or refused. Held rather than flashed,
  // because the findings are a list of things to go and fix.
  const [promotion, setPromotion] = useState<api.PromotionResult | null>(null)
  const [promoting, setPromoting] = useState(false)
  // The segment loaded into the form for editing, by id. Held as an id rather
  // than the object so a refresh after saving re-resolves to the stored row
  // instead of pinning the form to a stale copy.
  const [editingId, setEditingId] = useState<number | null>(null)

  // REG-LOCATION's side: what ENG published, awaiting a stewardship decision.
  const [stewardship, setStewardship] = useState<api.StewardshipItem[]>([])
  const [stewardshipLoading, setStewardshipLoading] = useState(false)
  const [stewardshipError, setStewardshipError] = useState<string | null>(null)

  // ENG's reference data, offered in the class picker.
  const [engClasses, setEngClasses] = useState<api.ClassDefinition[]>([])

  // Published segments are finished work, so the table opens on what is still
  // pending and the filter is there when the rest is wanted.
  const [maturityFilter, setMaturityFilter] = useState<MaturityFilter>('Pending')

  const p = PERSONAS.find(x => x.id === persona)!
  const steps = WORKFLOW_STEPS[workflow]
  const mySteps = steps.filter(s => s.persona === persona)

  function flash(msg: string) { setToast({ msg, accent: p.accent }); setTimeout(() => setToast(null), 2800) }
  function now() { return new Date().toISOString().slice(0, 16).replace('T', ' ') }

  // ── Loading the twins ────────────────────────────────────────────────────

  useEffect(() => {
    const abort = new AbortController()

    api.listTwins(abort.signal)
      .then(found => {
        const mapped = found.map(toITwin)
        setTwins(mapped)
        // Selecting the first twin makes the app usable immediately. Left
        // unselected, every panel below would render empty and look broken.
        setActiveTwin(current => current ?? mapped[0] ?? null)
        setTwinsError(null)
      })
      .catch(err => {
        if (abort.signal.aborted) return
        setTwinsError(err instanceof Error ? err.message : String(err))
      })
      .finally(() => {
        if (!abort.signal.aborted) setTwinsLoading(false)
      })

    return () => abort.abort()
  }, [])

  // ── Loading the segments ─────────────────────────────────────────────────

  const refreshSegments = useCallback(async (signal?: AbortSignal) => {
    if (!activeTwin) {
      setEngSegments([])
      return
    }

    setEngLoading(true)

    try {
      const result = await api.listTags(activeTwin.uuid, signal)
      if (signal?.aborted) return
      setEngSegments(result.tags)
      setEngError(null)
    } catch (err) {
      if (signal?.aborted) return
      setEngError(err instanceof Error ? err.message : String(err))
      // Cleared rather than left in place: stale rows under a new twin would
      // read as that twin's design.
      setEngSegments([])
    } finally {
      if (!signal?.aborted) setEngLoading(false)
    }
  }, [activeTwin])

  useEffect(() => {
    const abort = new AbortController()
    void refreshSegments(abort.signal)
    return () => abort.abort()
  }, [refreshSegments])

  // Switching twins abandons any edit in progress. The segment being edited
  // belongs to the twin that was selected, and leaving it loaded would let a
  // save write it into the wrong iModel.
  useEffect(() => {
    setEditingId(null)
    setCreateError(null)
  }, [activeTwin?.uuid])

  // ── Loading ENG's reference data ─────────────────────────────────────────
  //
  // Fetched once: reference data is fixture-loaded per participant and does not
  // change while the app is open. A failure is not surfaced -- the picker simply
  // has nothing to offer, which the empty state explains.
  useEffect(() => {
    const abort = new AbortController()

    api.listClasses('eng', abort.signal)
      .then(setEngClasses)
      .catch(() => { /* picker renders its own empty state */ })

    return () => abort.abort()
  }, [])

  // ── Loading the stewardship queue ────────────────────────────────────────

  const refreshStewardship = useCallback(async (signal?: AbortSignal) => {
    setStewardshipLoading(true)

    try {
      const items = await api.listStewardship(signal)
      if (signal?.aborted) return
      setStewardship(items)
      setStewardshipError(null)
    } catch (err) {
      if (signal?.aborted) return
      setStewardshipError(err instanceof Error ? err.message : String(err))
      setStewardship([])
    } finally {
      if (!signal?.aborted) setStewardshipLoading(false)
    }
  }, [])

  // Loaded when the REG persona is being viewed rather than on mount, so the
  // queue is re-read on arrival instead of showing whatever it held when the
  // page was opened. Segments published since would otherwise be missing.
  useEffect(() => {
    if (persona !== 'REG') return

    const abort = new AbortController()
    void refreshStewardship(abort.signal)
    return () => abort.abort()
  }, [persona, refreshStewardship])

  // ── Authoring and editing a segment ──────────────────────────────────────

  // Resolved from the current list, so it follows a refresh rather than holding
  // the copy that was on screen when the row was clicked.
  const editingSegment = engSegments.find(s => s.id === editingId) ?? null

  const visibleSegments = engSegments.filter(s => matchesFilter(s, maturityFilter))

  async function saveSegment(segment: api.NewTag) {
    if (!activeTwin) return

    const isEdit = editingId !== null

    setBusy(true)
    setCreateError(null)

    try {
      // The same call either way: POST /admin/eng/tags is an upsert keyed on the
      // segment number within the twin, so an edit is a write of the whole
      // record rather than a separate endpoint.
      const saved = await api.createTag(activeTwin.uuid, segment)
      // Re-read rather than merging the response: the server assigns the
      // federation id, derives the P&ID reference, and resets maturity, so the
      // list should reflect what was actually stored.
      await refreshSegments()
      flash(
        isEdit
          ? `Segment ${saved.tagNumber} updated — now ${MATURITY_LABEL[saved.maturity]}`
          : `Segment ${saved.tagNumber} authored in ${activeTwin.shortName}`,
      )
    } catch (err) {
      // Shown in the form, not as a toast: a duplicate segment number needs the
      // user to change what they typed, so the message belongs beside the input
      // and must not disappear after three seconds.
      setCreateError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  // ── Publishing the design ────────────────────────────────────────────────

  async function publishDesign(name: string) {
    if (!activeTwin) return

    setPromoting(true)
    setPromotion(null)

    try {
      const result = await api.promote(activeTwin.uuid, name)
      setPromotion(result)

      if (result.released) {
        // Maturity changes on every published segment, so the table is stale
        // until it is re-read.
        await refreshSegments()
        flash(`Released "${result.name}" — ${result.tagCount} segment(s) published`)
      }
      // A refusal is not flashed: the panel keeps the findings on screen, and a
      // toast that vanished would take the only explanation with it.
    } catch (err) {
      // Reaching here means the call itself failed rather than being refused --
      // a refusal comes back as a result. Surfaced in the same panel so there is
      // one place to look.
      setPromotion({
        released: false,
        namedVersionId: 0,
        name,
        tagCount: 0,
        findings: [err instanceof Error ? err.message : String(err)],
      })
    } finally {
      setPromoting(false)
    }
  }

  // ── Inbox items per persona × workflow ───────────────────────────────────

  function inboxAsBuilt(): AsBuiltAsset[] {
    if (!activeTwin) return []
    const tw = activeTwin.shortName
    if (workflow === 'SC05') {
      // SC05 starts from SC04's finish line: AR only offers what it has actually
      // registered, and the O&M systems only see what AR has released.
      const registered = asBuilt.filter(a => a.installationSite === tw && (a.status === 'registered' || a.status === 'om_published'))
      if (persona === 'REG_ASSET') return registered
      if (persona === 'MMS' || persona === 'RELIABILITY' || persona === 'GIS') return registered.filter(a => a.status === 'om_published')
      return []
    }
    if (workflow !== 'SC04') return []
    if (persona === 'ENG') return asBuilt.filter(a => a.installationSite === tw)
    if (persona === 'CONSTRUCT') return asBuilt.filter(a => a.installationSite === tw && (a.status === 'eng_published' || a.status === 'construct_published'))
    if (persona === 'REG_ASSET') return asBuilt.filter(a => a.installationSite === tw && (a.status === 'construct_published' || a.status === 'registered' || a.status === 'om_published'))
    return []
  }

  function inboxSegments(): Segment[] {
    if (workflow !== 'SC01' || !activeTwin) return []
    const tw = activeTwin.shortName
    if (persona === 'ENG') return segments.filter(s => s.registrationSite === tw)
    if (persona === 'REG') return segments.filter(s => s.registrationSite === tw && (s.status === 'published' || s.status === 'validated'))
    if (persona === 'MMS') return segments.filter(s => s.registrationSite === tw && (s.status === 'approved' || s.storedMms))
    if (persona === 'GIS') return segments.filter(s => s.registrationSite === tw && (s.status === 'approved' || s.storedGis))
    if (persona === 'RELIABILITY') return segments.filter(s => s.registrationSite === tw && (s.status === 'approved' || s.storedRel))
    return []
  }

  function inboxUpdates(): AssetUpdate[] {
    if (workflow !== 'SC11' || !activeTwin) return []
    const tw = activeTwin.shortName
    if (persona === 'MMS') return updates.filter(u => u.iTwinShortName === tw)
    if (persona === 'RELIABILITY') return updates.filter(u => u.iTwinShortName === tw && u.status !== 'pending')
    if (persona === 'REG') return updates.filter(u => u.iTwinShortName === tw && (u.status === 'published' || u.status === 'rel_updated' || u.status === 'reg_updated'))
    return []
  }

  const usesUpdates = workflow === 'SC11'
  const usesAsBuilt = workflow === 'SC04' || workflow === 'SC05'

  // Segments are ENG's, and only SC01 concerns the design leg.
  const showSegments = workflow === 'SC01' && persona === 'ENG'

  // The registry's queue, shown wherever REG-LOCATION is the persona: it is the
  // receiving end of SC01 and the starting point of SC02.
  const showStewardship = (workflow === 'SC01' || workflow === 'SC02') && persona === 'REG'
  const ibSegs = inboxSegments()
  const ibUpds = inboxUpdates()
  const ibAb = inboxAsBuilt()
  const ibItems: { id: string }[] = usesUpdates ? ibUpds.map(u => ({ id: u.id })) : usesAsBuilt ? ibAb.map(a => ({ id: a.uuid })) : ibSegs.map(s => ({ id: s.uuid }))
  const selCount = ibItems.filter(x => selected.has(x.id)).length

  function toggleItem(id: string) {
    setSelected(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n })
  }
  function toggleAll() {
    const all = ibItems.every(x => selected.has(x.id))
    setSelected(() => { const n = new Set<string>(); if (!all) ibItems.forEach(x => n.add(x.id)); return n })
  }

  // ── SC01 actions ─────────────────────────────────────────────────────────

  function handleSC01(action: string) {
    if (!activeTwin) return
    const selIds = [...selected]

    // The mock CREATE SEGMENT action is gone: authoring is real now and happens
    // in the ENG SEGMENTS panel against /admin/eng/tags. A button that appended a
    // templated row to a separate mock grid, directly above a table of the
    // sandbox's actual records, made it look as though authoring had two
    // different and disagreeing homes.

    // PUBLISH DESIGN is gone from here too, for the same reason: it is real now
    // and lives in the PUBLISH DESIGN panel, which promotes a named version
    // through /admin/eng/promote rather than flipping a status on mock rows.

    if (action === 'VALIDATE') {
      const ids = selIds.filter(id => segments.find(s => s.uuid === id)?.status === 'published')
      if (!ids.length) { flash('Select published segments to validate'); return }
      setSegments(prev => prev.map(s => ids.includes(s.uuid) ? { ...s, status: 'validated' } : s))
      setSelected(new Set())
      flash(`${ids.length} segment(s) validated`)
      return
    }

    if (action === 'APPROVE') {
      const ids = selIds.filter(id => segments.find(s => s.uuid === id)?.status === 'validated')
      if (!ids.length) { flash('Select validated segments to approve'); return }
      setSegments(prev => prev.map(s => ids.includes(s.uuid) ? { ...s, status: 'approved' } : s))
      setSelected(new Set())
      flash(`${ids.length} segment(s) approved → TAMS, ESRI & Reliability`)
      return
    }

    if (action === 'STORE ASSETS') {
      const ids = selIds.filter(id => segments.find(s => s.uuid === id)?.status === 'approved')
      if (!ids.length) { flash('Select approved segments to store'); return }
      // Explicit per-persona flags: this was an if/else that treated anything
      // not MMS as Reliability, so a third O&M consumer would silently have
      // written to REL's records.
      const storeField = persona === 'MMS' ? 'storedMms' : persona === 'GIS' ? 'storedGis' : 'storedRel'
      setSegments(prev => prev.map(s => ids.includes(s.uuid) ? { ...s, [storeField]: true } : s))
      setSelected(new Set())
      flash(`${ids.length} segment(s) stored as assets`)
      return
    }
  }

  // ── SC11 actions ─────────────────────────────────────────────────────────

  function handleSC11(action: string) {
    if (!activeTwin) return
    const selIds = [...selected]

    if (action === 'INSTALL ASSET') {
      const approvedSegs = segments.filter(s => s.status === 'approved')
      if (!approvedSegs.length) { flash('No approved segments available'); return }
      const tmpl = ASSET_TEMPLATES[assetIdx % ASSET_TEMPLATES.length]
      setAssetIdx(i => i + 1)
      const seg = approvedSegs[0]
      const id = `UPD-${String(updCounter++).padStart(3, '0')}`
      setUpdates(prev => [...prev, { id, segmentUuid: seg.uuid, segmentShortName: seg.shortName, iTwinShortName: activeTwin.shortName, assetType: tmpl.assetType, serialNumber: tmpl.serial + Math.floor(10000 + Math.random() * 90000), installedBy: 'Current User', installedAt: now(), status: 'pending', relUpdated: false, regUpdated: false }])
      flash(`${id} — ${tmpl.assetType} installed on ${seg.shortName}`)
      return
    }

    if (action === 'PUBLISH UPDATE') {
      const ids = selIds.filter(id => updates.find(u => u.id === id)?.status === 'pending')
      if (!ids.length) { flash('Select pending updates to publish'); return }
      setUpdates(prev => prev.map(u => ids.includes(u.id) ? { ...u, status: 'published' } : u))
      setSelected(new Set())
      flash(`${ids.length} update(s) published → RELIABILITY & REG`)
      return
    }

    if (action === 'UPDATE RECORDS') {
      if (persona === 'RELIABILITY') {
        const ids = selIds.filter(id => updates.find(u => u.id === id)?.status === 'published')
        if (!ids.length) { flash('Select published updates to apply'); return }
        setUpdates(prev => prev.map(u => ids.includes(u.id) ? { ...u, status: 'rel_updated', relUpdated: true } : u))
        setSelected(new Set())
        flash(`${ids.length} record(s) updated with fitted asset`)
      } else if (persona === 'REG') {
        const ids = selIds.filter(id => { const u = updates.find(u => u.id === id); return u && (u.status === 'published' || u.status === 'rel_updated') })
        if (!ids.length) { flash('Select published updates to apply'); return }
        setUpdates(prev => prev.map(u => ids.includes(u.id) ? { ...u, status: 'reg_updated', regUpdated: true } : u))
        setSelected(new Set())
        flash(`${ids.length} segment record(s) updated`)
      }
      return
    }
  }

  function handleSC04(action: string) {
    if (!activeTwin) return
    const selIds = [...selected]

    if (action === 'PUBLISH AS-BUILT') {
      const ids = selIds.filter(id => asBuilt.find(a => a.uuid === id)?.status === 'draft')
      if (!ids.length) { flash('Select draft assets to publish'); return }
      setAsBuilt(prev => prev.map(a => ids.includes(a.uuid) ? { ...a, status: 'eng_published' } : a))
      setSelected(new Set())
      flash(`${ids.length} as-built record(s) published → CONSTRUCT`)
      return
    }

    if (action === 'CREATE AS-BUILT') {
      const tmpl = NEW_ASBUILT_TEMPLATES[newAbIdx % NEW_ASBUILT_TEMPLATES.length]
      setNewAbIdx(i => i + 1)
      const uuid = makeUuid()
      const serial = `SN-${tmpl.equipmentTag.replace(/[^A-Z0-9]/g, '')}-${Math.floor(10000 + Math.random() * 90000)}`
      setAsBuilt(prev => [...prev, { uuid, equipmentTag: tmpl.equipmentTag, description: tmpl.description, equipmentClass: tmpl.equipmentClass, manufacturer: tmpl.manufacturer, modelNumber: tmpl.modelNumber, serialNumber: serial, installationSite: activeTwin.shortName, installDate: now().slice(0, 10), created: now(), status: 'draft', registeredOm: false, omMms: false, omRel: false, omGis: false }])
      flash(`As-built record ${tmpl.equipmentTag} created`)
      return
    }

    if (action === 'PUBLISH ASSET') {
      const ids = selIds.filter(id => asBuilt.find(a => a.uuid === id)?.status === 'eng_published')
      if (!ids.length) { flash('Select ENG-published assets to publish'); return }
      setAsBuilt(prev => prev.map(a => ids.includes(a.uuid) ? { ...a, status: 'construct_published' } : a))
      setSelected(new Set())
      flash(`${ids.length} asset(s) published → REG-ASSET`)
      return
    }

    if (action === 'REGISTER ASSET') {
      const ids = selIds.filter(id => asBuilt.find(a => a.uuid === id)?.status === 'construct_published')
      if (!ids.length) { flash('Select published assets to register'); return }
      setAsBuilt(prev => prev.map(a => ids.includes(a.uuid) ? { ...a, status: 'registered', registeredOm: true } : a))
      setSelected(new Set())
      flash(`${ids.length} asset(s) registered in O&M Registry`)
      return
    }
  }

  // SC05 — AR releases registered assets, then each O&M system takes them up.
  // All three consumers share one STORE ASSETS action and the persona decides
  // which flag it sets, mirroring how SC02 handles its three O&M consumers.
  function handleSC05(action: string) {
    if (!activeTwin) return
    const selIds = [...selected]

    if (action === 'PUBLISH TO O&M') {
      const ids = selIds.filter(id => asBuilt.find(a => a.uuid === id)?.status === 'registered')
      if (!ids.length) { flash('Select registered assets to publish'); return }
      setAsBuilt(prev => prev.map(a => ids.includes(a.uuid) ? { ...a, status: 'om_published' } : a))
      setSelected(new Set())
      flash(`${ids.length} asset(s) published → O&M`)
      return
    }

    if (action === 'STORE ASSETS') {
      const ids = selIds.filter(id => asBuilt.find(a => a.uuid === id)?.status === 'om_published')
      if (!ids.length) { flash('Select published assets to store'); return }
      const key = persona === 'MMS' ? 'omMms' : persona === 'RELIABILITY' ? 'omRel' : 'omGis'
      setAsBuilt(prev => prev.map(a => ids.includes(a.uuid) ? { ...a, [key]: true } : a))
      setSelected(new Set())
      flash(`${ids.length} asset(s) stored in ${personaAlias(persona)}`)
      return
    }
  }

  function handleAction(action: string) {
    // SC02 continues the same segment lifecycle SC01 starts, so the mock inbox
    // transitions live in the one handler rather than being duplicated.
    if (workflow === 'SC01' || workflow === 'SC02') handleSC01(action)
    else if (workflow === 'SC11') handleSC11(action)
    else if (workflow === 'SC05') handleSC05(action)
    else handleSC04(action)
  }

  // Ordered by where the data travels, because WORKFLOW_PERSONAS is derived from
  // the step order, so the sidebar reads in the same direction as the workflow.
  const visiblePersonas = WORKFLOW_PERSONAS[workflow]
    .map(id => PERSONAS.find(p => p.id === id)!)

  function inboxLabel() {
    if (workflow === 'SC01') {
      if (persona === 'ENG') return 'ALL SEGMENTS · AUTHORED BY BIC'
      return 'SEGMENTS PENDING REVIEW FROM BIC'
    }
    if (workflow === 'SC02') {
      if (persona === 'REG') return 'PROPOSALS AWAITING STEWARDSHIP DECISION'
      if (persona === 'MMS') return 'APPROVED LOCATIONS · TAMS INBOX'
      if (persona === 'GIS') return 'APPROVED LOCATIONS · ESRI INBOX'
      return 'SEGMENT DATA · RELIABILITY INBOX'
    }
    if (workflow === 'SC04') {
      if (persona === 'ENG') return 'AS-BUILT ASSETS · AUTHORED BY BIC'
      if (persona === 'CONSTRUCT') return 'AS-BUILT DATA FROM BIC · SYNCHRO INBOX'
      return 'CONSTRUCTED ASSETS · AR INBOX'
    }
    if (persona === 'MMS') return 'ASSET UPDATES · TAMS AUTHORED'
    return 'INCOMING ASSET UPDATES FROM TAMS'
  }

  function sidebarCount(px: typeof PERSONAS[0]): number {
    if (workflow === 'SC01' || workflow === 'SC02') {
      if (px.id === 'ENG') return segments.length
      if (px.id === 'REG') return segments.filter(s => s.status === 'published' || s.status === 'validated').length
      if (px.id === 'MMS') return segments.filter(s => s.status === 'approved' || s.storedMms).length
      if (px.id === 'GIS') return segments.filter(s => s.status === 'approved' || s.storedGis).length
      return segments.filter(s => s.status === 'approved' || s.storedRel).length
    }
    if (workflow === 'SC05') {
      const registered = asBuilt.filter(a => a.status === 'registered' || a.status === 'om_published')
      if (px.id === 'REG_ASSET') return registered.length
      return registered.filter(a => a.status === 'om_published').length
    }
    if (workflow === 'SC04') {
      if (px.id === 'ENG') return asBuilt.length
      if (px.id === 'CONSTRUCT') return asBuilt.filter(a => a.status === 'eng_published' || a.status === 'construct_published').length
      return asBuilt.filter(a => a.status === 'construct_published' || a.status === 'registered' || a.status === 'om_published').length
    }
    if (px.id === 'MMS') return updates.length
    if (px.id === 'RELIABILITY') return updates.filter(u => u.status !== 'pending').length
    return updates.filter(u => u.status === 'published' || u.status === 'rel_updated' || u.status === 'reg_updated').length
  }

  return (
    <div style={{ minHeight: '100vh', background: 'var(--bg-base)', display: 'flex', flexDirection: 'column' }}>
      {/* ── Header ────────────────────────────────────────────────────────── */}
      <header style={{ background: 'var(--bg-surface)', borderBottom: '1px solid var(--border-subtle)', padding: '0 32px', display: 'flex', alignItems: 'center', justifyContent: 'space-between', height: 52, flexShrink: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
          <div style={{ width: 18, height: 18, borderRadius: '4px', background: 'linear-gradient(135deg, #3b82f6, #10b981)', flexShrink: 0 }} />
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: '13px', fontWeight: 700, letterSpacing: '0.12em', color: 'var(--text-primary)' }}>WORKFLOW ORCHESTRATOR</span>
          <span style={{ color: 'var(--border-mid)', fontSize: '14px' }}>|</span>
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.1em' }}>MULTI-APP PIPELINE v2.4.1</span>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.1em', marginRight: 4 }}>WORKFLOW</span>
          {(['SC01', 'SC02', 'SC04', 'SC05', 'SC11'] as WorkflowId[]).map(wf => (
            <button
              key={wf}
              onClick={() => {
                setWorkflow(wf)
                setSelected(new Set())
                // Each workflow has its own cast, so a persona that does not
                // appear in the one being opened would leave an empty screen.
                // Falling back to the first participant in the flow keeps the
                // view meaningful, and derives from WORKFLOW_PERSONAS rather
                // than a chain of conditionals that must be extended by hand
                // every time a workflow is added.
                if (!WORKFLOW_PERSONAS[wf].includes(persona)) {
                  setPersona(WORKFLOW_PERSONAS[wf][0])
                }
              }}
              style={{ background: workflow === wf ? 'rgba(255,255,255,0.08)' : 'transparent', border: `1px solid ${workflow === wf ? 'var(--border-mid)' : 'var(--border-subtle)'}`, borderRadius: '4px', padding: '5px 14px', fontFamily: 'var(--font-mono)', fontSize: '11px', fontWeight: 700, letterSpacing: '0.1em', color: workflow === wf ? 'var(--text-primary)' : 'var(--text-muted)', cursor: 'pointer', transition: 'all 0.13s' }}>
              {wf}
            </button>
          ))}
        </div>

        {/* Description and identity share the right-hand block so the header
            keeps its three-part balance: brand, workflow switch, context. */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 16, justifyContent: 'flex-end' }}>
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)', letterSpacing: '0.06em', maxWidth: 360, textAlign: 'right', lineHeight: 1.4 }}>
            {WORKFLOW_DESCRIPTION[workflow]}
          </span>
          <UserMenu user={user} />
        </div>
      </header>

      {/* ── iTwin context bar ──────────────────────────────────────────────── */}
      <div style={{ background: 'var(--bg-panel)', borderBottom: '1px solid var(--border-subtle)', padding: '0 32px', display: 'flex', alignItems: 'center', gap: 0, height: 40, flexShrink: 0 }}>
        <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.14em', marginRight: 16, flexShrink: 0 }}>iTwin CONTEXT</span>
        {twinsLoading && (
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>loading twins…</span>
        )}
        {!twinsLoading && twinsError && (
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', color: '#f87171' }}>{twinsError}</span>
        )}
        {!twinsLoading && !twinsError && twins.length === 0 && (
          // A twin is created implicitly by the first write that names one, so an
          // empty registry is a normal state on a fresh database rather than a
          // fault. Saying so beats an empty bar that reads as a broken screen.
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', color: 'var(--text-muted)' }}>
            no twins registered — create one via POST /admin/eng/twins
          </span>
        )}
        {twins.map((tw, i) => {
          const isActive = activeTwin?.uuid === tw.uuid
          return (
            <button
              key={tw.uuid}
              onClick={() => { setActiveTwin(tw); setSelected(new Set()) }}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 8,
                padding: '0 16px',
                height: '100%',
                background: isActive ? 'rgba(255,255,255,0.05)' : 'transparent',
                borderTop: 'none',
                borderBottom: isActive ? '2px solid #3b82f6' : '2px solid transparent',
                borderLeft: i === 0 ? '1px solid var(--border-subtle)' : 'none',
                borderRight: '1px solid var(--border-subtle)',
                cursor: 'pointer',
                transition: 'all 0.13s',
              }}
            >
              <div style={{ width: 6, height: 6, borderRadius: '50%', background: isActive ? '#3b82f6' : 'var(--text-muted)', flexShrink: 0, boxShadow: isActive ? '0 0 6px #3b82f6' : 'none', transition: 'all 0.13s' }} />
              <div style={{ textAlign: 'left' }}>
                <div style={{ fontFamily: 'var(--font-mono)', fontSize: '11px', fontWeight: 600, color: isActive ? 'var(--text-primary)' : 'var(--text-secondary)', letterSpacing: '0.06em' }}>{tw.shortName}</div>
                <div style={{ fontFamily: 'var(--font-mono)', fontSize: '8px', color: 'var(--text-muted)', letterSpacing: '0.04em' }}>{tw.uuid.slice(0, 18)}…</div>
              </div>
            </button>
          )
        })}
        {activeTwin && (
          <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 8 }}>
            <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.06em' }}>ACTIVE:</span>
            <span style={{ fontFamily: 'var(--font-mono)', fontSize: '10px', color: '#3b82f6', fontWeight: 600 }}>{activeTwin.fullName}</span>
          </div>
        )}
      </div>

      <div style={{ display: 'flex', flex: 1, overflow: 'hidden' }}>
        {/* ── Sidebar ───────────────────────────────────────────────────── */}
        <nav style={{ width: 200, background: 'var(--bg-surface)', borderRight: '1px solid var(--border-subtle)', display: 'flex', flexDirection: 'column', padding: '20px 0', flexShrink: 0 }}>
          <div style={{ padding: '0 18px 14px', fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.14em' }}>PERSONAS</div>
          {visiblePersonas.map(px => {
            const isActive = persona === px.id
            return (
              <button key={px.id} onClick={() => { setPersona(px.id); setSelected(new Set()) }}
                style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '11px 18px', background: isActive ? px.dimBg : 'transparent', borderTop: 'none', borderRight: 'none', borderBottom: 'none', borderLeft: `3px solid ${isActive ? px.accent : 'transparent'}`, cursor: 'pointer', width: '100%', textAlign: 'left', transition: 'all 0.13s' }}>
                <div>
                  <div style={{ fontFamily: 'var(--font-mono)', fontSize: '11px', fontWeight: 600, color: isActive ? px.accent : 'var(--text-secondary)', letterSpacing: '0.08em', marginBottom: 2 }}>{px.alias}</div>
                  <div style={{ fontSize: '10px', color: 'var(--text-muted)', lineHeight: 1.3 }}>{px.aliasFull}</div>
                </div>
                <span style={{ background: isActive ? px.accent + '33' : 'var(--bg-hover)', color: isActive ? px.accent : 'var(--text-muted)', borderRadius: '3px', padding: '1px 6px', fontFamily: 'var(--font-mono)', fontSize: '10px', fontWeight: 600, minWidth: 22, textAlign: 'center' }}>
                  {sidebarCount(px)}
                </span>
              </button>
            )
          })}

          <div style={{ marginTop: 'auto', padding: '16px 18px 0', borderTop: '1px solid var(--border-subtle)' }}>
            <div style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.12em', marginBottom: 10 }}>STEPS IN {workflow}</div>
            {steps.map((step, i) => {
              const color = PERSONA_COLOR[step.persona]
              return (
                <div key={i} style={{ display: 'flex', alignItems: 'flex-start', gap: 8, marginBottom: 6, opacity: step.persona === persona ? 1 : 0.4 }}>
                  <div style={{ fontFamily: 'var(--font-mono)', fontSize: '8px', color, fontWeight: 700, minWidth: 14, marginTop: 1 }}>{step.num}</div>
                  <div>
                    <div style={{ fontFamily: 'var(--font-mono)', fontSize: '8px', color, letterSpacing: '0.06em' }}>{personaAlias(step.persona)}</div>
                    <div style={{ fontSize: '9px', color: 'var(--text-muted)', lineHeight: 1.3 }}>{step.label}</div>
                  </div>
                </div>
              )
            })}
          </div>
        </nav>

        {/* ── Main ──────────────────────────────────────────────────────── */}
        <main style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
          <div style={{ padding: '16px 28px', background: 'var(--bg-surface)', borderBottom: `1px solid ${p.borderColor}`, display: 'flex', alignItems: 'center', gap: 20, flexShrink: 0 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
              <div style={{ width: 8, height: 8, borderRadius: '50%', background: p.accent, boxShadow: `0 0 7px ${p.accent}` }} />
              <span title={`${p.label} — ${p.fullLabel}`} style={{ fontFamily: 'var(--font-mono)', fontSize: '13px', fontWeight: 700, color: p.accent, letterSpacing: '0.12em' }}>{p.alias}</span>
              <span style={{ fontSize: '12px', color: 'var(--text-muted)' }}>— {p.aliasFull}</span>
            </div>
            <div style={{ marginLeft: 'auto' }}>
              <PipelineBanner steps={steps} activePersona={persona} />
            </div>
          </div>

          <div style={{ flex: 1, overflow: 'auto', padding: '24px 28px', display: 'flex', flexDirection: 'column', gap: 20 }}>
            {mySteps.length > 0 && (
              <section>
                <div style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.14em', marginBottom: 10 }}>
                  WORKFLOW STEPS — {p.alias}
                </div>
                <StepsPanel steps={steps} persona={persona} accent={p.accent} dimBg={p.dimBg} borderColor={p.borderColor} onAction={handleAction} selectedCount={selCount} />
              </section>
            )}

            {/* ── REG-LOCATION stewardship queue: real sandbox data ───────── */}
            {showStewardship && (
              <section>
                <div style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.14em', marginBottom: 10, display: 'flex', alignItems: 'center', gap: 10 }}>
                  <span>STEWARDSHIP QUEUE</span>
                  <span style={{ background: p.dimBg, color: p.accent, padding: '1px 8px', borderRadius: '3px', fontWeight: 600 }}>
                    {stewardship.filter(s => s.state === 'Proposed').length} PROPOSED
                  </span>
                  <span style={{ color: 'var(--text-muted)', fontSize: '8px', letterSpacing: '0.1em', marginLeft: 4 }}>
                    LIVE — GET /admin/reg-location/stewardship
                  </span>
                  {stewardship.some(s => s.classDegraded || s.propertiesUnmapped > 0) && (
                    <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: '#f59e0b', fontWeight: 700, letterSpacing: '0.06em' }}>
                      ⚠ {stewardship.filter(s => s.classDegraded || s.propertiesUnmapped > 0).length} WITH FIDELITY LOSS
                    </span>
                  )}
                  <button
                    onClick={() => void refreshStewardship()}
                    disabled={stewardshipLoading}
                    style={{ marginLeft: 'auto', background: 'none', border: '1px solid var(--border-mid)', borderRadius: '3px', color: 'var(--text-secondary)', cursor: stewardshipLoading ? 'default' : 'pointer', fontSize: '9px', fontFamily: 'var(--font-mono)', letterSpacing: '0.1em', padding: '3px 10px', opacity: stewardshipLoading ? 0.5 : 1 }}
                  >
                    REFRESH
                  </button>
                </div>
                {/* The queue is registry-wide, not twin-scoped: the endpoint takes
                    no iTwin, and proposals carry the sender's identifier rather
                    than a twin. Said plainly so the iTwin selector above is not
                    read as filtering this table. */}
                <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: 10, lineHeight: 1.5 }}>
                  Everything ENG has published, across all iTwins. Arrival is not acceptance —
                  these are proposals until a steward approves them.
                </div>
                <div style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-subtle)', borderRadius: '6px', overflow: 'hidden' }}>
                  <StewardshipTable items={stewardship} accent={p.accent} loading={stewardshipLoading} error={stewardshipError} />
                </div>
              </section>
            )}

            {/* ── ENG segments: real sandbox data ──────────────────────────
                Kept as its own section rather than folded into the inbox
                below. The inbox is still seeded sample data, and mixing the
                two would make it impossible to tell, on screen, which rows
                the sandbox actually holds. */}
            {showSegments && (
              <section>
                <div style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.14em', marginBottom: 10, display: 'flex', alignItems: 'center', gap: 10 }}>
                  <span>BIC SEGMENTS</span>
                  <span style={{ background: p.dimBg, color: p.accent, padding: '1px 8px', borderRadius: '3px', fontWeight: 600 }}>
                    {visibleSegments.length} OF {engSegments.length}
                  </span>
                  <span style={{ color: 'var(--text-muted)', fontSize: '8px', letterSpacing: '0.1em', marginLeft: 4 }}>
                    LIVE — GET /admin/eng/tags
                  </span>
                  {/* Counted across the whole twin, not the filtered view: a
                      blocking segment stops the Named Version whether or not it
                      happens to be on screen. */}
                  {engSegments.some(s => gateFindings(s).length > 0) && (
                    <span style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: '#f59e0b', fontWeight: 700, letterSpacing: '0.06em' }}>
                      ⚠ {engSegments.filter(s => gateFindings(s).length > 0).length} BLOCKING PUBLICATION
                    </span>
                  )}
                  <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 4 }}>
                    {MATURITY_FILTERS.map(f => (
                      <button
                        key={f}
                        onClick={() => setMaturityFilter(f)}
                        style={{
                          background: maturityFilter === f ? p.dimBg : 'transparent',
                          border: `1px solid ${maturityFilter === f ? p.accent : 'var(--border-subtle)'}`,
                          borderRadius: '3px',
                          color: maturityFilter === f ? p.accent : 'var(--text-muted)',
                          cursor: 'pointer',
                          fontSize: '9px',
                          fontFamily: 'var(--font-mono)',
                          letterSpacing: '0.1em',
                          padding: '3px 10px',
                        }}
                      >
                        {f.toUpperCase()}
                      </button>
                    ))}
                    <button
                      onClick={() => void refreshSegments()}
                      disabled={engLoading}
                      style={{ marginLeft: 6, background: 'none', border: '1px solid var(--border-mid)', borderRadius: '3px', color: 'var(--text-secondary)', cursor: engLoading ? 'default' : 'pointer', fontSize: '9px', fontFamily: 'var(--font-mono)', letterSpacing: '0.1em', padding: '3px 10px', opacity: engLoading ? 0.5 : 1 }}
                    >
                      REFRESH
                    </button>
                  </div>
                </div>
                <div style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-subtle)', borderRadius: '6px', overflow: 'hidden' }}>
                  <SegmentForm
                    accent={p.accent}
                    dimBg={p.dimBg}
                    busy={busy}
                    error={createError}
                    editing={editingSegment}
                    classes={engClasses}
                    onSubmit={segment => void saveSegment(segment)}
                    onDismissError={() => setCreateError(null)}
                    onCancelEdit={() => { setEditingId(null); setCreateError(null) }}
                  />
                  <SegmentTableLive
                    segments={visibleSegments}
                    accent={p.accent}
                    dimBg={p.dimBg}
                    loading={engLoading}
                    error={engError}
                    selectedId={editingId}
                    // Clicking the row already loaded closes the editor, so the
                    // same click both opens and dismisses.
                    onSelect={seg => {
                      setEditingId(current => (current === seg.id ? null : seg.id))
                      setCreateError(null)
                    }}
                  />
                </div>
              </section>
            )}

            {/* ── Publish design: promote a named version ─────────────────── */}
            {showSegments && (
              <PublishDesignPanel
                accent={p.accent}
                dimBg={p.dimBg}
                // Promotion gathers everything not yet Published, so the count
                // is derived from maturity rather than from row selection.
                pendingCount={engSegments.filter(s => s.maturity !== 'Published').length}
                busy={promoting}
                result={promotion}
                onPromote={name => void publishDesign(name)}
                onDismiss={() => setPromotion(null)}
              />
            )}

            <section style={{ flex: 1 }}>
              <div style={{ fontFamily: 'var(--font-mono)', fontSize: '9px', color: 'var(--text-muted)', letterSpacing: '0.14em', marginBottom: 10, display: 'flex', alignItems: 'center', gap: 10 }}>
                <span>INBOX</span>
                <span style={{ background: p.dimBg, color: p.accent, padding: '1px 8px', borderRadius: '3px', fontWeight: 600 }}>
                  {usesUpdates ? ibUpds.length : usesAsBuilt ? ibAb.length : ibSegs.length} ITEMS
                </span>
                <span style={{ color: 'var(--text-muted)', fontSize: '8px', letterSpacing: '0.1em', marginLeft: 4 }}>{inboxLabel()}</span>
                {selCount > 0 && (
                  <span style={{ marginLeft: 'auto', fontFamily: 'var(--font-mono)', fontSize: '10px', color: p.accent, fontWeight: 600 }}>
                    {selCount} SELECTED
                    <button onClick={() => setSelected(new Set())} style={{ marginLeft: 10, background: 'none', border: 'none', color: 'var(--text-muted)', cursor: 'pointer', fontSize: '10px', fontFamily: 'var(--font-mono)' }}>CLEAR</button>
                  </span>
                )}
              </div>
              <div style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-subtle)', borderRadius: '6px', overflow: 'hidden' }}>
                {usesUpdates
                  ? <UpdateTable updates={ibUpds} selected={selected} onToggle={toggleItem} onToggleAll={toggleAll} accent={p.accent} dimBg={p.dimBg} />
                  : usesAsBuilt
                  ? <AsBuiltTable assets={ibAb} selected={selected} onToggle={toggleItem} onToggleAll={toggleAll} accent={p.accent} dimBg={p.dimBg} showOm={workflow === 'SC05'} />
                  : <SegmentTable segments={ibSegs} selected={selected} onToggle={toggleItem} onToggleAll={toggleAll} accent={p.accent} dimBg={p.dimBg} />
                }
              </div>
            </section>
          </div>
        </main>
      </div>

      {toast && <Toast message={toast.msg} accent={toast.accent} />}
      <style>{`@keyframes fadeInUp { from { opacity:0; transform:translateX(-50%) translateY(8px) } to { opacity:1; transform:translateX(-50%) translateY(0) } }`}</style>
    </div>
  )
}
