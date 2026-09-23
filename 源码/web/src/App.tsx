import { useCallback, useEffect, useRef, useState, type MouseEvent, type ReactNode } from "react"
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
import { Separator } from "@/components/ui/separator"
import { cn } from "@/lib/utils"
import {
  createLogStream,
  getStatus,
  runAction,
  type ActionId,
  type StatusInfo,
} from "@/lib/api"

type ActionItem = {
  id: ActionId
  title: string
  description: string
  icon: ReactNode
  featured?: boolean
  danger?: boolean
  peOnly?: boolean
  winOnly?: boolean
}

const ACTIONS: ActionItem[] = [
  {
    id: "full",
    title: "穷尽修复（不含远程）",
    description: "补 USB · 清 UsbDk · 开机自修。远程自启请用独立按钮「部署远程软件自启」。",
    icon: <Zap className="size-5" />,
    featured: true,
    peOnly: true,
  },
  {
    id: "check",
    title: "仅检查问题（完整）",
    description: "完整只读体检：服务/过滤/策略/PnP/电源等；区分严重问题与提示，不修改系统",
    icon: <Search className="size-4" />,
  },
  {
    id: "remote",
    title: "部署远程软件自启",
    description: "独立功能：复制绿版、清安全标记、只用 bat 自启（不把 exe 放进 Startup，避免弹窗）",
    icon: <MonitorSmartphone className="size-4" />,
  },
  {
    id: "usbdk",
    title: "UsbDk 专项离线补服务",
    description: "双 ControlSet · 清过滤驱动 · 补 usbxhci / hub Start",
    icon: <Bug className="size-4" />,
    peOnly: true,
  },
  {
    id: "usb",
    title: "离线修复 USB",
    description: "注册表双集 · DISM · SFC",
    icon: <Usb className="size-4" />,
    peOnly: true,
  },
  {
    id: "account",
    title: "解除账号自动登录",
    description: "启用 Administrator · 空密码进桌面",
    icon: <Shield className="size-4" />,
    peOnly: true,
  },
  {
    id: "deploy",
    title: "仅部署 USB 开机自检",
    description: "写入穷尽修复脚本到 Windows 自启",
    icon: <Wrench className="size-4" />,
    peOnly: true,
  },
  {
    id: "drivers",
    title: "删除第三方驱动",
    description: "清理 USB / vivo / Mi 等冲突 oem 驱动",
    icon: <Trash2 className="size-4" />,
    danger: true,
    peOnly: true,
  },
  {
    id: "winFix",
    title: "立即全面体检修复",
    description: "UsbDk 专项 + 注册表深度清理 + 设备重启",
    icon: <Play className="size-5" />,
    featured: true,
    winOnly: true,
  },
  {
    id: "uninstall",
    title: "卸载开机自检",
    description: "移除服务、自启项与体检脚本",
    icon: <Trash2 className="size-4" />,
    winOnly: true,
  },
  {
    id: "openLog",
    title: "打开体检报告",
    description: "白话结论：要不要管、严重/提示分别是什么",
    icon: <FileText className="size-4" />,
    winOnly: true,
  },
]

