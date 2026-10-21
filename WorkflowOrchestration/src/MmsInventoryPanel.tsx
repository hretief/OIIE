import type { MmsInventory } from './api'

export default function MmsInventoryPanel({ inventory, loading, error, hasTwin, onRefresh }: {
  inventory: MmsInventory | null
  loading: boolean
  error: string | null
  hasTwin: boolean
  onRefresh: () => void
}) {
  const headings = ['MMS ID', 'LIGHT UNIT NAME', 'FEDERATION ID', 'OWNER ID', 'LIGHT SYSTEM ID', 'UPDATED']
  const cell = { padding: '12px 14px', textAlign: 'left' as const, fontSize: '11px', borderBottom: '1px solid var(--border-subtle)' }
  const empty = !hasTwin ? 'Select an iTwin to see its MMS inventory.'
    : loading && !inventory ? 'Loading MMS inventory...'
    : inventory && !inventory.siteMapped ? 'No ITWIN-SITE/MMS owner mapping exists for this iTwin. Site ingestion must register it before assets can be shown.'
    : 'No persisted MMS light units for this iTwin. Approval is asynchronous; refresh after the engine has ingested the publication.'

  return (
    <section aria-label="Live MMS inventory">
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 10, fontFamily: 'var(--font-mono)', fontSize: '9px', letterSpacing: '0.1em' }}>
        <span>PERSISTED LIGHT UNITS</span>
        <span style={{ color: 'var(--mms-accent)' }}>{inventory ? `${inventory.count} STORED` : loading ? 'LOADING' : 'UNAVAILABLE'}</span>
        <span style={{ color: 'var(--text-muted)' }}>LIVE — GET /admin/mms/lightunits</span>
        <button type="button" onClick={onRefresh} disabled={loading || !hasTwin}
          style={{ marginLeft: 'auto', background: 'none', border: '1px solid var(--border-mid)', borderRadius: 3, color: 'var(--text-secondary)', padding: '3px 10px', cursor: loading ? 'default' : 'pointer' }}>
          {loading ? 'REFRESHING...' : 'REFRESH'}
        </button>
      </div>
      <p style={{ color: 'var(--text-muted)', fontSize: '11px', lineHeight: 1.5 }}>
        Actual MMS Provider records, filtered by the selected iTwin&apos;s CIR owner mapping.
        The MMS engine stores approved streetlights automatically; no manual Store Assets action is required.
        This view refreshes every 10 seconds while open.
      </p>
      {error && <div role="alert" style={{ color: '#ef4444', padding: '12px 0' }}>Could not load MMS inventory: {error}</div>}
      <div style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-subtle)', borderRadius: 6, overflowX: 'auto' }}>
        <table style={{ width: '100%', borderCollapse: 'collapse' }} aria-busy={loading}>
          <thead><tr>{headings.map(h => <th key={h} style={{ ...cell, color: 'var(--text-muted)', fontFamily: 'var(--font-mono)', fontSize: '9px', whiteSpace: 'nowrap' }}>{h}</th>)}</tr></thead>
          <tbody>
            {inventory?.items.map(unit => (
              <tr key={unit.lightUnitId}>
                <td style={cell}>{unit.lightUnitId}</td>
                <td style={{ ...cell, color: 'var(--text-primary)', fontWeight: 600 }}>{unit.lightUnitName}</td>
                <td style={{ ...cell, fontFamily: 'var(--font-mono)', fontSize: '10px' }}>{unit.extAssetId ?? '—'}</td>
                <td style={cell}>{unit.ownerId ?? '—'}</td>
                <td style={cell}>{unit.lightSystemId ?? 'Ungrouped'}</td>
                <td style={{ ...cell, whiteSpace: 'nowrap' }}>{unit.dateUpdate ? new Date(unit.dateUpdate).toLocaleString() : '—'}</td>
              </tr>
            ))}
            {!error && !inventory?.items.length && <tr><td colSpan={headings.length} style={{ ...cell, padding: 24, color: 'var(--text-muted)' }}>{empty}</td></tr>}
          </tbody>
        </table>
      </div>
    </section>
  )
}
