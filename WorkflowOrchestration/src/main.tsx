import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App'
import './index.css'
import { applyTheme, initialTheme } from './theme'

// Applied before the first render so the app never paints the dark palette and
// then swaps to light a frame later.
applyTheme(initialTheme())

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
)
