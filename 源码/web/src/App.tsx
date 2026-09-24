import { useCallback, useEffect, useRef, useState, type DragEvent, type MouseEvent, type ReactNode } from "react"
import {
  Usb,
  Shield,
  Wrench,
  Trash2,
  Play,
  FileText,
  RefreshCw,
  Loader2,
  CheckCircle2,
  MonitorSmartphone,
  Bug,
  Zap,
  Terminal,
  ChevronRight,
  ChevronUp,
  ChevronDown,
  Search,
  Minus,
  Square,
  X,
  Copy,
  HardDrive,
  Wifi,
  FolderOpen,
  Share2,
  Network,
  FolderSearch,
  MousePointerClick,
} from "lucide-react"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { ScrollArea } from "@/components/ui/scroll-area"
import { cn } from "@/lib/utils"
import {
  createLogStream,
  getStatus,
  browsePath,
  runAction,
  type ActionId,
  type ActionExtra,
  type StatusInfo,
} from "@/lib/api"

type KitMode = "usb" | "net" | "share" | "folder"

type ActionItem = {
  id: ActionId
  title: string
  description: string
  icon: ReactNode
  featured?: boolean
  danger?: boolean
  peOnly?: boolean
  winOnly?: boolean
  mode: KitMode
}

const ACTIONS: ActionItem[] = [
  // ── PE 媒介：只有这些在真 PE 里能修盘 / 写自启 ──
  {
    id: "full",
    title: "① 穷尽修复并部署合并自修",
    description:
      "媒介主流程·不需网：离线修 ImagePath/UsbDk → 写 USB+网开机自修 → 进 Windows 全屏自跑",
    icon: <Zap className="size-5" />,
    featured: true,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "deploy",
    title: "部署合并自修（USB+网）",
    description: "开机自跑键鼠/USB + 有线网（推荐两边都死时）",
    icon: <Wrench className="size-4" />,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "deployUsb",
    title: "仅部署键鼠/USB 自修",
    description: "开机只穷尽修 USB/键鼠，不跑救网",
    icon: <Usb className="size-4" />,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "deployNet",
    title: "仅部署救网自修",
    description: "开机专修有线网卡/DHCP（网活即可远程接手）",
    icon: <Wifi className="size-4" />,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "remote",
    title: "部署远程软件自启",
    description: "部署时不需网；进系统后要上网才能连。没网请先部署救网",
    icon: <MonitorSmartphone className="size-4" />,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "check",
    title: "仅检查问题",
    description: "只读体检，不修改（PE 离线检目标盘）",
    icon: <Search className="size-4" />,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "usbdk",
    title: "UsbDk / ImagePath 离线补丁",
    description: "双 ControlSet · 清过滤 · 修 usbxhci ImagePath",
    icon: <Bug className="size-4" />,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "usb",
    title: "离线 USB 深度修复",
    description: "注册表双集 · DISM · SFC（不含写自启；要自启请点①）",
    icon: <Usb className="size-4" />,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "account",
    title: "解除账号 · 自动登录",
    description: "启用 Administrator · 空密码，保证进系统后自修能跑",
    icon: <Shield className="size-4" />,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "drivers",
    title: "删除第三方 USB 驱动",
    description: "清理 vivo / 小米等冲突 oem",
    icon: <Trash2 className="size-4" />,
    danger: true,
    peOnly: true,
    mode: "usb",
  },
  // ── 正常 Windows ──
  {
    id: "winFix",
    title: "立即全面自动修复",
    description:
      "键鼠+网络全自动（离线）：ImagePath/问题码39 · USB控制器 · 有线网卡 · 穷尽阶段（不自动重启）",
    icon: <Play className="size-5" />,
    featured: true,
    winOnly: true,
    mode: "usb",
  },
  {
    id: "check",
    title: "检查并急救开机键鼠",
    description: "完整体检；若发现开机风险，自动写入开机必起（不自动重启）",
    icon: <Search className="size-4" />,
    winOnly: true,
    mode: "usb",
  },
  {
    id: "usbBoot",
    title: "急救：写入开机必起",
    description: "立刻写 USB/键鼠 Start · 修 ImagePath · 关快速启动（不自动重启）",
    icon: <Shield className="size-5" />,
    winOnly: true,
    mode: "usb",
  },
  {
    id: "remote",
    title: "部署远程软件自启",
    description: "复制绿版、清安全标记、bat 自启（进系统后需能上网才连得上）",
    icon: <MonitorSmartphone className="size-4" />,
    winOnly: true,
    mode: "usb",
  },
  {
    id: "uninstall",
    title: "卸载开机自检",
    description: "移除服务、自启项与体检脚本",
    icon: <Trash2 className="size-4" />,
    winOnly: true,
    mode: "usb",
  },
  {
    id: "openLog",
    title: "打开体检报告",
    description: "白话结论：要不要管、严重/提示分别是什么",
    icon: <FileText className="size-4" />,
    winOnly: true,
    mode: "usb",
  },
  // —— 网络（原畅通匣，已并入本程序）——
  {
    id: "tcNetDiagnose",
    title: "诊断网络 / WiFi",
    description: "只读检查网卡、WLAN 服务与连通性",
    icon: <Search className="size-5" />,
    featured: true,
    winOnly: true,
    mode: "net",
  },
  {
    id: "tcNetFixWifi",
    title: "一键修复 WiFi（仅服务）",
    description: "重启 WLAN 等服务 · 刷新 DNS · 不碰 USB",
    icon: <Wifi className="size-4" />,
    winOnly: true,
    mode: "net",
  },
  {
    id: "tcNetFixDriver",
    title: "软重置无线服务",
    description: "仅重启 WLAN（不删设备、不写 USB 注册表）",
    icon: <RefreshCw className="size-4" />,
    winOnly: true,
    mode: "net",
  },
  {
    id: "tcNetWaitUsb",
    title: "等待拔插无线网卡",
    description: "只监测人工拔插，不删除设备节点",
    icon: <Usb className="size-4" />,
    winOnly: true,
    mode: "net",
  },
  {
    id: "tcNetResetStack",
    title: "重置协议栈",
    description: "Winsock / TCP-IP · 可能需重启",
    icon: <Network className="size-4" />,
    winOnly: true,
    mode: "net",
  },
  {
    id: "tcNetFull",
    title: "网络全面修复",
    description: "有线优先硬复位/DHCP → WiFi 服务 → 协议栈（离线·不碰 USB）",
    icon: <Wrench className="size-4" />,
    winOnly: true,
    mode: "net",
  },
  {
    id: "tcNetBackupWifi",
    title: "备份 WiFi",
    description: "导出配置到软件目录 wifi_backup",
    icon: <HardDrive className="size-4" />,
    winOnly: true,
    mode: "net",
  },
  {
    id: "tcNetRestoreWifi",
    title: "导入 WiFi",
    description: "从 wifi_backup 写回并尝试连接",
    icon: <Wifi className="size-4" />,
    winOnly: true,
    mode: "net",
  },
  // —— 共享 ——
  {
    id: "tcShareDiagnose",
    title: "诊断文件共享",
    description: "SMB / 发现 / 防火墙 / Win11 来宾",
    icon: <Share2 className="size-4" />,
    winOnly: true,
    mode: "share",
  },
  {
    id: "tcShareFull",
    title: "共享全面修复",
    description: "专用网络 · 发现 · 防火墙 · SMB",
    icon: <Share2 className="size-5" />,
    featured: true,
    winOnly: true,
    mode: "share",
  },
  {
    id: "tcShareHosting",
    title: "一键开启本机共享",
    description: "开主机共享（可关密码保护）",
    icon: <FolderOpen className="size-4" />,
    winOnly: true,
    mode: "share",
  },
  {
    id: "tcShareNas",
    title: "修复 Win11 / NAS 来宾",
    description: "AllowInsecureGuestAuth 等",
    icon: <MonitorSmartphone className="size-4" />,
    winOnly: true,
    mode: "share",
  },
]

