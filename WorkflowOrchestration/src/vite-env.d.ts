/// <reference types="vite/client" />

interface ImportMetaEnv {
  /**
   * Base URL of the sandbox API. Empty in development, where vite.config.ts
   * proxies /admin and the browser stays on one origin.
   */
  readonly VITE_SANDBOX_API?: string

  /**
   * Shared key for /admin/* on instances that set Sandbox:AdminKey. Not needed
   * locally, where the endpoints are open.
   */
  readonly VITE_SANDBOX_ADMIN_KEY?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
