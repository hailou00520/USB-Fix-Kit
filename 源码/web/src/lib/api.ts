export type StatusInfo = {
  isPe: boolean
  drive: string | null
  isAdmin: boolean
  /** 探测到键鼠/USB 主机控制器已挂 —— 应自动修 */
  inputBroken?: boolean
  inputReason?: string
  /** 命令行 --autofix */
  autoFix?: boolean
  /** --pe 在正常 Windows 下预览 PE 功能集 */
  pePreview?: boolean
}

export type ActionId =
  | "check"
  | "full"
  | "usb"
  | "usbdk"
  | "account"
  | "deploy"
  | "deployUsb"
  | "deployNet"
  | "remote"
  | "drivers"
  | "winFix"
  | "usbBoot"
  | "uninstall"
  | "openLog"
  | "tcNetDiagnose"
  | "tcNetFixWifi"
  | "tcNetFixDriver"
  | "tcNetWaitUsb"
  | "tcNetResetStack"
  | "tcNetFull"
  | "tcNetBackupWifi"
  | "tcNetRestoreWifi"
  | "tcShareDiagnose"
  | "tcShareFull"
  | "tcShareHosting"
  | "tcShareNas"
  | "tcFolderScan"
  | "tcFolderKill"

export type ActionExtra = {
  path?: string
  pids?: number[]
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, init)
  if (!res.ok) {
    const text = await res.text()
    let msg = text || `HTTP ${res.status}`
    try {
      const j = JSON.parse(text) as { message?: string; error?: string }
      msg = j.message || j.error || msg
    } catch {
      /* keep raw */
    }
    // 去掉冗长 traceback，只留最后一行有用信息
    const lines = msg.split(/\r?\n/).map((s) => s.trim()).filter(Boolean)
    const last = lines[lines.length - 1] || msg
    throw new Error(last.length > 280 ? `${last.slice(0, 280)}…` : last)
  }
  return res.json() as Promise<T>
}

export function getStatus() {
  return request<StatusInfo>("/api/status")
}

/** 系统对话框：一次可选文件或文件夹（占用页） */
export function browsePath() {
  return request<{ ok: boolean; cancelled?: boolean; path: string | null }>("/api/browse", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: "{}",
  })
}

export function runAction(action: ActionId, extra?: ActionExtra) {
  return request<{
    ok: boolean
    message: string
    lockers?: { pid: number; name: string; reason?: string; exe_path?: string }[]
  }>("/api/action", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ action, ...extra }),
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