type HostChrome = {
  postMessage: (msg: string) => void
  postMessageWithAdditionalObjects?: (msg: string, objects: FileList | File[]) => void
  addEventListener?: (t: string, fn: (e: { data: string }) => void) => void
}

function getHostChrome(): HostChrome | null {
  const w = window as Window & { chrome?: { webview?: HostChrome } }
  return w.chrome?.webview ?? null
}

function hostCmd(cmd: string) {
  try {
    getHostChrome()?.postMessage(cmd)
  } catch {
    /* browser preview */
  }
}

export default function App() {
  const [status, setStatus] = useState<StatusInfo | null>(null)
  const [logs, setLogs] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState(false)
  const [maximized, setMaximized] = useState(false)
  const [inHost, setInHost] = useState(false)
  const [mode, setMode] = useState<KitMode>("usb")
  const [folderPath, setFolderPath] = useState("")
  const [lockers, setLockers] = useState<
    { pid: number; name: string; reason?: string; exe_path?: string }[]
  >([])
  const [selectedPids, setSelectedPids] = useState<number[]>([])
  const [dropOver, setDropOver] = useState(false)
  const [scannedOnce, setScannedOnce] = useState(false)
  const [autoSec, setAutoSec] = useState<number | null>(null)
  /** 日志抽屉：空闲收起让按钮完整可见；开跑自动展开，也可手动开关 */
  const [logExpanded, setLogExpanded] = useState(false)
  const bottomRef = useRef<HTMLDivElement>(null)
  const autoStartedRef = useRef(false)
  const busyRef = useRef(false)
  const onActionRef = useRef<(id: ActionId, extra?: ActionExtra) => Promise<void>>(async () => {})

  const applyFolderPath = useCallback((p: string) => {
    const t = p.trim().replace(/^["']|["']$/g, "")
    if (t) {
      setFolderPath(t)
      setLockers([])
      setSelectedPids([])
      setScannedOnce(false)
      setError(null)
    }
  }, [])

  const togglePid = useCallback((pid: number) => {
    setSelectedPids((prev) =>
      prev.includes(pid) ? prev.filter((x) => x !== pid) : [...prev, pid]
    )
  }, [])

  const selectAllLockers = useCallback(() => {
    setSelectedPids(lockers.map((x) => x.pid))
  }, [lockers])

  const selectNoneLockers = useCallback(() => {
    setSelectedPids([])
  }, [])

  const onBrowse = useCallback(async () => {
    if (busy) return
    try {
      const r = await browsePath()
      if (r.ok && r.path) applyFolderPath(r.path)
    } catch (e) {
      setError(e instanceof Error ? e.message : "选择路径失败")
    }
  }, [busy, applyFolderPath])

  const onNativeDrop = useCallback(
    (e: DragEvent) => {
      e.preventDefault()
      e.stopPropagation()
      setDropOver(false)
      const files = e.dataTransfer?.files
      if (!files?.length) return
      const host = getHostChrome()
      if (host?.postMessageWithAdditionalObjects) {
        try {
          host.postMessageWithAdditionalObjects("FilesDropped", files)
          return
        } catch { /* fall through */ }
      }
      // 浏览器预览兜底：只有文件名，提示用系统选择
      setError("请用「浏览」选择，或在本程序窗口内拖放")
    },
    []
  )

  const refresh = useCallback(async () => {
    try {
      const s = await getStatus()
      setStatus(s)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : "无法连接后端")
    }
  }, [])

  useEffect(() => {
    document.documentElement.classList.remove("dark")
    refresh()
    const host = getHostChrome()
    setInHost(!!host)
    const unlog = createLogStream((line) => setLogs((prev) => [...prev.slice(-500), line]))

    const handleHostData = (data: string) => {
      if (data.startsWith("winstate:")) setMaximized(data.slice(9) === "1")
      if (data.startsWith("dropped-path:")) {
        applyFolderPath(data.slice("dropped-path:".length))
        setMode("folder")
      }
    }

    if (!host) return unlog

    const onMsg = (ev: MessageEvent) => {
      handleHostData(typeof ev.data === "string" ? ev.data : "")
    }
    window.addEventListener("message", onMsg)
    try {
      host.addEventListener?.("message", (e) => {
        handleHostData(typeof e.data === "string" ? e.data : "")
      })
    } catch { /* ignore */ }
    host.postMessage("query-state")
    return () => {
      window.removeEventListener("message", onMsg)
      unlog()
    }
  }, [refresh, applyFolderPath])

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" })
  }, [logs])

  useEffect(() => {
    if (busy) setLogExpanded(true)
  }, [busy])

  useEffect(() => {
    if (mode === "folder") setLogExpanded(true)
  }, [mode])

  const onAction = useCallback(async (id: ActionId, extra?: ActionExtra) => {
    if (busyRef.current) return
    busyRef.current = true
    setBusy(true)
    setDone(false)
    setError(null)
    setLogs([])
    setAutoSec(null)
    try {
      const res = await runAction(id, extra)
      if (!res.ok) throw new Error(res.message)
      if (id === "tcFolderScan") {
        const list = (res.lockers ?? []).filter((x) => x.pid > 0)
        setLockers(list)
        setSelectedPids(list.map((x) => x.pid))
        setScannedOnce(true)
      }
      if (id === "tcFolderKill") {
        const killed = new Set(extra?.pids ?? [])
        setLockers((prev) => prev.filter((x) => !killed.has(x.pid)))
        setSelectedPids((prev) => prev.filter((p) => !killed.has(p)))
      }
      setDone(true)
      await refresh()
    } catch (e) {
      const msg = e instanceof Error ? e.message : "操作失败"
      setLogs((prev) => [...prev, `错误: ${msg}`])
      setError(msg)
    } finally {
      busyRef.current = false
      setBusy(false)
    }
  }, [refresh])

  useEffect(() => {
    onActionRef.current = onAction
  }, [onAction])

  // 键鼠已废 或 --autofix：自动倒计时开修（不用点）；键鼠正常则不打扰
  useEffect(() => {
    if (!status || status.isPe) return
    if (autoStartedRef.current || busyRef.current) return
    const q = new URLSearchParams(window.location.search)
    const force = q.get("autofix") === "1" || !!status.autoFix
    const broken = !!status.inputBroken
    if (!force && !broken) return
    setAutoSec(force ? 3 : 8)
    if (broken && status.inputReason) {
      setLogs((prev) =>
        prev.some((l) => l.includes("键鼠失灵探测"))
          ? prev
          : [...prev, `键鼠失灵探测: ${status.inputReason}`]
      )
    }
  }, [status])

  useEffect(() => {
    if (autoSec === null) return
    if (autoSec <= 0) {
      if (autoStartedRef.current || busyRef.current) {
        setAutoSec(null)
        return
      }
      autoStartedRef.current = true
      setAutoSec(null)
      setMode("usb")
      setLogs((prev) => [...prev, "══ 键鼠失灵模式：自动开始全面修复 ══"])
      void onActionRef.current("winFix")
      return
    }
    const t = window.setTimeout(() => {
      setAutoSec((s) => (s === null ? null : s - 1))
    }, 1000)
    return () => window.clearTimeout(t)
  }, [autoSec])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement | null)?.tagName
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return

      if (e.key === "Escape" && autoSec !== null) {
        e.preventDefault()
        setAutoSec(null)
        setLogs((prev) => [...prev, "已取消自动修复倒计时（仍可按 Enter / F5 开始）"])
        return
      }

      if (busyRef.current) return

      if (e.key === "Enter" || e.key === "F5" || e.key === "F1") {
        e.preventDefault()
        setAutoSec(null)
        autoStartedRef.current = true
        setMode("usb")
        void onActionRef.current("winFix")
        return
      }

      // 数字键切页（有键盘时）；PE 只有 USB 页
      if (e.key === "1") setMode("usb")
      if (status?.isPe) return
      if (e.key === "2") setMode("net")
      if (e.key === "3") setMode("share")
      if (e.key === "4") setMode("folder")
    }
    window.addEventListener("keydown", onKey)
    return () => window.removeEventListener("keydown", onKey)
  }, [autoSec, status?.isPe])

  const isPe = status?.isPe ?? true

  useEffect(() => {
    if (isPe && mode !== "usb") setMode("usb")
  }, [isPe, mode])

  const visible = ACTIONS.filter((a) => {
    if (a.mode !== mode) return false
    if (a.peOnly && !isPe) return false
    if (a.winOnly && isPe) return false
    return true
  })
  const featured = visible.filter((a) => a.featured)
  // 打开报告并进日志栏标题，避免半宽孤卡 + 右侧留白
  const rest = visible.filter((a) => !a.featured && a.id !== "openLog")
  const canOpenReport = mode === "usb" && !isPe
  const needsDrive = (a: ActionItem) =>
    a.mode === "usb" && (!!a.peOnly || (a.id === "check" && isPe) || (a.id === "remote" && isPe))

  const modeTitle: Record<KitMode, string> = {
    usb: isPe ? "PE 媒介急救" : "USB 修复",
    net: "网络 / WiFi",
    share: "文件共享",
    folder: "解除占用",
  }
  const modeDesc: Record<KitMode, string> = {
    usb: isPe
      ? status?.pePreview
        ? "仅预览界面 · 点修复会提示无效 · 真急救请 U 盘进 PE"
        : "本软件当媒介：可选 USB / 救网 / 合并 开机自修 → 拔盘重启进 Windows 自跑（不需网）"
      : "急救键鼠 / USB：查出风险就写死开机必起",
    net: "诊断与安全修复 WiFi（不删 USB 设备）",
    share: "本机共享、NAS / Win11 来宾等",
    folder: "扫描并结束占用文件夹的进程",
  }

  const onDragDown = (e: MouseEvent) => {
    if (!inHost || e.button !== 0) return
    if (
      (e.target as HTMLElement).closest(
        "[data-no-drag],button,a,input,textarea,select,[role='button']"
      )
    )
      return
    hostCmd("drag")
  }

  return (
    <div className="relative flex h-full flex-col">
      {autoSec !== null && !busy && (
        <div
          className="animate-fade-up fixed inset-0 z-50 flex items-center justify-center bg-zinc-950/55 px-6 backdrop-blur-[2px]"
          role="dialog"
          aria-label="键鼠失灵自动修复"
        >
          <div className="w-full max-w-lg rounded-xl border border-teal-700/30 bg-white/95 p-6 text-center shadow-2xl elevation-md">
            <p className="text-[11px] font-medium uppercase tracking-[0.16em] text-teal-800">
              键鼠失灵急救
            </p>
            <p
              className="mt-2 text-4xl font-semibold tabular-nums tracking-tight text-teal-950"
              style={{ fontFamily: "var(--font-display)" }}
            >
              {autoSec}
              <span className="ml-1 text-lg font-medium text-teal-800/80">秒</span>
            </p>
            <p className="mt-2 text-[15px] font-medium text-zinc-800">
              {status?.inputBroken
                ? "检测到键鼠已失灵 —— 即将自动全面修复，什么都不用点"
                : "即将自动「全面自动修复」—— 不用点鼠标"}
            </p>
            <p className="mt-3 space-y-1 text-[12px] leading-relaxed text-zinc-500">
              <span className="block">
                有键盘：<kbd className="rounded border bg-muted px-1.5 py-0.5 font-mono text-[11px]">Enter</kbd>
                {" / "}
                <kbd className="rounded border bg-muted px-1.5 py-0.5 font-mono text-[11px]">F5</kbd>
                {" 立刻开始 · "}
                <kbd className="rounded border bg-muted px-1.5 py-0.5 font-mono text-[11px]">Esc</kbd>
                {" 取消倒计时"}
              </span>
              <span className="block">键鼠都废了：什么都不用做，倒计时结束会自己修</span>
            </p>
            <div className="mt-5 flex flex-wrap items-center justify-center gap-2">
              <Button
                type="button"
                className="h-10 min-w-[8rem] px-4"
                autoFocus
                onClick={() => {
                  setAutoSec(null)
                  autoStartedRef.current = true
                  setMode("usb")
                  void onAction("winFix")
                }}
              >
                立刻开始
              </Button>
              <Button
                type="button"
                variant="outline"
                className="h-10 min-w-[8rem] px-4"
                onClick={() => {
                  setAutoSec(null)
                  setLogs((prev) => [...prev, "已取消自动修复倒计时（仍可按 Enter / F5 开始）"])
                }}
              >
                取消 Esc
              </Button>
            </div>
          </div>
        </div>
      )}
      <div className="pointer-events-none fixed inset-0 bg-app">
        <span className="orb orb-a" />
        <span className="orb orb-b" />
        <span className="orb orb-c" />
      </div>

      <div
        className="relative flex h-full w-full min-h-0 flex-1 flex-col gap-3 overflow-hidden px-4 pt-4 pb-5"
        onMouseDown={onDragDown}
      >
        <Card
          className="animate-fade-up relative gap-0 overflow-hidden rounded-lg border-border/80 py-0 elevation"
          onDoubleClick={() => {
            if (inHost) hostCmd("maximize")
          }}
        >
          {busy && (
            <div className="busy-bar">
              <span />
            </div>
          )}
          <CardHeader className="space-y-0 px-3.5 py-3">
            {/* 合并前布局：左标题说明 · 右状态/窗控，目标盘贴在其正下方 */}
            <div className="flex flex-row flex-wrap items-start justify-between gap-3">
              <div className="flex min-w-0 items-center gap-2.5">
                <div className="flex size-9 shrink-0 items-center justify-center overflow-hidden rounded-md border bg-accent/40">
                  <img src="./icon.png" alt="" width={36} height={36} className="size-full object-cover" />
                </div>
                <div className="min-w-0 space-y-0.5">
                  <p className="text-[10px] font-medium uppercase tracking-[0.14em] text-primary">
                    一体化急救工具
                  </p>
                  <CardTitle
                    className="truncate text-lg leading-none tracking-tight"
                    style={{ fontFamily: "var(--font-display)" }}
                  >
                    {modeTitle[mode]}
                  </CardTitle>
                  <CardDescription className="truncate text-[12px] leading-snug">
                    {modeDesc[mode]}
                  </CardDescription>
                </div>
              </div>

              <div className="flex flex-col items-end gap-1.5" data-no-drag>
                <div className="flex h-7 items-center gap-1.5">
                  <Badge
                    variant="secondary"
                    className={cn(
                      "animate-chrome-in inline-flex h-7 items-center gap-1.5 rounded-md border-0 px-2 text-[11px] font-normal leading-none",
                      isPe
                        ? "bg-sky-50 text-sky-800 hover:bg-sky-50"
                        : "bg-teal-50 text-teal-800 hover:bg-teal-50"
                    )}
                  >
                    <span
                      className={cn(
                        "size-1.5 shrink-0 rounded-full animate-pulse-dot",
                        isPe ? "bg-sky-500" : "bg-teal-600"
                      )}
                    />
                    {isPe ? (status?.pePreview ? "PE 预览" : "PE") : "Windows"}
                  </Badge>
                  <Button
                    variant="outline"
                    size="icon"
                    className="animate-chrome-in animate-chrome-in-d1 size-7 shrink-0 rounded-md"
                    onClick={refresh}
                    disabled={busy}
                    title="刷新"
                  >
                    <RefreshCw className={cn("size-3.5", busy && "animate-spin")} />
                  </Button>
                  {inHost && (
                    <div className="animate-chrome-in animate-chrome-in-d2 flex h-7 items-stretch overflow-hidden rounded-md border bg-muted/50">
                      <button
                        type="button"
                        title="最小化"
                        className="win-btn win-btn-compact"
                        onClick={() => hostCmd("minimize")}
                      >
                        <Minus className="size-3.5" strokeWidth={1.75} />
                      </button>
                      <button
                        type="button"
                        title={maximized ? "还原" : "最大化"}
                        className="win-btn win-btn-compact"
                        onClick={() => hostCmd("maximize")}
                      >
                        {maximized ? (
                          <Copy className="size-3" strokeWidth={1.75} />
                        ) : (
                          <Square className="size-3" strokeWidth={1.75} />
                        )}
                      </button>
                      <button
                        type="button"
                        title="关闭"
                        className="win-btn win-btn-compact win-btn-close"
                        onClick={() => hostCmd("close")}
                      >
                        <X className="size-3.5" strokeWidth={1.75} />
                      </button>
                    </div>
                  )}
                </div>
                <p
                  key={status?.drive ?? "none"}
                  className={cn(
                    "inline-flex items-center gap-1 rounded-sm px-2 py-0.5 text-[11px] leading-none",
                    status?.drive
                      ? "animate-drive-in bg-teal-50/80 text-teal-900"
                      : "animate-drive-miss text-muted-foreground"
                  )}
                >
                  {status?.drive ? (
                    <>
                      <HardDrive className="size-3 shrink-0 text-teal-700" strokeWidth={1.75} />
                      <span className="text-teal-700/80">目标盘</span>
                      <span className="animate-drive-letter font-semibold tracking-wide text-teal-950">
                        {status.drive}
                      </span>
                    </>
                  ) : (
                    <>
                      <HardDrive className="size-3 shrink-0 opacity-60" strokeWidth={1.75} />
                      未检测到系统盘
                    </>
                  )}
                  {status && !status.isAdmin ? (
                    <span className="text-amber-700"> · 需管理员</span>
                  ) : null}
                </p>
              </div>
            </div>

            {/* 页签：PE 只留 USB（媒介页）；网络/共享/占用在正常 Windows */}
            <div className="mt-2.5 flex h-7 items-center gap-1" data-no-drag>
              {(
                (
                  isPe
                    ? ([["usb", "USB"]] as const)
                    : ([
                        ["usb", "USB"],
                        ["net", "网络"],
                        ["share", "共享"],
                        ["folder", "占用"],
                      ] as const)
                )
              ).map(([id, label]) => {
                return (
                  <button
                    key={id}
                    type="button"
                    disabled={busy}
                    onClick={() => {
                      setMode(id)
                      setLogs([])
                      setDone(false)
                      setError(null)
                    }}
                    className={cn(
                      "inline-flex h-7 items-center rounded-md px-2.5 text-[11px] font-medium leading-none transition-colors",
                      mode === id
                        ? "bg-primary text-primary-foreground"
                        : "bg-muted text-muted-foreground hover:bg-muted/80"
                    )}
                  >
                    {label}
                  </button>
                )
              })}
            </div>
          </CardHeader>
        </Card>

        {mode === "folder" && !isPe && (
          <Card
            data-no-drag
            className={cn(
              "shrink-0 gap-0 rounded-lg border-border/80 py-0 elevation transition-colors",
              dropOver && "border-primary/50 bg-primary/[0.04] ring-2 ring-primary/20"
            )}
            onDragEnter={(e) => {
              e.preventDefault()
              setDropOver(true)
            }}
            onDragOver={(e) => {
              e.preventDefault()
              e.dataTransfer.dropEffect = "copy"
              setDropOver(true)
            }}
            onDragLeave={(e) => {
              if (e.currentTarget.contains(e.relatedTarget as Node)) return
              setDropOver(false)
            }}
            onDrop={onNativeDrop}
          >
            <CardContent className="flex flex-col gap-2.5 px-3.5 py-3">
              <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
                <input
                  value={folderPath}
                  onChange={(e) => setFolderPath(e.target.value)}
                  placeholder="粘贴路径，或拖放文件/文件夹到这里"
                  className="h-9 min-w-0 flex-1 rounded-md border bg-background px-3 text-[12px] outline-none focus-visible:ring-2 focus-visible:ring-ring"
                  disabled={busy}
                />
                <div className="flex shrink-0 flex-wrap gap-2">
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={busy}
                    onClick={() => void onBrowse()}
                    className="h-9 gap-1"
                    title="可选文件或文件夹（选文件夹：进入后点打开）"
                  >
                    <FolderOpen className="size-3.5" />
                    浏览
                  </Button>
                  <Button
                    size="sm"
                    disabled={busy || !folderPath.trim()}
                    onClick={() => {
                      setLockers([])
                      setSelectedPids([])
                      setScannedOnce(false)
                      void onAction("tcFolderScan", { path: folderPath.trim() })
                    }}
                  >
                    {busy ? <Loader2 className="animate-spin" /> : <FolderSearch />}
                    扫描占用
                  </Button>
                  <Button
                    size="sm"
                    variant="destructive"
                    disabled={busy || selectedPids.length === 0}
                    onClick={() => void onAction("tcFolderKill", { pids: selectedPids })}
                  >
                    结束所选 ({selectedPids.length})
                  </Button>
                </div>
              </div>
              <p className="flex items-center gap-1.5 text-[11px] text-muted-foreground">
                <MousePointerClick className="size-3.5 shrink-0 opacity-70" />
                {dropOver
                  ? "松开即可填入路径"
                  : "可拖放文件/文件夹到本卡片，或点「浏览」。扫 .lnk 会自动解析到真实程序"}
              </p>
            </CardContent>
          </Card>
        )}

        {mode === "folder" && !isPe && scannedOnce && (
          <Card data-no-drag className="min-h-0 shrink gap-0 overflow-hidden rounded-lg border-border/80 py-0 elevation">
            <CardHeader className="flex shrink-0 flex-row items-center justify-between space-y-0 border-b px-3.5 py-2">
              <div>
                <CardTitle className="text-[13px]">占用进程</CardTitle>
                <CardDescription className="text-[10px]">
                  {lockers.length === 0
                    ? "未发现占用，可换真实程序/安装目录再扫"
                    : `共 ${lockers.length} 个 · 勾选后点「结束所选」`}
                </CardDescription>
              </div>
              {lockers.length > 0 && (
                <div className="flex items-center gap-1">
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    className="h-7 px-2 text-[11px]"
                    disabled={busy}
                    onClick={selectAllLockers}
                  >
                    全选
                  </Button>
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    className="h-7 px-2 text-[11px]"
                    disabled={busy}
                    onClick={selectNoneLockers}
                  >
                    全不选
                  </Button>
                </div>
              )}
            </CardHeader>
            <CardContent className="px-0 py-0">
              {lockers.length === 0 ? (
                <div className="space-y-1.5 px-3.5 py-4 text-center text-[12px] text-muted-foreground">
                  <p>没有占用此路径的进程</p>
                  {folderPath.toLowerCase().endsWith(".lnk") && (
                    <p className="text-[11px] leading-snug">
                      会按快捷方式名（如 QQ）和解析出的程序名查找任务管理器里在跑的进程；
                      若仍为空，请直接选安装目录。
                    </p>
                  )}
                </div>
              ) : (
                <ScrollArea rail type="always" className="h-[min(28vh,220px)]">
                  <ul className="divide-y divide-border/70 pr-1">
                    {lockers.map((L) => {
                      const checked = selectedPids.includes(L.pid)
                      return (
                        <li key={L.pid}>
                          <label
                            className={cn(
                              "flex cursor-pointer items-start gap-2.5 px-3.5 py-2.5 transition-colors",
                              "hover:bg-muted/50",
                              checked && "bg-primary/[0.04]"
                            )}
                          >
                            <input
                              type="checkbox"
                              className="mt-0.5 size-3.5 shrink-0 accent-teal-700"
                              checked={checked}
                              disabled={busy}
                              onChange={() => togglePid(L.pid)}
                            />
                            <span className="min-w-0 flex-1 space-y-0.5">
                              <span className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
                                <span className="text-[12.5px] font-medium text-foreground">
                                  {L.name || "未知进程"}
                                </span>
                                <span className="font-mono text-[11px] text-muted-foreground">
                                  PID {L.pid}
                                </span>
                              </span>
                              {L.reason && (
                                <span className="block text-[11px] leading-snug text-muted-foreground">
                                  {L.reason}
                                </span>
                              )}
                              {L.exe_path && (
                                <span
                                  className="block truncate font-mono text-[10px] text-zinc-400"
                                  title={L.exe_path}
                                >
                                  {L.exe_path}
                                </span>
                              )}
                            </span>
                          </label>
                        </li>
                      )
                    })}
                  </ul>
                </ScrollArea>
              )}
            </CardContent>
          </Card>
        )}

        {/* 操作区铺满；日志收起=底栏不挡，展开=整面盖住操作区（不半遮） */}
        <div data-no-drag className="relative min-h-0 flex-1">
          {mode !== "folder" && (() => {
            const actionsBody = (
              <div className="flex flex-col gap-2 pr-1 pb-4">
                {featured.map((a) => (
                  <button
                    key={a.id}
                    type="button"
                    disabled={busy || (needsDrive(a) && !status?.drive)}
                    onClick={() => onAction(a.id)}
                    className={cn(
                      "animate-fade-up animate-fade-up-delay-1 pressable cta-shine group relative w-full shrink-0 overflow-hidden rounded-lg text-left",
                      "bg-primary text-primary-foreground elevation-md",
                      "hover:brightness-[1.03]",
                      "disabled:pointer-events-none disabled:opacity-50",
                      "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
                    )}
                  >
                    <span className="cta-bar absolute inset-y-0 left-0 w-1 bg-teal-300/80" />
                    <div className="relative grid grid-cols-[2.25rem_1fr_auto] items-center gap-3 px-3.5 py-2.5 pl-5">
                      <span className="cta-icon flex size-9 items-center justify-center rounded-md bg-white/15">
                        {busy ? <Loader2 className="size-4 animate-spin" /> : a.icon}
                      </span>
                      <div className="min-w-0 space-y-0.5">
                        <div className="flex flex-wrap items-center gap-1.5">
                          <span className="text-[14px] font-semibold leading-none tracking-tight">
                            {a.title}
                          </span>
                          <Badge className="h-4 rounded-md border-0 bg-white/20 px-1.5 py-0 text-[10px] leading-none text-white hover:bg-white/20">
                            推荐
                          </Badge>
                        </div>
                        <p className="line-clamp-2 text-[12px] leading-snug text-primary-foreground/80">
                          {a.description}
                        </p>
                      </div>
                      <ChevronRight className="size-4 opacity-70" />
                    </div>
                    {busy && (
                      <div className="busy-bar">
                        <span />
                      </div>
                    )}
                  </button>
                ))}

                {rest.length > 0 && (
                  <section className="animate-fade-up animate-fade-up-delay-2 space-y-2">
                    <div className="flex h-5 items-center justify-between gap-2 pr-0.5">
                      <h2 className="text-xs font-medium text-muted-foreground">更多操作</h2>
                      <span className="shrink-0 text-[11px] tabular-nums text-muted-foreground">
                        {rest.length} 项 · 上下滑看全
                      </span>
                    </div>
                    <div className="grid gap-2 sm:grid-cols-2">
                      {rest.map((a) => (
                        <button
                          key={a.id}
                          type="button"
                          disabled={busy || (needsDrive(a) && !status?.drive)}
                          onClick={() => onAction(a.id)}
                          className={cn(
                            "stagger-item pressable card-shine surface-card group grid grid-cols-[2rem_1fr] items-start gap-2 rounded-lg border px-3 py-2.5 text-left elevation",
                            "hover:border-primary/25 hover:elevation-md",
                            "disabled:pointer-events-none disabled:opacity-50",
                            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                            a.danger && "hover:border-destructive/30"
                          )}
                        >
                          <span
                            className={cn(
                              "mt-0.5 flex size-7 shrink-0 items-center justify-center rounded-md border",
                              a.danger
                                ? "border-destructive/20 bg-destructive/5 text-destructive"
                                : "bg-muted text-muted-foreground"
                            )}
                          >
                            {busy ? <Loader2 className="size-3.5 animate-spin" /> : a.icon}
                          </span>
                          <span className="min-w-0 space-y-0.5">
                            <span
                              className={cn(
                                "block text-[12.5px] font-medium leading-snug",
                                a.danger ? "text-destructive" : "text-foreground"
                              )}
                            >
                              {a.title}
                            </span>
                            <span className="line-clamp-2 block text-[11px] leading-snug text-muted-foreground">
                              {a.description}
                            </span>
                          </span>
                        </button>
                      ))}
                    </div>
                  </section>
                )}
              </div>
            )
            return (
              <div
                className={cn(
                  "absolute inset-x-0 top-0 overflow-hidden",
                  logExpanded ? "bottom-0 opacity-0 pointer-events-none" : "bottom-12"
                )}
                aria-hidden={logExpanded}
              >
                <ScrollArea
                  key={`actions-${mode}-${isPe ? "pe" : "win"}`}
                  rail
                  type="always"
                  className="h-full"
                >
                  {actionsBody}
                </ScrollArea>
              </div>
            )
          })()}

          {/* 收起：底栏一条，不挡按钮 */}
          {!logExpanded && (
            <button
              type="button"
              data-no-drag
              onClick={() => setLogExpanded(true)}
              className={cn(
                "absolute inset-x-0 bottom-0 z-10 flex h-11 items-center gap-2 rounded-lg border border-border/80 bg-card/95 px-3.5 text-left elevation backdrop-blur-sm",
                "hover:bg-muted/50"
              )}
              title="展开日志（整面覆盖）"
            >
              <span className="flex w-5 shrink-0 justify-center">
                <span className="h-1 w-8 rounded-full bg-muted-foreground/35" />
              </span>
              <span className={cn("flex size-6 shrink-0 items-center justify-center rounded-md border bg-muted", busy && "animate-pulse")}>
                <Terminal className={cn("size-3 text-primary", busy && "animate-spin")} />
              </span>
              <span className="min-w-0 flex-1">
                <span className="flex items-center gap-1.5">
                  <span className="text-[13px] font-semibold leading-none">输出日志</span>
                  {busy && (
                    <Badge variant="secondary" className="h-4 gap-1 rounded-sm px-1.5 text-[10px] font-normal">
                      <Loader2 className="size-2.5 animate-spin" />
                      运行中
                    </Badge>
                  )}
                  {done && !busy && !error && (
                    <Badge className="h-4 gap-1 rounded-sm border-0 bg-teal-50 px-1.5 text-[10px] text-teal-800 hover:bg-teal-50">
                      <CheckCircle2 className="size-2.5" />
                      完成
                    </Badge>
                  )}
                  {error && !busy && (
                    <Badge
                      variant="secondary"
                      className="h-4 max-w-[6rem] truncate rounded-sm border-0 bg-destructive/10 px-1.5 text-[10px] font-normal text-destructive"
                      title={error}
                    >
                      失败
                    </Badge>
                  )}
                </span>
                <span className="mt-0.5 block truncate text-[10px] text-muted-foreground">
                  {logs.length > 0
                    ? logs[logs.length - 1]
                    : "点开整面覆盖 · 点修复后会自动打开"}
                </span>
              </span>
              <ChevronUp className="size-4 shrink-0 text-muted-foreground" />
            </button>
          )}

          {/* 展开：整面盖住操作区，不半遮按钮 */}
          {logExpanded && (
            <Card
              key={`log-panel-${mode}`}
              className={cn(
                "absolute inset-0 z-20 flex flex-col gap-0 overflow-hidden rounded-lg border-border/80 bg-card py-0 elevation",
                "animate-fade-up"
              )}
            >
              {done && !busy && !error && (
                <div
                  aria-hidden
                  className="animate-success-flash pointer-events-none absolute inset-0 z-0 rounded-lg"
                />
              )}
              <div className="relative z-[1] flex h-11 shrink-0 items-center gap-2 border-b px-3.5">
                <button
                  type="button"
                  data-no-drag
                  onClick={() => setLogExpanded(false)}
                  className="flex min-w-0 flex-1 items-center gap-2 text-left hover:opacity-90"
                  title="收起日志，回到操作"
                  disabled={mode === "folder"}
                >
                  <span className="flex w-5 shrink-0 justify-center">
                    <span className="h-1 w-8 rounded-full bg-muted-foreground/35" />
                  </span>
                  <span className={cn("flex size-6 shrink-0 items-center justify-center rounded-md border bg-muted", busy && "animate-pulse")}>
                    <Terminal className={cn("size-3 text-primary", busy && "animate-spin")} />
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className="flex items-center gap-1.5">
                      <span className="text-[13px] font-semibold leading-none">输出日志</span>
                      {busy && (
                        <Badge variant="secondary" className="h-4 gap-1 rounded-sm px-1.5 text-[10px] font-normal">
                          <Loader2 className="size-2.5 animate-spin" />
                          运行中
                        </Badge>
                      )}
                      {done && !busy && !error && (
                        <Badge className="h-4 gap-1 rounded-sm border-0 bg-teal-50 px-1.5 text-[10px] text-teal-800 hover:bg-teal-50">
                          <CheckCircle2 className="size-2.5" />
                          完成
                        </Badge>
                      )}
                      {error && !busy && (
                        <Badge
                          variant="secondary"
                          className="h-4 max-w-[6rem] truncate rounded-sm border-0 bg-destructive/10 px-1.5 text-[10px] font-normal text-destructive"
                          title={error}
                        >
                          失败
                        </Badge>
                      )}
                    </span>
                    <span className="mt-0.5 block text-[10px] text-muted-foreground">
                      {mode === "folder" ? "占用页日志" : "整面覆盖中 · 点此或右侧返回操作"}
                    </span>
                  </span>
                </button>
                <div className="flex shrink-0 items-center gap-1">
                  {canOpenReport && (
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      disabled={busy}
                      onClick={() => void onAction("openLog")}
                      className="h-7 gap-1 rounded-md border-primary/20 bg-white/70 px-2 text-[11px] font-medium shadow-none hover:border-primary/35 hover:bg-teal-50/80"
                      title="打开最新白话体检报告"
                    >
                      <FileText className="size-3.5 text-primary" />
                      报告
                    </Button>
                  )}
                  {mode !== "folder" && (
                    <Button
                      type="button"
                      size="sm"
                      variant="secondary"
                      className="h-7 gap-1 px-2 text-[11px]"
                      onClick={() => setLogExpanded(false)}
                      title="收起日志"
                    >
                      <ChevronDown className="size-3.5" />
                      返回
                    </Button>
                  )}
                </div>
              </div>

              <CardContent className="relative z-[1] flex min-h-0 flex-1 flex-col gap-2 px-3.5 pb-3 pt-2.5">
                <ScrollArea rail type="always" className="scroll-rail-inset h-0 min-h-0 flex-1 rounded-md border border-primary/10 bg-white/45">
                  <div className="space-y-0.5 p-3 font-mono text-[11.5px] leading-5 text-zinc-600">
                    {logs.length === 0 ? (
                      <div className="flex min-h-[10rem] flex-col justify-center gap-1.5 py-2">
                        <p className="text-[12.5px] font-medium text-zinc-800">等待操作…</p>
                        <div className="space-y-1 text-zinc-500">
                          <p className="tip-item">
                            <span className="mr-2 text-teal-700">01</span>
                            点「返回」回到按钮；开始修复后会自动打开本页
                          </p>
                          <p className="tip-item">
                            <span className="mr-2 text-teal-700">02</span>
                            进度会实时写在这里（整面覆盖，不再挡半截按钮）
                          </p>
                          <p className="tip-item">
                            <span className="mr-2 text-teal-700">03</span>
                            {mode === "usb"
                              ? "修好后拔 U 盘重启 · 进系统请干看着"
                              : "结果会显示在本日志区"}
                          </p>
                        </div>
                      </div>
                    ) : (
                      logs.map((line, i) => (
                        <div
                          key={`${i}-${line.slice(0, 24)}`}
                          className={cn(
                            "animate-log-in",
                            line.startsWith("错误") || line.startsWith("✗")
                              ? "text-red-600"
                              : line.includes("完成") ||
                                  line.startsWith("✓") ||
                                  line.includes("成功")
                                ? "text-teal-700"
                                : line.startsWith("══") || line.startsWith("──")
                                  ? "text-zinc-900"
                                  : undefined
                          )}
                        >
                          {line || "\u00A0"}
                        </div>
                      ))
                    )}
                    <div ref={bottomRef} />
                  </div>
                </ScrollArea>
                <p className="shrink-0 text-center text-[10px] text-muted-foreground">
                  {mode === "usb"
                    ? "修好后拔 U 盘重启 · 进系统请干看着"
                    : "同一程序内功能 · 网络/占用不删 USB 设备"}
                </p>
              </CardContent>
            </Card>
          )}
        </div>
      </div>
    </div>
  )
}
