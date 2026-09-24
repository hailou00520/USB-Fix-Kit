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
  {
    id: "full",
    title: "穷尽修复（不含远程）",
    description: "补 USB · 清 UsbDk · 开机自修。远程自启请用独立按钮「部署远程软件自启」。",
    icon: <Zap className="size-5" />,
    featured: true,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "check",
    title: "检查并急救开机键鼠",
    description: "完整体检；若发现开机风险，自动写入开机必起（不自动重启）",
    icon: <Search className="size-4" />,
    mode: "usb",
  },
  {
    id: "remote",
    title: "部署远程软件自启",
    description: "独立功能：复制绿版、清安全标记、只用 bat 自启（不把 exe 放进 Startup，避免弹窗）",
    icon: <MonitorSmartphone className="size-4" />,
    mode: "usb",
  },
  {
    id: "usbdk",
    title: "UsbDk 专项离线补服务",
    description: "双 ControlSet · 清过滤驱动 · 补 usbxhci / hub Start",
    icon: <Bug className="size-4" />,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "usb",
    title: "离线修复 USB",
    description: "注册表双集 · DISM · SFC",
    icon: <Usb className="size-4" />,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "account",
    title: "解除账号自动登录",
    description: "启用 Administrator · 空密码进桌面",
    icon: <Shield className="size-4" />,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "deploy",
    title: "仅部署 USB 开机自检",
    description: "写入穷尽修复脚本到 Windows 自启",
    icon: <Wrench className="size-4" />,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "drivers",
    title: "删除第三方驱动",
    description: "清理 USB / vivo / Mi 等冲突 oem 驱动",
    icon: <Trash2 className="size-4" />,
    danger: true,
    peOnly: true,
    mode: "usb",
  },
  {
    id: "usbBoot",
    title: "急救：写入开机必起",
    description: "立刻把 USB/键鼠服务写成开机必起 · 关快速启动（不自动重启）",
    icon: <Shield className="size-5" />,
    featured: true,
    winOnly: true,
    mode: "usb",
  },
  {
    id: "winFix",
    title: "深度全面体检修复",
    description: "开机必起 + UsbDk/注册表深度清理（不自动重启）",
    icon: <Play className="size-4" />,
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
    description: "服务软重启 → 协议栈（不碰 USB）",
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
  const bottomRef = useRef<HTMLDivElement>(null)

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

  const onAction = async (id: ActionId, extra?: ActionExtra) => {
    if (busy) return
    setBusy(true)
    setDone(false)
    setError(null)
    setLogs([])
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
      // 错误写进日志流，不再单独弹红框挤压日志区
      setLogs((prev) => [...prev, `错误: ${msg}`])
      setError(msg)
    } finally {
      setBusy(false)
    }
  }

  const isPe = status?.isPe ?? true
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
    usb: "USB 修复",
    net: "网络 / WiFi",
    share: "文件共享",
    folder: "解除占用",
  }
  const modeDesc: Record<KitMode, string> = {
    usb: "急救键鼠 / USB：查出风险就写死开机必起",
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
      <div className="pointer-events-none fixed inset-0 bg-app">
        <span className="orb orb-a" />
        <span className="orb orb-b" />
        <span className="orb orb-c" />
      </div>

      <div
        className="relative flex h-full w-full min-h-0 flex-1 flex-col gap-3 p-4"
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
            {/* 第一行：标题 ←→ 状态/窗口按钮，固定行高垂直居中 */}
            <div className="grid h-9 grid-cols-[minmax(0,1fr)_auto] items-center gap-3">
              <div className="flex min-w-0 items-center gap-2.5">
                <div className="flex size-9 shrink-0 items-center justify-center overflow-hidden rounded-md border bg-accent/40">
                  <img src="./icon.png" alt="" width={36} height={36} className="size-full object-cover" />
                </div>
                <div className="min-w-0 leading-none">
                  <p className="text-[10px] font-medium uppercase tracking-[0.14em] text-primary">
                    一体化急救工具
                  </p>
                  <CardTitle
                    className="mt-0.5 truncate text-lg leading-none tracking-tight"
                    style={{ fontFamily: "var(--font-display)" }}
                  >
                    {modeTitle[mode]}
                  </CardTitle>
                </div>
              </div>

              <div className="flex h-7 items-center gap-1.5" data-no-drag>
                <Badge
                  variant="secondary"
                  className={cn(
                    "inline-flex h-7 items-center gap-1.5 rounded-md border-0 px-2 text-[11px] font-normal leading-none",
                    isPe
                      ? "bg-sky-50 text-sky-800 hover:bg-sky-50"
                      : "bg-teal-50 text-teal-800 hover:bg-teal-50"
                  )}
                >
                  <span
                    className={cn(
                      "size-1.5 shrink-0 rounded-full",
                      isPe ? "bg-sky-500" : "bg-teal-600"
                    )}
                  />
                  {isPe ? "PE" : "Windows"}
                </Badge>
                <Button
                  variant="outline"
                  size="icon"
                  className="size-7 shrink-0 rounded-md"
                  onClick={refresh}
                  disabled={busy}
                  title="刷新"
                >
                  <RefreshCw className={cn("size-3.5", busy && "animate-spin")} />
                </Button>
                {inHost && (
                  <div className="flex h-7 items-stretch overflow-hidden rounded-md border bg-muted/50">
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
            </div>

            {/* 第二行：说明 + 目标盘，同一行高基线 */}
            <div className="mt-2 grid h-6 grid-cols-[minmax(0,1fr)_auto] items-center gap-3">
              <CardDescription className="min-w-0 truncate text-[12px] leading-none">
                {modeDesc[mode]}
              </CardDescription>
              <p
                key={status?.drive ?? "none"}
                className={cn(
                  "inline-flex h-6 items-center gap-1 rounded-md px-2 text-[11px] leading-none",
                  status?.drive
                    ? "bg-teal-50/80 text-teal-900"
                    : "text-muted-foreground"
                )}
              >
                <HardDrive className="size-3 shrink-0 opacity-70" strokeWidth={1.75} />
                {status?.drive ? (
                  <>
                    <span className="opacity-70">目标盘</span>
                    <span className="font-semibold tracking-wide">{status.drive}</span>
                  </>
                ) : (
                  "未检测到系统盘"
                )}
                {status && !status.isAdmin ? (
                  <span className="text-amber-700"> · 需管理员</span>
                ) : null}
              </p>
            </div>

            {/* 第三行：页签左对齐，等高 */}
            <div className="mt-2.5 flex h-7 items-center gap-1" data-no-drag>
              {(
                [
                  ["usb", "USB"],
                  ["net", "网络"],
                  ["share", "共享"],
                  ["folder", "占用"],
                ] as const
              ).map(([id, label]) => {
                const disabled = busy || (id !== "usb" && isPe)
                return (
                  <button
                    key={id}
                    type="button"
                    disabled={disabled}
                    title={id !== "usb" && isPe ? "需在正常 Windows 下使用" : undefined}
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
                        : "bg-muted text-muted-foreground hover:bg-muted/80",
                      disabled && id !== "usb" && "opacity-50"
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
          <Card data-no-drag className="shrink-0 gap-0 overflow-hidden rounded-lg border-border/80 py-0 elevation">
            <CardHeader className="flex flex-row items-center justify-between space-y-0 border-b px-3.5 py-2">
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
                <ScrollArea rail className="h-[min(32vh,280px)]">
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

        {/* 操作区按内容高度；按钮多才滚动。日志吃掉剩余空间，避免中间空一大块 */}
        <div data-no-drag className="flex min-h-0 flex-1 flex-col gap-3">
          {mode !== "folder" && (
          <div
            className={cn(
              "min-h-0",
              mode === "net" || mode === "share"
                ? "max-h-[46%] shrink overflow-hidden"
                : "shrink-0"
            )}
          >
            <ScrollArea
              rail={mode === "net" || mode === "share"}
              className={cn(
                mode === "net" || mode === "share" ? "h-full max-h-[min(46vh,420px)]" : "h-auto"
              )}
            >
              <div className="flex flex-col gap-2.5">
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
                    <div className="relative grid grid-cols-[2.25rem_1fr_auto] items-center gap-3 px-3.5 py-3 pl-5">
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
                        <p className="text-[12px] leading-snug text-primary-foreground/80">
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
                  <section className="animate-fade-up animate-fade-up-delay-2 space-y-3">
                    <div className="flex h-6 items-center justify-between">
                      <h2 className="text-xs font-medium text-muted-foreground">更多操作</h2>
                      <span className="text-[11px] tabular-nums text-muted-foreground">{rest.length} 项</span>
                    </div>
                    <div className="grid gap-2.5 sm:grid-cols-2">
                      {rest.map((a) => (
                        <button
                          key={a.id}
                          type="button"
                          disabled={busy || (needsDrive(a) && !status?.drive)}
                          onClick={() => onAction(a.id)}
                          className={cn(
                            "stagger-item pressable card-shine surface-card group grid grid-cols-[2rem_1fr] items-center gap-2.5 rounded-lg border p-3.5 text-left elevation",
                            "hover:border-primary/25 hover:elevation-md",
                            "disabled:pointer-events-none disabled:opacity-50",
                            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                            a.danger && "hover:border-destructive/30"
                          )}
                        >
                          <span
                            className={cn(
                              "flex size-8 items-center justify-center rounded-md border",
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
                            <span className="block text-[11px] leading-snug text-muted-foreground">
                              {a.description}
                            </span>
                          </span>
                        </button>
                      ))}
                    </div>
                  </section>
                )}
              </div>
            </ScrollArea>
          </div>
          )}

          <Card
            className={cn(
              "animate-fade-up animate-fade-up-delay-3 flex min-h-[11rem] flex-1 flex-col gap-0 overflow-hidden rounded-lg border-border/80 py-0 elevation",
              done && !busy && "animate-success-flash"
            )}
          >
            <CardHeader className="flex shrink-0 flex-row items-center justify-between gap-2 space-y-0 border-b px-3.5 py-2.5">
              <div className="flex min-w-0 items-center gap-2">
                <span className={cn("flex size-6 shrink-0 items-center justify-center rounded-md border bg-muted", busy && "animate-pulse")}>
                  <Terminal className={cn("size-3 text-primary", busy && "animate-spin")} />
                </span>
                <div className="min-w-0">
                  <CardTitle className="text-[13px]">输出日志</CardTitle>
                  <CardDescription className="truncate text-[10px]">
                    {canOpenReport ? "进度在此 · 白话报告点右侧打开" : "实时修复进度"}
                  </CardDescription>
                </div>
              </div>
              <div className="flex shrink-0 items-center gap-1.5">
                {busy && (
                  <Badge variant="secondary" className="gap-1 rounded-sm text-[11px] font-normal">
                    <Loader2 className="size-3 animate-spin" />
                    运行中
                  </Badge>
                )}
                {done && !busy && !error && (
                  <Badge className="animate-badge-pop gap-1 rounded-sm border-0 bg-teal-50 text-[11px] text-teal-800 hover:bg-teal-50">
                    <CheckCircle2 className="size-3" />
                    完成
                  </Badge>
                )}
                {error && !busy && (
                  <Badge
                    variant="secondary"
                    className="max-w-[8rem] truncate rounded-sm border-0 bg-destructive/10 text-[11px] font-normal text-destructive"
                    title={error}
                  >
                    失败
                  </Badge>
                )}
                {canOpenReport && (
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    disabled={busy}
                    onClick={() => void onAction("openLog")}
                    className="h-7 gap-1 rounded-md border-primary/20 bg-white/70 px-2 text-[11px] font-medium text-foreground shadow-none hover:border-primary/35 hover:bg-teal-50/80"
                    title="打开最新白话体检报告"
                  >
                    <FileText className="size-3.5 text-primary" />
                    打开报告
                  </Button>
                )}
              </div>
            </CardHeader>

            <CardContent className="flex min-h-0 flex-1 flex-col gap-2 px-3.5 py-2.5">
              <ScrollArea rail className="scroll-rail-inset h-0 min-h-0 flex-1 rounded-md border border-primary/10 bg-white/45">
                <div className="space-y-0.5 p-3 font-mono text-[11.5px] leading-5 text-zinc-600">
                  {logs.length === 0 ? (
                    <div className="flex min-h-[6.5rem] flex-col justify-center gap-1.5 py-1">
                      <p className="text-[12.5px] font-medium text-zinc-800">等待操作…</p>
                      <div className="space-y-1 text-zinc-500">
                        <p className="tip-item">
                          <span className="mr-2 text-teal-700">01</span>
                          点上方推荐按钮开始
                        </p>
                        <p className="tip-item">
                          <span className="mr-2 text-teal-700">02</span>
                          进度会实时写在这里
                        </p>
                        <p className="tip-item">
                          <span className="mr-2 text-teal-700">03</span>
                          {mode === "usb"
                            ? "做完后点右上「打开报告」看白话结论"
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
        </div>
      </div>
    </div>
  )
}