type HostChrome = { postMessage: (msg: string) => void }

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
  const bottomRef = useRef<HTMLDivElement>(null)

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
    if (!host) return unlog

    const onMsg = (ev: MessageEvent) => {
      const data = typeof ev.data === "string" ? ev.data : ""
      if (data.startsWith("winstate:")) setMaximized(data.slice(9) === "1")
    }
    // WebView2 既会走 window.message，也会走 chrome.webview 的 addEventListener
    window.addEventListener("message", onMsg)
    try {
      ;(host as HostChrome & { addEventListener?: (t: string, fn: (e: { data: string }) => void) => void })
        .addEventListener?.("message", (e) => {
          const data = typeof e.data === "string" ? e.data : ""
          if (data.startsWith("winstate:")) setMaximized(data.slice(9) === "1")
        })
    } catch { /* ignore */ }
    host.postMessage("query-state")
    return () => {
      window.removeEventListener("message", onMsg)
      unlog()
    }
  }, [refresh])

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" })
  }, [logs])

  const onAction = async (id: ActionId) => {
    if (busy) return
    setBusy(true)
    setDone(false)
    setError(null)
    setLogs([])
    try {
      const res = await runAction(id)
      if (!res.ok) throw new Error(res.message)
      setDone(true)
      await refresh()
    } catch (e) {
      setError(e instanceof Error ? e.message : "操作失败")
    } finally {
      setBusy(false)
    }
  }

  const isPe = status?.isPe ?? true
  const visible = ACTIONS.filter((a) => {
    if (a.peOnly && !isPe) return false
    if (a.winOnly && isPe) return false
    return true
  })
  const featured = visible.filter((a) => a.featured)
  const rest = visible.filter((a) => !a.featured)
  const needsDrive = (a: ActionItem) =>
    !!a.peOnly || (a.id === "check" && isPe) || (a.id === "remote" && isPe)

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
          <CardHeader className="flex flex-row flex-wrap items-center justify-between gap-3 space-y-0 px-3.5 py-3">
            <div className="flex min-w-0 items-center gap-2.5">
              <div className="animate-logo-float flex size-9 shrink-0 items-center justify-center overflow-hidden rounded-md border bg-accent/40">
                <img src="./icon.png" alt="" width={36} height={36} className="size-full object-cover" />
              </div>
              <div className="min-w-0 space-y-0.5">
                <p className="animate-fade-up text-[10px] font-medium uppercase tracking-[0.16em] text-primary">
                  USB Fix Kit
                </p>
                <CardTitle
                  className="text-xl tracking-tight"
                  style={{ fontFamily: "var(--font-display)" }}
                >
                  USB 急救工具
                </CardTitle>
                <span className="title-underline" />
                <CardDescription className="animate-fade-up animate-fade-up-delay-1 text-[12px] leading-snug">
                  PE 点一次，进系统后干看着。穷尽办法自动修；全失败才提示重装。
                </CardDescription>
              </div>
            </div>

            <div className="flex flex-col items-end gap-1.5" data-no-drag>
              <div className="flex items-center gap-1.5">
                <Badge
                  variant="secondary"
                  className={cn(
                    "animate-badge-pop gap-1.5 rounded-sm px-2 py-0.5 text-[11px] font-normal",
                    isPe
                      ? "bg-sky-50 text-sky-800 hover:bg-sky-50"
                      : "bg-teal-50 text-teal-800 hover:bg-teal-50"
                  )}
                >
                  <span
                    className={cn(
                      "size-1.5 rounded-full animate-pulse-dot",
                      isPe ? "bg-sky-500" : "bg-teal-600"
                    )}
                  />
                  {isPe ? "PE 维护模式" : "正常 Windows"}
                </Badge>
                <Button
                  variant="outline"
                  size="icon"
                  className="size-7 rounded-sm transition-transform hover:rotate-45"
                  onClick={refresh}
                  disabled={busy}
                  title="刷新"
                >
                  <RefreshCw className={cn("size-3.5", busy && "animate-spin")} />
                </Button>
                {inHost && (
                  <div className="ml-0.5 flex h-7 overflow-hidden rounded-sm border bg-muted/50">
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
          </CardHeader>
        </Card>

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
            <div className="relative flex items-center gap-3 px-3.5 py-3 pl-5">
              <span className="cta-icon flex size-9 shrink-0 items-center justify-center rounded-md bg-white/15 transition-transform duration-200 group-hover:scale-110">
                {busy ? <Loader2 className="size-4 animate-spin" /> : a.icon}
              </span>
              <div className="min-w-0 flex-1 space-y-0.5">
                <div className="flex flex-wrap items-center gap-1.5">
                  <span className="text-[14px] font-semibold tracking-tight">{a.title}</span>
                  <Badge className="cta-recommend rounded-sm border-0 bg-white/20 px-1.5 py-0 text-[10px] text-white hover:bg-white/20">
                    推荐
                  </Badge>
                </div>
                <p className="text-[12px] leading-snug text-primary-foreground/80">
                  {a.description}
                </p>
              </div>
              <ChevronRight className="cta-chevron size-4 shrink-0 opacity-70 transition-transform duration-200 group-hover:translate-x-0.5" />
            </div>
            {busy && (
              <div className="busy-bar">
                <span />
              </div>
            )}
          </button>
        ))}

        <section data-no-drag className="animate-fade-up animate-fade-up-delay-2 shrink-0 space-y-3">
          <div className="animate-slide-in flex items-center justify-between px-0.5">
            <h2 className="text-xs font-medium text-muted-foreground">更多操作</h2>
            <span className="text-[11px] text-muted-foreground">{rest.length} 项</span>
          </div>
          <div className="grid gap-3 sm:grid-cols-2">
            {rest.map((a) => (
              <button
                key={a.id}
                type="button"
                disabled={busy || (needsDrive(a) && !status?.drive)}
                onClick={() => onAction(a.id)}
                className={cn(
                  "stagger-item pressable card-shine surface-card group flex items-start gap-2.5 rounded-lg border p-3.5 text-left elevation",
                  "hover:border-primary/25 hover:elevation-md",
                  "disabled:pointer-events-none disabled:opacity-50",
                  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                  a.danger && "hover:border-destructive/30"
                )}
              >
                <span
                  className={cn(
                    "card-icon mt-0.5 flex size-8 shrink-0 items-center justify-center rounded-md border transition-all duration-200",
                    a.danger
                      ? "border-destructive/20 bg-destructive/5 text-destructive"
                      : "bg-muted text-muted-foreground group-hover:scale-110 group-hover:border-primary/20 group-hover:bg-accent group-hover:text-accent-foreground"
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

        <Card
          data-no-drag
          className={cn(
            "animate-fade-up animate-fade-up-delay-3 flex min-h-0 flex-1 flex-col gap-0 overflow-hidden rounded-lg border-border/80 py-0 elevation",
            done && !busy && "animate-success-flash"
          )}
        >
          <CardHeader className="flex flex-row items-center justify-between space-y-0 border-b px-3.5 py-3">
            <div className="flex items-center gap-2">
              <span className={cn("flex size-6 items-center justify-center rounded-md border bg-muted", busy && "animate-pulse")}>
                <Terminal className={cn("size-3 text-primary", busy && "animate-spin")} />
              </span>
              <div>
                <CardTitle className="text-[13px]">输出日志</CardTitle>
                <CardDescription className="text-[10px]">实时修复进度</CardDescription>
              </div>
            </div>
            <div className="flex items-center gap-2">
              {busy && (
                <Badge variant="secondary" className="gap-1 rounded-sm text-[11px] font-normal">
                  <Loader2 className="size-3 animate-spin" />
                  运行中
                </Badge>
              )}
              {done && !busy && (
                <Badge className="animate-badge-pop gap-1 rounded-sm border-0 bg-teal-50 text-[11px] text-teal-800 hover:bg-teal-50">
                  <CheckCircle2 className="size-3" />
                  完成
                </Badge>
              )}
            </div>
          </CardHeader>

          <CardContent className="flex min-h-0 flex-1 flex-col gap-3 px-3.5 py-3">
            {error && (
              <div className="animate-shake shrink-0 rounded-md border border-destructive/20 bg-destructive/5 px-3 py-2 text-[12px] text-destructive">
                {error}
              </div>
            )}
            <div className="relative min-h-0 flex-1 overflow-y-auto rounded-md border border-primary/10 bg-white/45">
              <div className="space-y-0.5 p-3.5 font-mono text-[11.5px] leading-5 text-zinc-600">
                {logs.length === 0 ? (
                  <div className="flex h-full min-h-[5.5rem] flex-col justify-center gap-1.5 py-1">
                    <p className="wait-cursor text-[12.5px] font-medium text-zinc-800">等待操作…</p>
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
                        完成后拔盘重启，进系统干看着
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
            </div>
            <Separator />
            <p className="shrink-0 text-center text-[10px] text-muted-foreground">
              修好后拔 U 盘重启 · 进系统请干看着
            </p>
          </CardContent>
        </Card>
      </div>
    </div>
  )
}
