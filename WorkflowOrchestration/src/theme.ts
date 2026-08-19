// Theme selection. The palette itself lives in index.css as two sets of custom
// properties; all this does is decide which set is active by setting
// data-theme on <html>.
//
// Dark is the default because every panel in this app was designed against it,
// and because an operations console is usually running on a wall display or in
// a dim room. Light exists for projectors and printed screenshots, where the
// dark surface ramp turns into an undifferentiated block.

export type Theme = 'dark' | 'light'

const STORAGE_KEY = 'oiie.theme'

// Read the stored choice, falling back to the OS preference and then to dark.
//
// localStorage access is guarded: Safari throws on access in private browsing
// rather than returning null, and a theme preference is not worth failing the
// app's first paint over.
export function initialTheme(): Theme {
  try {
    const stored = localStorage.getItem(STORAGE_KEY)
    if (stored === 'dark' || stored === 'light') return stored
  } catch {
    /* storage unavailable; fall through to the OS preference */
  }

  // Only an explicit light preference flips the default. matchMedia is absent
  // in some test environments, hence the guard.
  if (typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: light)').matches) {
    return 'light'
  }
  return 'dark'
}

// Dark is left as the absence of the attribute so the :root block in index.css
// stays the base case and light is the single override. That keeps the CSS from
// needing both themes spelled out twice.
export function applyTheme(theme: Theme): void {
  const root = document.documentElement
  if (theme === 'light') root.setAttribute('data-theme', 'light')
  else root.removeAttribute('data-theme')

  try {
    localStorage.setItem(STORAGE_KEY, theme)
  } catch {
    /* storage unavailable; the theme still applies for this session */
  }
}
