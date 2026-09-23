export type StatusInfo = {
  isPe: boolean
  drive: string | null
  isAdmin: boolean
}

export type ActionId =
  | "check"
  | "full"
  | "usb"
  | "usbdk"
  | "account"
  | "deploy"
  | "remote"
  | "drivers"
  | "winFix"
  | "uninstall"
  | "openLog"

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, init)
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || `HTTP ${res.status}`)
  }
  return res.json() as Promise<T>
}

export function getStatus() {
  return request<StatusInfo>("/api/status")
}

export function runAction(action: ActionId) {
  return request<{ ok: boolean; message: string }>("/api/action", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ action }),
  })
}

export function createLogStream(onLine: (line: string) => void) {
  const es = new EventSource("/api/logs")
  es.onmessage = (ev) => onLine(ev.data)
  es.onerror = () => {
    // keep open; host may restart stream
  }
  return () => es.close()
}
