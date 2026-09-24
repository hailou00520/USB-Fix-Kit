export type Locker = {
  pid: number
  name: string
  type_name: string
  reason: string
  exe_path: string
  skip?: string | null
}

export type ScanResult = {
  ok: boolean
  path: string
  note: string
  is_admin: boolean
  lockers: Locker[]
  error?: string
}

export type KillResult = {
  ok: boolean
  messages: string[]
  error?: string
}

export type WifiProfile = {
  ssid: string
  password: string
  auth?: string
  cipher?: string
}

export type ShareItem = {
  name: string
  path: string
  description?: string
  special?: boolean
  access?: string
  unc_name?: string
  unc_ips?: string[]
}

export type ShareSettings = {
  password_protected?: boolean
  guest_active?: boolean
  server_running?: boolean
  firewall_file_share?: boolean
  firewall_discovery?: boolean
  smb_guest?: boolean | null
  smb_require_sign?: boolean | null
  profiles?: Array<Record<string, unknown>>
}

export type NetworkResult = {
  ok: boolean
  is_admin: boolean
  steps: string[]
  suggestions?: string[]
  need_reboot?: boolean
  need_unplug?: boolean
  ping_internet?: boolean
  ping_gateway?: boolean
  wlan_alive?: boolean
  adapters?: Array<Record<string, unknown>>
  services?: Array<Record<string, unknown>>
  pnp?: Array<Record<string, unknown>>
  profiles?: WifiProfile[]
  export_path?: string
  wlan_text?: string
  error?: string
  computer?: string
  ips?: string[]
  access_lines?: string[]
  shares?: ShareItem[]
  settings?: ShareSettings
  share_name?: string
  unc?: string
  share_info?: Record<string, unknown>
}

export type Api = {
  get_status: () => Promise<{ is_admin: boolean; initial_path: string }>
  browse_folder: () => Promise<string | null>
  browse_file: () => Promise<string | null>
  take_dropped_paths: (names: string[]) => Promise<string[]>
  scan: (path: string) => Promise<ScanResult>
  kill: (pids: number[]) => Promise<KillResult>
  network_diagnose: () => Promise<NetworkResult>
  network_fix_wifi: () => Promise<NetworkResult>
  network_fix_driver: () => Promise<NetworkResult>
  network_wait_usb: () => Promise<NetworkResult>
  network_reset_stack: () => Promise<NetworkResult>
  network_full_repair: () => Promise<NetworkResult>
  network_wifi_passwords: () => Promise<NetworkResult>
  network_backup_wifi: () => Promise<NetworkResult>
  network_restore_wifi: () => Promise<NetworkResult>
  share_diagnose: () => Promise<NetworkResult>
  share_fix_win11_nas: () => Promise<NetworkResult>
  share_fix_discovery: () => Promise<NetworkResult>
  share_fix_firewall: () => Promise<NetworkResult>
  share_fix_private_profile: () => Promise<NetworkResult>
  share_restart_smb: () => Promise<NetworkResult>
  share_clear_credentials: () => Promise<NetworkResult>
  share_enable_smb1: () => Promise<NetworkResult>
  share_disable_password_protected: () => Promise<NetworkResult>
  share_enable_password_protected: () => Promise<NetworkResult>
  share_get_settings: () => Promise<NetworkResult>
  share_enable_hosting: (close_password?: boolean) => Promise<NetworkResult>
  share_enable_public_folder: () => Promise<NetworkResult>
  share_list: () => Promise<NetworkResult>
  share_create: (path: string, name?: string, access?: string) => Promise<NetworkResult>
  share_remove: (name: string) => Promise<NetworkResult>
  share_open_path: (target?: string) => Promise<NetworkResult>
  share_open_advanced_panel: () => Promise<NetworkResult>
  share_map_drive: (unc: string, letter?: string) => Promise<NetworkResult>
  share_full_repair: () => Promise<NetworkResult>
}

declare global {
  interface Window {
    pywebview?: {
      api: Api
    }
  }
}

export {}
