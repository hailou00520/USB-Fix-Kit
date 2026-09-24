import { useCallback, useEffect, useMemo, useState, type DragEvent } from 'react'
import {
  FolderOpen,
  FileSearch,
  Loader2,
  Upload,
  Trash2,
  ShieldAlert,
  Wifi,
  Stethoscope,
  RotateCcw,
  Cpu,
  Network,
  Wrench,
  Download,
  Upload as UploadIcon,
  Share2,
  HardDrive,
  ShieldOff,
  KeyRound,
  Plus,
  Settings2,
  FolderPlus,
  Link2,
  Copy,
  ExternalLink,
} from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import type { Locker, NetworkResult, ShareItem, ShareSettings, WifiProfile } from '@/lib/api'
import { cn } from '@/lib/utils'

async function waitApi(timeoutMs = 8000) {
  const start = Date.now()
  while (!window.pywebview?.api) {
    if (Date.now() - start > timeoutMs) throw new Error('桌面桥接未就绪')
    await new Promise((r) => setTimeout(r, 50))
  }
  return window.pywebview.api
}

type Tab = 'folder' | 'network' | 'share'

export default function App() {
  const [tab, setTab] = useState<Tab>('folder')
  const [path, setPath] = useState('')
  const [lockers, setLockers] = useState<Locker[]>([])
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [note, setNote] = useState('输入路径，或拖入文件/文件夹')
  const [isAdmin, setIsAdmin] = useState(false)
  const [busy, setBusy] = useState(false)
  const [dragging, setDragging] = useState(false)
  const [ready, setReady] = useState(false)
  const [netLog, setNetLog] = useState<string[]>(['点上方按钮诊断或修复 WiFi / 网络'])
  const [netTips, setNetTips] = useState<string[]>([])
  const [netBusy, setNetBusy] = useState(false)
  const [wifiProfiles, setWifiProfiles] = useState<WifiProfile[]>([])
  const [shareItems, setShareItems] = useState<ShareItem[]>([])
  const [shareSettings, setShareSettings] = useState<ShareSettings | null>(null)
  const [shareIps, setShareIps] = useState<string[]>([])
  const [shareAccess, setShareAccess] = useState<string[]>([])
  const [shareComputer, setShareComputer] = useState('')
  const [newSharePath, setNewSharePath] = useState('')
  const [newShareName, setNewShareName] = useState('')
  const [newSharePerm, setNewSharePerm] = useState<'full' | 'change' | 'read'>('full')
  const [mapUnc, setMapUnc] = useState('')
  const [mapLetter, setMapLetter] = useState('')

  const scan = useCallback(async (override?: string) => {
    const target = (override ?? path).trim().replace(/^"|"$/g, '')
    if (!target) {
      setNote('请先指定路径')
      return
    }
    setBusy(true)
    setNote('正在扫描…')
    try {
      const api = await waitApi()
      const res = await api.scan(target)
      if (!res.ok) {
        setNote(res.error || '扫描失败')
        setLockers([])
        setSelected(new Set())
        return
      }
      setPath(res.path || target)
      setLockers(res.lockers || [])
      setSelected(new Set())
      setIsAdmin(!!res.is_admin)
      setNote(res.note || '')
    } catch (e) {
      setNote(String(e))
    } finally {
      setBusy(false)
    }
  }, [path])

  useEffect(() => {
    waitApi()
      .then(async (api) => {
        const st = await api.get_status()
        setIsAdmin(!!st.is_admin)
        if (st.initial_path) {
          setPath(st.initial_path)
          void scan(st.initial_path)
        }
        setReady(true)
      })
      .catch((e) => {
        setNote(String(e))
        setReady(true)
      })

    const onNativePath = (ev: Event) => {
      const detail = (ev as CustomEvent<string>).detail
      if (!detail) return
      setDragging(false)
      setTab('folder')
      setPath(detail)
      setNote(`已拖入: ${detail}`)
      void scan(detail)
    }
    const onNativeErr = (ev: Event) => {
      setDragging(false)
      setNote(String((ev as CustomEvent<string>).detail || '拖放失败'))
    }
    window.addEventListener('native-path', onNativePath as EventListener)
    window.addEventListener('native-path-error', onNativeErr as EventListener)
    return () => {
      window.removeEventListener('native-path', onNativePath as EventListener)
      window.removeEventListener('native-path-error', onNativeErr as EventListener)
    }
  }, [scan])

  const allSelected = useMemo(
    () => lockers.length > 0 && lockers.every((l) => selected.has(l.pid)),
    [lockers, selected],
  )

  const browseFolder = async () => {
    const api = await waitApi()
    const p = await api.browse_folder()
    if (p) {
      setPath(p)
      await scan(p)
    }
  }

  const browseFile = async () => {
    const api = await waitApi()
    const p = await api.browse_file()
    if (p) {
      setPath(p)
      await scan(p)
    }
  }

  const toggle = (pid: number, on?: boolean) => {
    setSelected((prev) => {
      const next = new Set(prev)
      const enable = on ?? !next.has(pid)
      if (enable) next.add(pid)
      else next.delete(pid)
      return next
    })
  }

  const toggleAll = (on: boolean) => {
    setSelected(on ? new Set(lockers.map((l) => l.pid)) : new Set())
  }

  const kill = async (pids: number[]) => {
    if (!pids.length) {
      setNote('请先勾选要结束的进程')
      return
    }
    if (!window.confirm(`确定结束 ${pids.length} 个进程（含子进程）？`)) return
    setBusy(true)
    try {
      const api = await waitApi()
      const res = await api.kill(pids)
      setNote((res.messages || []).join(' · ') || res.error || '完成')
      await scan()
    } catch (e) {
      setNote(String(e))
    } finally {
      setBusy(false)
    }
  }

  const applyNetResult = (res: NetworkResult, title: string) => {
    setIsAdmin(!!res.is_admin)
    const lines = [`【${title}】${res.ok ? '完成' : '未完全成功'}`, ...(res.steps || [])]
    if (res.need_reboot) lines.push('⚠ 系统可能需要重启后完全生效')
    if (res.need_unplug) lines.push('⚠ 请拔插 USB 无线网卡，或使用「等待拔插恢复」')
    setNetLog(lines)
    setNetTips(res.suggestions || [])
    if (res.profiles) setWifiProfiles(res.profiles)
    if (res.shares) setShareItems(res.shares)
    if (res.settings) setShareSettings(res.settings)
    if (res.ips) setShareIps(res.ips)
    if (res.access_lines) setShareAccess(res.access_lines)
    if (res.computer) setShareComputer(res.computer)
  }

  const refreshShare = async () => {
    setNetBusy(true)
    try {
      const api = await waitApi()
      const res = await api.share_get_settings()
      applyNetResult(res, '共享设置')
    } catch (e) {
      setNetLog([String(e)])
    } finally {
      setNetBusy(false)
    }
  }

  useEffect(() => {
    if (tab !== 'share' || !ready) return
    void refreshShare()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, ready])

  const copyText = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text)
      setNetTips([`已复制: ${text}`])
    } catch {
      setNetTips([`复制失败，请手动选择: ${text}`])
    }
  }

  const pickShareFolder = async () => {
    const api = await waitApi()
    const p = await api.browse_folder()
    if (p) {
      setNewSharePath(p)
      const base = p.replace(/[/\\]+$/, '').split(/[/\\]/).pop() || ''
      if (!newShareName) setNewShareName(base)
    }
  }

  const runNet = async (
    title: string,
    action: (api: Awaited<ReturnType<typeof waitApi>>) => Promise<NetworkResult>,
    confirm?: string,
  ) => {
    if (confirm && !window.confirm(confirm)) return
    setNetBusy(true)
    setNetLog([`正在执行：${title}…`])
    setNetTips([])
    try {
      const api = await waitApi()
      const res = await action(api)
      applyNetResult(res, title)
    } catch (e) {
      setNetLog([String(e)])
    } finally {
      setNetBusy(false)
    }
  }

  const onDragOver = (e: DragEvent) => {
    e.preventDefault()
    setDragging(true)
  }

  return (
    <div
      className="flex h-full flex-col bg-[hsl(40_20%_98%)]"
      onDragEnter={onDragOver}
      onDragOver={onDragOver}
      onDragLeave={() => setDragging(false)}
    >
      <div className="mx-auto flex h-full w-full max-w-5xl flex-col gap-3 p-4">
        <div className="flex shrink-0 items-center justify-between gap-3">
          <div>
            <h1 className="text-xl font-semibold tracking-tight">畅通匣</h1>
            <p className="text-xs text-muted-foreground">解除占用 · 修复网络 · 文件共享 · WiFi 备份</p>
          </div>
          <Badge variant={isAdmin ? 'success' : 'warning'}>
            {isAdmin ? '管理员' : '普通权限'}
          </Badge>
        </div>

        <div className="flex shrink-0 gap-1 rounded-lg border bg-white p-1">
          <button
            type="button"
            className={cn(
              'flex flex-1 items-center justify-center gap-1.5 rounded-md px-2 py-2 text-sm font-medium transition-colors',
              tab === 'folder' ? 'bg-foreground text-background' : 'text-muted-foreground hover:bg-muted/60',
            )}
            onClick={() => setTab('folder')}
          >
            <FolderOpen className="h-4 w-4 shrink-0" />
            <span className="truncate">文件夹占用</span>
          </button>
          <button
            type="button"
            className={cn(
              'flex flex-1 items-center justify-center gap-1.5 rounded-md px-2 py-2 text-sm font-medium transition-colors',
              tab === 'network' ? 'bg-foreground text-background' : 'text-muted-foreground hover:bg-muted/60',
            )}
            onClick={() => setTab('network')}
          >
            <Wifi className="h-4 w-4 shrink-0" />
            <span className="truncate">网络修复</span>
          </button>
          <button
            type="button"
            className={cn(
              'flex flex-1 items-center justify-center gap-1.5 rounded-md px-2 py-2 text-sm font-medium transition-colors',
              tab === 'share' ? 'bg-foreground text-background' : 'text-muted-foreground hover:bg-muted/60',
            )}
            onClick={() => setTab('share')}
          >
            <Share2 className="h-4 w-4 shrink-0" />
            <span className="truncate">文件共享</span>
          </button>
        </div>

        {tab === 'folder' ? (
          <>
            <Card className="shrink-0">
              <CardHeader className="space-y-1 p-4 pb-2">
                <CardTitle className="text-base">目标路径</CardTitle>
                <CardDescription className="text-xs">输入、浏览，或拖入文件/文件夹</CardDescription>
              </CardHeader>
              <CardContent className="space-y-2 p-4 pt-0">
                <div className="flex flex-col gap-2 sm:flex-row">
                  <Input
                    value={path}
                    onChange={(e) => setPath(e.target.value)}
                    onKeyDown={(e) => e.key === 'Enter' && void scan()}
                    placeholder="例如 E:\AITEMP\5"
                    disabled={busy || !ready}
                    className="bg-white"
                  />
                  <div className="flex shrink-0 gap-2">
                    <Button variant="outline" onClick={() => void browseFolder()} disabled={busy || !ready}>
                      <FolderOpen />
                      选文件夹
                    </Button>
                    <Button variant="outline" onClick={() => void browseFile()} disabled={busy || !ready}>
                      <FileSearch />
                      选文件
                    </Button>
                  </div>
                </div>

                <div
                  className={cn(
                    'flex min-h-14 flex-col items-center justify-center rounded-lg border border-dashed bg-white px-3 py-3 text-center transition-colors',
                    dragging ? 'border-foreground bg-muted/40' : 'border-border',
                  )}
                >
                  <div className="flex items-center gap-2 text-sm">
                    <Upload className="h-4 w-4 text-muted-foreground" />
                    <span className="font-medium">
                      {dragging ? '松开以放入' : '拖放到此处（也可拖到整个窗口）'}
                    </span>
                  </div>
                </div>

                <div className="flex flex-wrap gap-2">
                  <Button onClick={() => void scan()} disabled={busy || !ready}>
                    {busy ? <Loader2 className="animate-spin" /> : <FileSearch />}
                    扫描占用
                  </Button>
                  <Button
                    variant="secondary"
                    onClick={() => void kill([...selected])}
                    disabled={busy || selected.size === 0}
                  >
                    <Trash2 />
                    结束选中
                  </Button>
                  <Button
                    variant="destructive"
                    onClick={() => void kill(lockers.map((l) => l.pid))}
                    disabled={busy || lockers.length === 0}
                  >
                    结束全部
                  </Button>
                </div>
              </CardContent>
            </Card>

            <Card className="flex min-h-[20rem] flex-1 flex-col overflow-hidden">
              <CardHeader className="shrink-0 space-y-1 p-4 pb-2">
                <div className="flex items-center justify-between gap-2">
                  <CardTitle className="text-base">占用进程</CardTitle>
                  <span className="text-xs text-muted-foreground">{lockers.length} 项</span>
                </div>
                <CardDescription className="flex items-center gap-1.5 text-xs">
                  {!isAdmin && <ShieldAlert className="h-3.5 w-3.5" />}
                  {note}
                </CardDescription>
              </CardHeader>
              <CardContent className="min-h-0 flex-1 overflow-auto p-4 pt-0">
                {lockers.length === 0 ? (
                  <div className="flex h-full min-h-[14rem] items-center justify-center rounded-lg border border-dashed text-sm text-muted-foreground">
                    暂无数据，先扫描一个路径
                  </div>
                ) : (
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead className="w-10">
                          <Checkbox
                            checked={allSelected}
                            onCheckedChange={(v) => toggleAll(v === true)}
                            aria-label="全选"
                          />
                        </TableHead>
                        <TableHead className="w-20">PID</TableHead>
                        <TableHead className="w-40">进程</TableHead>
                        <TableHead className="w-28">类型</TableHead>
                        <TableHead>原因</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {lockers.map((row) => (
                        <TableRow key={row.pid} data-state={selected.has(row.pid) ? 'selected' : undefined}>
                          <TableCell>
                            <Checkbox
                              checked={selected.has(row.pid)}
                              onCheckedChange={(v) => toggle(row.pid, v === true)}
                              aria-label={`选择 ${row.pid}`}
                            />
                          </TableCell>
                          <TableCell className="font-mono text-xs">{row.pid}</TableCell>
                          <TableCell>
                            <div className="font-medium">{row.name}</div>
                            {row.exe_path ? (
                              <div className="max-w-[220px] truncate text-xs text-muted-foreground" title={row.exe_path}>
                                {row.exe_path}
                              </div>
                            ) : null}
                          </TableCell>
                          <TableCell>
                            <Badge variant="secondary">{row.type_name}</Badge>
                          </TableCell>
                          <TableCell className="text-muted-foreground">{row.reason}</TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                )}
              </CardContent>
            </Card>
          </>
        ) : tab === 'network' ? (
          <>
            <Card className="shrink-0">
              <CardHeader className="space-y-1 p-4 pb-2">
                <CardTitle className="text-base">WiFi / 网络修复</CardTitle>
                <CardDescription className="text-xs">
                  仅修复服务/协议栈；不改 USB 省电、不删除设备，避免 USB 失灵
                  {!isAdmin ? ' · 需管理员权限' : ''}
                </CardDescription>
              </CardHeader>
              <CardContent className="space-y-3 p-4 pt-0">
                <div className="flex flex-wrap gap-2">
                  <Button
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet('诊断网络', (api) => api.network_diagnose())
                    }
                  >
                    {netBusy ? <Loader2 className="animate-spin" /> : <Stethoscope />}
                    诊断网络
                  </Button>
                  <Button
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet(
                        '一键修复 WiFi',
                        (api) => api.network_fix_wifi(),
                        '将重启 WLAN 等网络服务并刷新 DNS（不碰 USB、不删设备），网络可能短暂中断，继续？',
                      )
                    }
                  >
                    <Wifi />
                    一键修复 WiFi
                  </Button>
                  <Button
                    variant="secondary"
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet(
                        '重置网卡驱动',
                        (api) => api.network_fix_driver(),
                        '将仅重启 WLAN 服务（已禁用删设备 / 改 USB 省电）。若仍无效需人工拔插无线网卡或重启，继续？',
                      )
                    }
                  >
                    <Cpu />
                    重置网卡驱动
                  </Button>
                  <Button
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet(
                        '等待拔插恢复',
                        (api) => api.network_wait_usb(),
                        '仅监测：请自行拔掉 USB 无线网卡等 5 秒再插上。程序不会删设备、不会改 USB 设置。',
                      )
                    }
                  >
                    <RotateCcw />
                    等待拔插恢复
                  </Button>
                  <Button
                    variant="secondary"
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet(
                        '重置协议栈',
                        (api) => api.network_reset_stack(),
                        '将重置 Winsock / TCP-IP，部分情况需要重启，继续？',
                      )
                    }
                  >
                    <Network />
                    重置协议栈
                  </Button>
                  <Button
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() => {
                      setWifiProfiles([])
                      void runNet('备份 WiFi', (api) => api.network_backup_wifi())
                    }}
                  >
                    <Download />
                    备份 WiFi
                  </Button>
                  <Button
                    disabled={netBusy || !ready}
                    onClick={() => {
                      setWifiProfiles([])
                      void runNet(
                        '导入 WiFi',
                        (api) => api.network_restore_wifi(),
                        '将从软件目录 wifi_backup 导入全部 WiFi 并尝试自动连接（无需手输密码），继续？',
                      )
                    }}
                  >
                    <UploadIcon />
                    导入 WiFi
                  </Button>
                  <Button
                    variant="destructive"
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet(
                        '全面修复',
                        (api) => api.network_full_repair(),
                        '将依次执行：WLAN 服务软重启 → 服务修复 → 协议栈重置（不碰 USB、不删设备），继续？',
                      )
                    }
                  >
                    <Wrench />
                    全面修复
                  </Button>
                </div>
                <p className="text-xs text-muted-foreground">
                  WiFi：先「备份 WiFi」存到软件目录 wifi_backup，之后「导入 WiFi」会自动写入系统并尝试连接，不用手输密码。
                </p>
              </CardContent>
            </Card>

            <Card className="flex min-h-[20rem] flex-1 flex-col overflow-hidden">
              <CardHeader className="shrink-0 space-y-1 p-4 pb-2">
                <div className="flex items-center justify-between gap-2">
                  <CardTitle className="text-base">执行日志</CardTitle>
                  {netBusy ? (
                    <span className="flex items-center gap-1 text-xs text-muted-foreground">
                      <Loader2 className="h-3.5 w-3.5 animate-spin" />
                      进行中
                    </span>
                  ) : (
                    <RotateCcw className="h-3.5 w-3.5 text-muted-foreground" />
                  )}
                </div>
                {netTips.length > 0 ? (
                  <CardDescription className="space-y-1 text-xs">
                    {netTips.map((t) => (
                      <div key={t} className="flex gap-1.5">
                        <ShieldAlert className="mt-0.5 h-3.5 w-3.5 shrink-0" />
                        <span>{t}</span>
                      </div>
                    ))}
                  </CardDescription>
                ) : null}
              </CardHeader>
              <CardContent className="min-h-0 flex-1 space-y-3 overflow-auto p-4 pt-0">
                {wifiProfiles.length > 0 ? (
                  <div className="overflow-auto rounded-lg border bg-white">
                    <Table>
                      <TableHeader>
                        <TableRow>
                          <TableHead>WiFi 名称</TableHead>
                          <TableHead>密码</TableHead>
                          <TableHead className="w-28">认证</TableHead>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {wifiProfiles.map((p) => (
                          <TableRow key={p.ssid}>
                            <TableCell className="font-medium">{p.ssid}</TableCell>
                            <TableCell className="font-mono text-xs">{p.password}</TableCell>
                            <TableCell className="text-xs text-muted-foreground">
                              {p.auth || '—'}
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </div>
                ) : null}
                <pre className="min-h-[10rem] whitespace-pre-wrap break-words rounded-lg border bg-white p-3 font-mono text-xs leading-relaxed text-foreground">
                  {netLog.join('\n')}
                </pre>
              </CardContent>
            </Card>
          </>
        ) : (
          <>
            <Card className="shrink-0">
              <CardHeader className="space-y-1 p-4 pb-2">
                <div className="flex items-center justify-between gap-2">
                  <div>
                    <CardTitle className="text-base">共享设置</CardTitle>
                    <CardDescription className="text-xs">
                      本机被访问通道 · 高级共享 · 访问地址
                      {!isAdmin ? ' · 需管理员权限' : ''}
                    </CardDescription>
                  </div>
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() => void refreshShare()}
                  >
                    <RotateCcw />
                    刷新
                  </Button>
                </div>
              </CardHeader>
              <CardContent className="space-y-3 p-4 pt-0">
                <div className="flex flex-wrap gap-2 text-xs">
                  <Badge variant={shareSettings?.server_running ? 'success' : 'warning'}>
                    Server {shareSettings?.server_running ? '运行中' : '未运行'}
                  </Badge>
                  <Badge variant={shareSettings?.firewall_file_share ? 'success' : 'warning'}>
                    文件共享防火墙 {shareSettings?.firewall_file_share ? '开' : '关'}
                  </Badge>
                  <Badge variant={shareSettings?.firewall_discovery ? 'success' : 'warning'}>
                    网络发现 {shareSettings?.firewall_discovery ? '开' : '关'}
                  </Badge>
                  <Badge variant={shareSettings?.password_protected ? 'secondary' : 'success'}>
                    密码保护 {shareSettings?.password_protected ? '开' : '关'}
                  </Badge>
                  {shareComputer ? <Badge variant="outline">{shareComputer}</Badge> : null}
                  {shareIps.slice(0, 3).map((ip) => (
                    <Badge key={ip} variant="outline">
                      {ip}
                    </Badge>
                  ))}
                </div>

                <div className="flex flex-wrap gap-2">
                  <Button
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet(
                        '一键开启本机共享',
                        (api) => api.share_enable_hosting(true),
                        '将：设为专用网络 → 开发现/防火墙 → 启动共享服务 → 关闭密码保护（家庭局域网）。继续？',
                      )
                    }
                  >
                    <Settings2 />
                    一键开启本机共享
                  </Button>
                  <Button
                    variant="secondary"
                    disabled={netBusy || !ready}
                    onClick={() => void runNet('设为专用网络', (api) => api.share_fix_private_profile())}
                  >
                    <Network />
                    设为专用网络
                  </Button>
                  <Button
                    variant="secondary"
                    disabled={netBusy || !ready}
                    onClick={() => void runNet('修复网络发现', (api) => api.share_fix_discovery())}
                  >
                    <Share2 />
                    开网络发现
                  </Button>
                  <Button
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() => void runNet('放行共享防火墙', (api) => api.share_fix_firewall())}
                  >
                    <ShieldAlert />
                    开文件共享防火墙
                  </Button>
                  <Button
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet(
                        '关闭密码保护',
                        (api) => api.share_disable_password_protected(),
                        '家庭互访常用，会降低安全性。继续？',
                      )
                    }
                  >
                    <ShieldOff />
                    关密码保护
                  </Button>
                  <Button
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() => void runNet('开启密码保护', (api) => api.share_enable_password_protected())}
                  >
                    <KeyRound />
                    开密码保护
                  </Button>
                  <Button
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() => void runNet('启用公用文件夹', (api) => api.share_enable_public_folder())}
                  >
                    <FolderPlus />
                    启用公用文件夹
                  </Button>
                  <Button
                    variant="ghost"
                    disabled={netBusy || !ready}
                    onClick={() => void runNet('打开系统共享面板', (api) => api.share_open_advanced_panel())}
                  >
                    <ExternalLink />
                    系统高级共享设置
                  </Button>
                </div>

                {shareAccess.length > 0 ? (
                  <div className="rounded-lg border bg-white p-3">
                    <p className="mb-2 text-xs font-medium text-muted-foreground">局域网访问地址（点复制）</p>
                    <div className="flex flex-wrap gap-2">
                      {shareAccess.slice(0, 10).map((line) => (
                        <button
                          key={line}
                          type="button"
                          className="inline-flex items-center gap-1 rounded-md border px-2 py-1 font-mono text-xs hover:bg-muted/50"
                          onClick={() => void copyText(line)}
                          title="复制"
                        >
                          <Copy className="h-3 w-3" />
                          {line}
                        </button>
                      ))}
                    </div>
                  </div>
                ) : null}
              </CardContent>
            </Card>

            <Card className="shrink-0">
              <CardHeader className="space-y-1 p-4 pb-2">
                <CardTitle className="text-base">共享功能</CardTitle>
                <CardDescription className="text-xs">
                  创建 / 删除共享文件夹 · 打开网络位置 · 映射网络驱动器
                </CardDescription>
              </CardHeader>
              <CardContent className="space-y-3 p-4 pt-0">
                <div className="flex flex-col gap-2 sm:flex-row">
                  <Input
                    value={newSharePath}
                    onChange={(e) => setNewSharePath(e.target.value)}
                    placeholder="要共享的文件夹路径"
                    disabled={netBusy || !ready}
                    className="bg-white"
                  />
                  <Button variant="outline" disabled={netBusy || !ready} onClick={() => void pickShareFolder()}>
                    <FolderOpen />
                    选文件夹
                  </Button>
                </div>
                <div className="flex flex-col gap-2 sm:flex-row">
                  <Input
                    value={newShareName}
                    onChange={(e) => setNewShareName(e.target.value)}
                    placeholder="共享名（可空=文件夹名）"
                    disabled={netBusy || !ready}
                    className="bg-white sm:max-w-[12rem]"
                  />
                  <select
                    className="h-9 rounded-md border bg-white px-2 text-sm"
                    value={newSharePerm}
                    disabled={netBusy || !ready}
                    onChange={(e) => setNewSharePerm(e.target.value as 'full' | 'change' | 'read')}
                  >
                    <option value="full">Everyone 完全控制</option>
                    <option value="change">Everyone 更改</option>
                    <option value="read">Everyone 只读</option>
                  </select>
                  <Button
                    disabled={netBusy || !ready || !newSharePath.trim()}
                    onClick={() =>
                      void runNet('创建共享', (api) =>
                        api.share_create(newSharePath.trim(), newShareName.trim(), newSharePerm),
                      )
                    }
                  >
                    <Plus />
                    创建共享
                  </Button>
                  <Button
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet('打开本机网络', (api) => api.share_open_path(`\\\\${shareComputer || '.'}`))
                    }
                  >
                    <ExternalLink />
                    打开 \\\\本机
                  </Button>
                </div>

                <div className="flex flex-col gap-2 sm:flex-row">
                  <Input
                    value={mapUnc}
                    onChange={(e) => setMapUnc(e.target.value)}
                    placeholder="映射 UNC，如 \\192.168.1.8\资料"
                    disabled={netBusy || !ready}
                    className="bg-white"
                  />
                  <Input
                    value={mapLetter}
                    onChange={(e) => setMapLetter(e.target.value)}
                    placeholder="盘符(可空)"
                    disabled={netBusy || !ready}
                    className="bg-white sm:max-w-[7rem]"
                  />
                  <Button
                    variant="secondary"
                    disabled={netBusy || !ready || !mapUnc.trim()}
                    onClick={() =>
                      void runNet('映射网络驱动器', (api) => api.share_map_drive(mapUnc.trim(), mapLetter.trim()))
                    }
                  >
                    <Link2 />
                    映射驱动器
                  </Button>
                </div>

                <div className="overflow-hidden rounded-lg border">
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>共享名</TableHead>
                        <TableHead>路径</TableHead>
                        <TableHead className="hidden sm:table-cell">权限</TableHead>
                        <TableHead className="w-[9rem]">操作</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {shareItems.length === 0 ? (
                        <TableRow>
                          <TableCell colSpan={4} className="text-center text-xs text-muted-foreground">
                            暂无共享，点「创建共享」或「刷新」
                          </TableCell>
                        </TableRow>
                      ) : (
                        shareItems.map((sh) => (
                          <TableRow key={sh.name}>
                            <TableCell className="font-mono text-xs">{sh.name}</TableCell>
                            <TableCell className="max-w-[14rem] truncate text-xs" title={sh.path}>
                              {sh.path}
                            </TableCell>
                            <TableCell className="hidden max-w-[10rem] truncate text-xs text-muted-foreground sm:table-cell">
                              {sh.access || (sh.special ? '系统' : '—')}
                            </TableCell>
                            <TableCell>
                              <div className="flex flex-wrap gap-1">
                                <Button
                                  size="sm"
                                  variant="ghost"
                                  disabled={netBusy}
                                  onClick={() => void copyText(sh.unc_name || `\\\\${shareComputer}\\${sh.name}`)}
                                >
                                  <Copy className="h-3.5 w-3.5" />
                                </Button>
                                <Button
                                  size="sm"
                                  variant="ghost"
                                  disabled={netBusy}
                                  onClick={() =>
                                    void runNet('打开共享', (api) =>
                                      api.share_open_path(sh.unc_name || `\\\\${shareComputer}\\${sh.name}`),
                                    )
                                  }
                                >
                                  <ExternalLink className="h-3.5 w-3.5" />
                                </Button>
                                {!sh.special && !String(sh.name).endsWith('$') ? (
                                  <Button
                                    size="sm"
                                    variant="ghost"
                                    disabled={netBusy}
                                    onClick={() =>
                                      void runNet(
                                        '删除共享',
                                        (api) => api.share_remove(sh.name),
                                        `仅删除共享「${sh.name}」，不删磁盘文件。继续？`,
                                      )
                                    }
                                  >
                                    <Trash2 className="h-3.5 w-3.5" />
                                  </Button>
                                ) : null}
                              </div>
                            </TableCell>
                          </TableRow>
                        ))
                      )}
                    </TableBody>
                  </Table>
                </div>
              </CardContent>
            </Card>

            <Card className="shrink-0">
              <CardHeader className="space-y-1 p-4 pb-2">
                <CardTitle className="text-base">疑难杂症修复</CardTitle>
                <CardDescription className="text-xs">
                  Win11 24H2 NAS/来宾、SMB 签名、凭据缓存、SMB1 等
                </CardDescription>
              </CardHeader>
              <CardContent className="space-y-3 p-4 pt-0">
                <div className="flex flex-wrap gap-2">
                  <Button
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() => void runNet('诊断共享', (api) => api.share_diagnose())}
                  >
                    {netBusy ? <Loader2 className="animate-spin" /> : <Stethoscope />}
                    诊断共享
                  </Button>
                  <Button
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet(
                        '修复 Win11/NAS 来宾',
                        (api) => api.share_fix_win11_nas(),
                        '将允许不安全来宾登录并关闭强制 SMB 签名（家庭局域网访问 NAS 常用）。会降低安全性，仅建议信任网络。继续？',
                      )
                    }
                  >
                    <HardDrive />
                    修复 Win11/NAS
                  </Button>
                  <Button
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() => void runNet('重启 SMB 服务', (api) => api.share_restart_smb())}
                  >
                    <RotateCcw />
                    重启 SMB 服务
                  </Button>
                  <Button
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet(
                        '清理共享凭据',
                        (api) => api.share_clear_credentials(),
                        '将删除凭据管理器中可能过期的网络共享账号，下次访问需重新输入密码。继续？',
                      )
                    }
                  >
                    <KeyRound />
                    清理共享凭据
                  </Button>
                  <Button
                    variant="outline"
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet(
                        '启用 SMB1',
                        (api) => api.share_enable_smb1(),
                        'SMB1 不安全，仅极旧 NAS/XP 设备需要，可能要重启。确定启用？',
                      )
                    }
                  >
                    <HardDrive />
                    启用 SMB1（不推荐）
                  </Button>
                  <Button
                    variant="destructive"
                    disabled={netBusy || !ready}
                    onClick={() =>
                      void runNet(
                        '全面修复共享',
                        (api) => api.share_full_repair(),
                        '将依次：专用网络 → 发现服务 → 防火墙 → SMB 服务 → Win11/NAS 来宾签名 → 清凭据。继续？',
                      )
                    }
                  >
                    <Wrench />
                    全面修复共享
                  </Button>
                </div>
                <p className="text-xs text-muted-foreground">
                  访问别人电脑/NAS 报 0x80070035、「组织策略阻止来宾」优先点「修复 Win11/NAS」。访问尽量用 \\IP\共享名。
                </p>
              </CardContent>
            </Card>

            <Card className="flex min-h-[16rem] flex-1 flex-col overflow-hidden">
              <CardHeader className="shrink-0 space-y-1 p-4 pb-2">
                <div className="flex items-center justify-between gap-2">
                  <CardTitle className="text-base">执行日志</CardTitle>
                  {netBusy ? (
                    <span className="flex items-center gap-1 text-xs text-muted-foreground">
                      <Loader2 className="h-3.5 w-3.5 animate-spin" />
                      进行中
                    </span>
                  ) : (
                    <Share2 className="h-3.5 w-3.5 text-muted-foreground" />
                  )}
                </div>
                {netTips.length > 0 ? (
                  <CardDescription className="space-y-1 text-xs">
                    {netTips.map((t) => (
                      <div key={t} className="flex gap-1.5">
                        <ShieldAlert className="mt-0.5 h-3.5 w-3.5 shrink-0" />
                        <span>{t}</span>
                      </div>
                    ))}
                  </CardDescription>
                ) : null}
              </CardHeader>
              <CardContent className="min-h-0 flex-1 overflow-auto p-4 pt-0">
                <pre className="min-h-[12rem] whitespace-pre-wrap break-words rounded-lg border bg-white p-3 font-mono text-xs leading-relaxed text-foreground">
                  {netLog.join('\n') || '在上方设置共享、创建文件夹，或排查疑难问题'}
                </pre>
              </CardContent>
            </Card>
          </>
        )}
      </div>
    </div>
  )
}
