#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
畅通匣 — Windows 急救小工具
  · 解除文件夹/文件占用
  · 诊断与修复 WiFi / 网络
  · 备份与一键导入 WiFi 配置

占用检测（多重交叉，尽量覆盖全部情况；扫描阶段多线程并行）：
  1) Restart Manager — 注册目录内文件
  2) FileProcessIdsUsingFile — 目录/文件当前打开句柄的 PID 列表
  3) 进程工作目录（cwd）落在目标路径下
  4) 进程映像路径 / 已加载模块位于目标路径下
  5) 命令行参数引用了目标路径
  6) 系统句柄表补检（限时）
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from pathlib import Path


# ---------------------------------------------------------------------------
# 常量 / 结构
# ---------------------------------------------------------------------------

CCH_RM_SESSION_KEY = 32
CCH_RM_MAX_APP_NAME = 255
CCH_RM_MAX_SVC_NAME = 63

RmUnknownApp = 0
RmMainWindow = 1
RmOtherWindow = 2
RmService = 3
RmExplorer = 4
RmCritical = 5

APP_TYPE_NAMES = {
    RmUnknownApp: "未知",
    RmMainWindow: "主窗口程序",
    RmOtherWindow: "窗口程序",
    RmService: "服务",
    RmExplorer: "资源管理器",
    RmCritical: "系统关键",
}

ERROR_SUCCESS = 0
ERROR_MORE_DATA = 234
ERROR_ACCESS_DENIED = 5

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
PROCESS_DUP_HANDLE = 0x0040
CREATE_NO_WINDOW = 0x08000000

FILE_READ_ATTRIBUTES = 0x80
FILE_SHARE_ALL = 0x1 | 0x2 | 0x4
OPEN_EXISTING = 3
FILE_FLAG_BACKUP_SEMANTICS = 0x02000000
FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000

FileProcessIdsUsingFileInformation = 47
ObjectNameInformation = 1
SystemExtendedHandleInformation = 64
STATUS_SUCCESS = 0
STATUS_INFO_LENGTH_MISMATCH = 0xC0000004
STATUS_BUFFER_OVERFLOW = 0x80000005
STATUS_BUFFER_TOO_SMALL = 0xC0000023


def _ntstatus(value: int) -> int:
    return int(value) & 0xFFFFFFFF

MAX_PATHS_FOR_SCAN = 8000
RM_BATCH = 64
HANDLE_SCAN_NAME_WORKERS = 24
# 并行度
OPEN_FILE_WORKERS = min(128, max(32, (os.cpu_count() or 8) * 8))
PROCESS_WORKERS = min(32, max(8, (os.cpu_count() or 8) * 2))
SCAN_POOL_WORKERS = 8

DUPLICATE_SAME_ACCESS = 0x2


class FILETIME(ctypes.Structure):
    _fields_ = [("dwLowDateTime", wt.DWORD), ("dwHighDateTime", wt.DWORD)]


class RM_UNIQUE_PROCESS(ctypes.Structure):
    _fields_ = [("dwProcessId", wt.DWORD), ("ProcessStartTime", FILETIME)]


class RM_PROCESS_INFO(ctypes.Structure):
    _fields_ = [
        ("Process", RM_UNIQUE_PROCESS),
        ("strAppName", wt.WCHAR * (CCH_RM_MAX_APP_NAME + 1)),
        ("strServiceShortName", wt.WCHAR * (CCH_RM_MAX_SVC_NAME + 1)),
        ("ApplicationType", ctypes.c_uint),
        ("AppStatus", wt.ULONG),
        ("TSSessionId", wt.DWORD),
        ("bRestartable", wt.BOOL),
    ]


class PROCESS_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("Reserved1", ctypes.c_void_p),
        ("PebBaseAddress", ctypes.c_void_p),
        ("Reserved2", ctypes.c_void_p * 2),
        ("UniqueProcessId", ctypes.c_void_p),
        ("Reserved3", ctypes.c_void_p),
    ]


class UNICODE_STRING(ctypes.Structure):
    _fields_ = [
        ("Length", wt.USHORT),
        ("MaximumLength", wt.USHORT),
        ("Buffer", ctypes.c_void_p),
    ]


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wt.DWORD),
        ("cntUsage", wt.DWORD),
        ("th32ProcessID", wt.DWORD),
        ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
        ("th32ModuleID", wt.DWORD),
        ("cntThreads", wt.DWORD),
        ("th32ParentProcessID", wt.DWORD),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", wt.DWORD),
        ("szExeFile", wt.WCHAR * 260),
    ]


class SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX(ctypes.Structure):
    _fields_ = [
        ("Object", ctypes.c_void_p),
        ("UniqueProcessId", ctypes.c_size_t),
        ("HandleValue", ctypes.c_size_t),
        ("GrantedAccess", wt.ULONG),
        ("CreatorBackTraceIndex", wt.USHORT),
        ("ObjectTypeIndex", wt.USHORT),
        ("HandleAttributes", wt.ULONG),
        ("Reserved", wt.ULONG),
    ]


class IO_STATUS_BLOCK(ctypes.Structure):
    _fields_ = [
        ("Status", ctypes.c_long),
        ("Information", ctypes.c_size_t),
    ]


RSTRTMGR = ctypes.WinDLL("rstrtmgr")
KERNEL32 = ctypes.WinDLL("kernel32", use_last_error=True)
NTDLL = ctypes.WinDLL("ntdll")
PSAPI = ctypes.WinDLL("psapi")

RSTRTMGR.RmStartSession.argtypes = [ctypes.POINTER(wt.DWORD), wt.DWORD, wt.LPWSTR]
RSTRTMGR.RmStartSession.restype = wt.DWORD
RSTRTMGR.RmRegisterResources.argtypes = [
    wt.DWORD,
    wt.UINT,
    ctypes.POINTER(ctypes.c_wchar_p),
    wt.UINT,
    ctypes.c_void_p,
    wt.UINT,
    ctypes.c_void_p,
]
RSTRTMGR.RmRegisterResources.restype = wt.DWORD
RSTRTMGR.RmGetList.argtypes = [
    wt.DWORD,
    ctypes.POINTER(wt.UINT),
    ctypes.POINTER(wt.UINT),
    ctypes.POINTER(RM_PROCESS_INFO),
    ctypes.POINTER(wt.DWORD),
]
RSTRTMGR.RmGetList.restype = wt.DWORD
RSTRTMGR.RmEndSession.argtypes = [wt.DWORD]
RSTRTMGR.RmEndSession.restype = wt.DWORD

KERNEL32.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]
KERNEL32.CreateToolhelp32Snapshot.restype = wt.HANDLE
KERNEL32.Process32FirstW.argtypes = [wt.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
KERNEL32.Process32FirstW.restype = wt.BOOL
KERNEL32.Process32NextW.argtypes = [wt.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
KERNEL32.Process32NextW.restype = wt.BOOL
KERNEL32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
KERNEL32.OpenProcess.restype = wt.HANDLE
KERNEL32.ReadProcessMemory.argtypes = [
    wt.HANDLE,
    ctypes.c_void_p,
    ctypes.c_void_p,
    ctypes.c_size_t,
    ctypes.POINTER(ctypes.c_size_t),
]
KERNEL32.ReadProcessMemory.restype = wt.BOOL
KERNEL32.CloseHandle.argtypes = [wt.HANDLE]
KERNEL32.CloseHandle.restype = wt.BOOL
KERNEL32.QueryFullProcessImageNameW.argtypes = [
    wt.HANDLE,
    wt.DWORD,
    wt.LPWSTR,
    ctypes.POINTER(wt.DWORD),
]
KERNEL32.QueryFullProcessImageNameW.restype = wt.BOOL
KERNEL32.CreateFileW.argtypes = [
    wt.LPCWSTR,
    wt.DWORD,
    wt.DWORD,
    ctypes.c_void_p,
    wt.DWORD,
    wt.DWORD,
    wt.HANDLE,
]
KERNEL32.CreateFileW.restype = wt.HANDLE
KERNEL32.DuplicateHandle.argtypes = [
    wt.HANDLE,
    wt.HANDLE,
    wt.HANDLE,
    ctypes.POINTER(wt.HANDLE),
    wt.DWORD,
    wt.BOOL,
    wt.DWORD,
]
KERNEL32.DuplicateHandle.restype = wt.BOOL
KERNEL32.GetCurrentProcess.restype = wt.HANDLE
KERNEL32.GetLogicalDrives.restype = wt.DWORD
KERNEL32.QueryDosDeviceW.argtypes = [wt.LPCWSTR, wt.LPWSTR, wt.DWORD]
KERNEL32.QueryDosDeviceW.restype = wt.DWORD

NTDLL.NtQueryInformationProcess.argtypes = [
    wt.HANDLE,
    ctypes.c_int,
    ctypes.c_void_p,
    wt.ULONG,
    ctypes.POINTER(wt.ULONG),
]
NTDLL.NtQueryInformationProcess.restype = ctypes.c_long
NTDLL.NtQueryInformationFile.argtypes = [
    wt.HANDLE,
    ctypes.POINTER(IO_STATUS_BLOCK),
    ctypes.c_void_p,
    wt.ULONG,
    ctypes.c_int,
]
NTDLL.NtQueryInformationFile.restype = ctypes.c_long
NTDLL.NtQuerySystemInformation.argtypes = [
    ctypes.c_int,
    ctypes.c_void_p,
    wt.ULONG,
    ctypes.POINTER(wt.ULONG),
]
NTDLL.NtQuerySystemInformation.restype = ctypes.c_long
NTDLL.NtQueryObject.argtypes = [
    wt.HANDLE,
    ctypes.c_int,
    ctypes.c_void_p,
    wt.ULONG,
    ctypes.POINTER(wt.ULONG),
]
NTDLL.NtQueryObject.restype = ctypes.c_long

PSAPI.EnumProcessModulesEx.argtypes = [
    wt.HANDLE,
    ctypes.POINTER(wt.HMODULE),
    wt.DWORD,
    ctypes.POINTER(wt.DWORD),
    wt.DWORD,
]
PSAPI.EnumProcessModulesEx.restype = wt.BOOL
PSAPI.GetModuleFileNameExW.argtypes = [
    wt.HANDLE,
    wt.HMODULE,
    wt.LPWSTR,
    wt.DWORD,
]
PSAPI.GetModuleFileNameExW.restype = wt.DWORD

IS_64BIT = ctypes.sizeof(ctypes.c_void_p) == 8
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value
LIST_MODULES_ALL = 0x03


@dataclass
class Locker:
    pid: int
    name: str
    app_type: int
    reason: str
    exe_path: str = ""
    service: str = ""

    @property
    def type_name(self) -> str:
        return APP_TYPE_NAMES.get(self.app_type, f"类型{self.app_type}")


# ---------------------------------------------------------------------------
# 基础工具
# ---------------------------------------------------------------------------

def _is_admin() -> bool:
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def _norm(path: str | Path) -> str:
    return os.path.normcase(os.path.abspath(str(path))).rstrip("\\")


def _under(child: str, parent: str) -> bool:
    c, p = _norm(child), _norm(parent)
    return c == p or c.startswith(p + os.sep)


def _process_image_path(pid: int) -> str:
    handle = KERNEL32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return ""
    try:
        buf = ctypes.create_unicode_buffer(4096)
        size = wt.DWORD(len(buf))
        if KERNEL32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
            return buf.value
        return ""
    finally:
        KERNEL32.CloseHandle(handle)


# 单次扫描内缓存映像路径，避免重复 OpenProcess
_image_path_cache: dict[int, str] = {}
_image_path_lock = __import__("threading").Lock()


def _process_image_path_cached(pid: int) -> str:
    with _image_path_lock:
        hit = _image_path_cache.get(pid)
        if hit is not None:
            return hit
    path = _process_image_path(pid)
    with _image_path_lock:
        _image_path_cache[pid] = path
    return path


def _clear_scan_caches() -> None:
    with _image_path_lock:
        _image_path_cache.clear()


def _process_name(pid: int) -> str:
    path = _process_image_path_cached(pid)
    return Path(path).name if path else f"PID {pid}"


def _iter_pids() -> list[tuple[int, str]]:
    out: list[tuple[int, str]] = []
    TH32CS_SNAPPROCESS = 0x00000002
    snap = KERNEL32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == INVALID_HANDLE_VALUE or not snap:
        return out
    try:
        entry = PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        if not KERNEL32.Process32FirstW(snap, ctypes.byref(entry)):
            return out
        while True:
            pid = int(entry.th32ProcessID)
            if pid and pid != os.getpid():
                out.append((pid, entry.szExeFile or ""))
            if not KERNEL32.Process32NextW(snap, ctypes.byref(entry)):
                break
    finally:
        KERNEL32.CloseHandle(snap)
    return out


def _make_locker(pid: int, reason: str, name: str = "", app_type: int = RmUnknownApp, service: str = "") -> Locker:
    exe = _process_image_path_cached(pid)
    return Locker(
        pid=pid,
        name=name or (Path(exe).name if exe else f"PID {pid}"),
        app_type=app_type,
        reason=reason,
        exe_path=exe,
        service=service,
    )


# ---------------------------------------------------------------------------
# 读取进程当前工作目录（PEB）
# ---------------------------------------------------------------------------

def _read_process_memory(handle, address, size: int) -> bytes | None:
    if not address:
        return None
    buf = (ctypes.c_char * size)()
    read = ctypes.c_size_t(0)
    ok = KERNEL32.ReadProcessMemory(
        handle, ctypes.c_void_p(address), buf, size, ctypes.byref(read)
    )
    if not ok or read.value == 0:
        return None
    return bytes(buf[: read.value])


def _ptr_from(data: bytes, offset: int = 0) -> int:
    width = ctypes.sizeof(ctypes.c_void_p)
    return int.from_bytes(data[offset : offset + width], "little")


def get_process_cwd(pid: int) -> str | None:
    access = PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
    handle = KERNEL32.OpenProcess(access, False, pid)
    if not handle:
        access = PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ
        handle = KERNEL32.OpenProcess(access, False, pid)
    if not handle:
        return None

    try:
        pbi = PROCESS_BASIC_INFORMATION()
        status = NTDLL.NtQueryInformationProcess(
            handle, 0, ctypes.byref(pbi), ctypes.sizeof(pbi), None
        )
        if status != 0 or not pbi.PebBaseAddress:
            return None

        params_off = 0x20 if IS_64BIT else 0x10
        width = ctypes.sizeof(ctypes.c_void_p)
        peb_chunk = _read_process_memory(
            handle, pbi.PebBaseAddress, params_off + width
        )
        if not peb_chunk:
            return None
        params_addr = _ptr_from(peb_chunk, params_off)
        if not params_addr:
            return None

        curdir_off = 0x38 if IS_64BIT else 0x24
        us_raw = _read_process_memory(
            handle, params_addr + curdir_off, ctypes.sizeof(UNICODE_STRING)
        )
        if not us_raw:
            return None
        us = UNICODE_STRING.from_buffer_copy(us_raw)
        if not us.Buffer or us.Length == 0:
            return None

        raw = _read_process_memory(handle, us.Buffer, us.Length)
        if not raw:
            return None
        text = raw.decode("utf-16-le", errors="ignore").rstrip("\x00")
        return text.rstrip("\\") if text else None
    except Exception:
        return None
    finally:
        KERNEL32.CloseHandle(handle)


def find_cwd_lockers(target: Path) -> list[Locker]:
    target_n = _norm(target)
    result: list[Locker] = []

    def _one(pid: int, exe_name: str) -> Locker | None:
        cwd = get_process_cwd(pid)
        if cwd and _under(cwd, target_n):
            return _make_locker(pid, f"工作目录: {cwd}", name=exe_name)
        return None

    with ThreadPoolExecutor(max_workers=PROCESS_WORKERS) as pool:
        futs = [pool.submit(_one, pid, name) for pid, name in _iter_pids()]
        for fut in as_completed(futs):
            try:
                item = fut.result()
            except Exception:
                continue
            if item:
                result.append(item)
    return result


def _inspect_one_process(pid: int, exe_name: str, target_n: str) -> list[Locker]:
    """对单个进程并行检查：cwd / 映像 / 模块。"""
    out: list[Locker] = []
    cwd = get_process_cwd(pid)
    if cwd and _under(cwd, target_n):
        out.append(_make_locker(pid, f"工作目录: {cwd}", name=exe_name))

    image = _process_image_path_cached(pid)
    if image and _under(image, target_n):
        out.append(
            _make_locker(
                pid,
                f"进程映像位于目标: {image}",
                name=exe_name or Path(image).name,
            )
        )

    handle = KERNEL32.OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid
    )
    if not handle:
        handle = KERNEL32.OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, False, pid
        )
    if not handle:
        return out
    try:
        needed = wt.DWORD(0)
        PSAPI.EnumProcessModulesEx(handle, None, 0, ctypes.byref(needed), LIST_MODULES_ALL)
        if needed.value == 0:
            return out
        count = max(needed.value // ctypes.sizeof(wt.HMODULE), 1)
        arr = (wt.HMODULE * count)()
        if not PSAPI.EnumProcessModulesEx(
            handle, arr, ctypes.sizeof(arr), ctypes.byref(needed), LIST_MODULES_ALL
        ):
            return out
        real_count = needed.value // ctypes.sizeof(wt.HMODULE)
        buf = ctypes.create_unicode_buffer(1024)
        for i in range(min(real_count, count)):
            if not PSAPI.GetModuleFileNameExW(handle, arr[i], buf, len(buf)):
                continue
            mod = buf.value
            if mod and _under(mod, target_n):
                out.append(_make_locker(pid, f"已加载模块: {mod}", name=exe_name))
                break
    finally:
        KERNEL32.CloseHandle(handle)
    return out


def find_process_attr_lockers(target: Path) -> list[Locker]:
    """
    一次枚举进程，多线程并行查 cwd / 映像 / 模块。
    覆盖原先 find_cwd + find_image + find_module 的全部能力。
    """
    target_n = _norm(target)
    pids = _iter_pids()
    result: list[Locker] = []
    with ThreadPoolExecutor(max_workers=PROCESS_WORKERS) as pool:
        futs = [
            pool.submit(_inspect_one_process, pid, exe_name, target_n)
            for pid, exe_name in pids
        ]
        for fut in as_completed(futs):
            try:
                result.extend(fut.result())
            except Exception:
                continue
    return result


def find_image_lockers(target: Path) -> list[Locker]:
    target_n = _norm(target)
    result: list[Locker] = []

    def _one(pid: int, exe_name: str) -> Locker | None:
        image = _process_image_path_cached(pid)
        if image and _under(image, target_n):
            return _make_locker(
                pid, f"进程映像位于目标: {image}", name=exe_name or Path(image).name
            )
        return None

    with ThreadPoolExecutor(max_workers=PROCESS_WORKERS) as pool:
        futs = [pool.submit(_one, pid, name) for pid, name in _iter_pids()]
        for fut in as_completed(futs):
            try:
                item = fut.result()
            except Exception:
                continue
            if item:
                result.append(item)
    return result


def find_module_lockers(target: Path) -> list[Locker]:
    """已从目标路径加载 DLL/模块的进程。"""
    target_n = _norm(target)
    result: list[Locker] = []

    def _one(pid: int, exe_name: str) -> Locker | None:
        handle = KERNEL32.OpenProcess(
            PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid
        )
        if not handle:
            handle = KERNEL32.OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, False, pid
            )
        if not handle:
            return None
        try:
            needed = wt.DWORD(0)
            PSAPI.EnumProcessModulesEx(
                handle, None, 0, ctypes.byref(needed), LIST_MODULES_ALL
            )
            if needed.value == 0:
                return None
            count = max(needed.value // ctypes.sizeof(wt.HMODULE), 1)
            arr = (wt.HMODULE * count)()
            if not PSAPI.EnumProcessModulesEx(
                handle, arr, ctypes.sizeof(arr), ctypes.byref(needed), LIST_MODULES_ALL
            ):
                return None
            real_count = needed.value // ctypes.sizeof(wt.HMODULE)
            buf = ctypes.create_unicode_buffer(1024)
            for i in range(min(real_count, count)):
                if not PSAPI.GetModuleFileNameExW(handle, arr[i], buf, len(buf)):
                    continue
                mod = buf.value
                if mod and _under(mod, target_n):
                    return _make_locker(pid, f"已加载模块: {mod}", name=exe_name)
            return None
        finally:
            KERNEL32.CloseHandle(handle)

    with ThreadPoolExecutor(max_workers=PROCESS_WORKERS) as pool:
        futs = [pool.submit(_one, pid, name) for pid, name in _iter_pids()]
        for fut in as_completed(futs):
            try:
                item = fut.result()
            except Exception:
                continue
            if item:
                result.append(item)
    return result


# ---------------------------------------------------------------------------
# FileProcessIdsUsingFile（目录/文件打开者）
# ---------------------------------------------------------------------------

def _open_path_for_query(path: str, is_dir: bool = False):
    # 目录必须带 BACKUP_SEMANTICS；普通文件去掉可明显加快 CreateFile
    flags = FILE_FLAG_OPEN_REPARSE_POINT
    if is_dir:
        flags |= FILE_FLAG_BACKUP_SEMANTICS
    handle = KERNEL32.CreateFileW(
        path,
        FILE_READ_ATTRIBUTES,
        FILE_SHARE_ALL,
        None,
        OPEN_EXISTING,
        flags,
        None,
    )
    if handle == INVALID_HANDLE_VALUE or not handle:
        return None
    return handle


def _pids_using_path(path: str, is_dir: bool = False) -> list[int]:
    handle = _open_path_for_query(path, is_dir=is_dir)
    if not handle:
        return []
    try:
        size = 16 + 8 * 64
        while size <= 16 + 8 * 4096:
            buf = (ctypes.c_ubyte * size)()
            iosb = IO_STATUS_BLOCK()
            status = NTDLL.NtQueryInformationFile(
                handle,
                ctypes.byref(iosb),
                buf,
                size,
                FileProcessIdsUsingFileInformation,
            )
            st = _ntstatus(status)
            if st in (
                STATUS_INFO_LENGTH_MISMATCH,
                STATUS_BUFFER_OVERFLOW,
                STATUS_BUFFER_TOO_SMALL,
            ):
                size *= 2
                continue
            if st != STATUS_SUCCESS:
                return []
            raw = bytes(buf)
            # ULONG NumberOfProcessIdsInList; 对齐后 ULONG_PTR ProcessIdList[]
            n = int.from_bytes(raw[:4], "little")
            if n <= 0:
                return []
            pids: list[int] = []
            width = ctypes.sizeof(ctypes.c_void_p)
            list_off = 8 if IS_64BIT else 4
            for i in range(n):
                off = list_off + i * width
                if off + width > size:
                    break
                pid = int.from_bytes(raw[off : off + width], "little")
                if pid:
                    pids.append(pid)
            return pids
        return []
    finally:
        KERNEL32.CloseHandle(handle)


def _scan_open_paths_chunk(items: list[tuple[str, bool]]) -> dict[int, set[str]]:
    """子进程/线程块：扫描一批 (path, is_dir)。"""
    pid_hits: dict[int, set[str]] = {}
    if not items:
        return pid_hits
    workers = min(OPEN_FILE_WORKERS, max(8, len(items)))
    self_pid = os.getpid()

    def _job(item: tuple[str, bool]) -> tuple[str, list[int]]:
        path, is_dir = item
        return path, _pids_using_path(path, is_dir=is_dir)

    with ThreadPoolExecutor(max_workers=workers) as pool:
        futs = [pool.submit(_job, item) for item in items]
        for fut in as_completed(futs):
            try:
                path, pids = fut.result()
            except Exception:
                continue
            for pid in pids:
                if pid == self_pid:
                    continue
                pid_hits.setdefault(pid, set()).add(path)
    return pid_hits


def _collect_scan_paths(root: Path, limit: int = MAX_PATHS_FOR_SCAN) -> tuple[list[str], list[str], bool]:
    """一次遍历：返回 (全部路径含目录, 仅文件, 是否截断)。"""
    all_paths: list[str] = []
    files: list[str] = []
    truncated = False
    if root.is_file():
        p = str(root.resolve())
        return [p], [p], False
    if not root.exists():
        return [], [], False
    root_s = str(root.resolve())
    all_paths.append(root_s)
    for dirpath, dirnames, filenames in os.walk(root_s):
        for d in dirnames:
            all_paths.append(str(Path(dirpath, d)))
            if len(all_paths) >= limit:
                return all_paths, files, True
        for f in filenames:
            fp = str(Path(dirpath, f))
            all_paths.append(fp)
            files.append(fp)
            if len(all_paths) >= limit:
                return all_paths, files, True
    return all_paths, files, truncated


def find_open_file_lockers(
    target: Path,
    paths: list[str] | None = None,
    truncated: bool = False,
    files: list[str] | None = None,
) -> tuple[list[Locker], int, bool]:
    collected_files: list[str] = []
    if paths is None:
        paths, collected_files, truncated = _collect_scan_paths(target)
        if files is None:
            files = collected_files
    if not paths:
        return [], 0, False

    file_set = set(files) if files is not None else None
    items: list[tuple[str, bool]] = [
        (p, True if file_set is None else (p not in file_set)) for p in paths
    ]

    # 仅用线程池（可从 GUI 工作线程安全调用；进程池在非主线程会卡住）
    pid_hits = _scan_open_paths_chunk(items)

    result: list[Locker] = []
    for pid, hit_files in pid_hits.items():
        sample = next(iter(hit_files))
        extra = f" 等{len(hit_files)}项" if len(hit_files) > 1 else ""
        result.append(_make_locker(pid, f"打开句柄: {sample}{extra}"))
    return result, len(paths), truncated


# ---------------------------------------------------------------------------
# Restart Manager（文件）
# ---------------------------------------------------------------------------

def _collect_files(root: Path, limit: int = MAX_PATHS_FOR_SCAN) -> list[str]:
    files: list[str] = []
    if root.is_file():
        return [str(root.resolve())]
    if not root.is_dir():
        return []
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in filenames:
            files.append(str(Path(dirpath, name)))
            if len(files) >= limit:
                return files
    return files


def _rm_query_files(file_paths: list[str]) -> list[Locker]:
    if not file_paths:
        return []

    session = wt.DWORD(0)
    key = ctypes.create_unicode_buffer(CCH_RM_SESSION_KEY + 1)
    rc = RSTRTMGR.RmStartSession(ctypes.byref(session), 0, key)
    if rc != ERROR_SUCCESS:
        return []

    lockers: dict[int, Locker] = {}
    try:
        # 多批注册到同一 session
        for i in range(0, len(file_paths), RM_BATCH):
            batch = file_paths[i : i + RM_BATCH]
            arr = (ctypes.c_wchar_p * len(batch))(*batch)
            RSTRTMGR.RmRegisterResources(
                session, len(batch), arr, 0, None, 0, None
            )

        needed = wt.UINT(0)
        count = wt.UINT(0)
        reboot = wt.DWORD(0)
        rc = RSTRTMGR.RmGetList(
            session, ctypes.byref(needed), ctypes.byref(count), None, ctypes.byref(reboot)
        )
        if rc == ERROR_ACCESS_DENIED:
            rc = RSTRTMGR.RmGetList(
                session, ctypes.byref(needed), ctypes.byref(count), None, ctypes.byref(reboot)
            )
        if rc not in (ERROR_SUCCESS, ERROR_MORE_DATA) or needed.value == 0:
            return []

        infos = (RM_PROCESS_INFO * needed.value)()
        count = wt.UINT(needed.value)
        rc = RSTRTMGR.RmGetList(
            session, ctypes.byref(needed), ctypes.byref(count), infos, ctypes.byref(reboot)
        )
        if rc != ERROR_SUCCESS:
            return []

        for j in range(count.value):
            info = infos[j]
            pid = int(info.Process.dwProcessId)
            if not pid or pid in lockers:
                continue
            lockers[pid] = Locker(
                pid=pid,
                name=info.strAppName or _process_name(pid),
                app_type=int(info.ApplicationType),
                reason="Restart Manager: 占用目录内文件",
                exe_path=_process_image_path_cached(pid),
                service=info.strServiceShortName or "",
            )
    finally:
        RSTRTMGR.RmEndSession(session)

    return list(lockers.values())


def find_rm_lockers(target: Path, files: list[str] | None = None) -> tuple[list[Locker], int]:
    if files is None:
        files = _collect_files(target)
    if not files:
        return [], 0
    return _rm_query_files(files), len(files)


# ---------------------------------------------------------------------------
# 系统句柄表扫描（最全面）
# ---------------------------------------------------------------------------

def _drive_device_map() -> list[tuple[str, str]]:
    """返回 [(device_prefix_lower, 'e:'), ...] 按 device 长度降序。"""
    mapping: list[tuple[str, str]] = []
    drives = KERNEL32.GetLogicalDrives()
    for i in range(26):
        if not (drives & (1 << i)):
            continue
        letter = f"{chr(ord('A') + i)}:"
        buf = ctypes.create_unicode_buffer(1024)
        if KERNEL32.QueryDosDeviceW(letter, buf, 1024):
            mapping.append((buf.value.lower(), letter.lower()))
    mapping.sort(key=lambda x: len(x[0]), reverse=True)
    return mapping


def _nt_to_dos(nt_path: str, drive_map: list[tuple[str, str]]) -> str | None:
    if not nt_path:
        return None
    low = nt_path.lower()
    # \??\E:\path 或 \DosDevices\E:\path
    for prefix in ("\\??\\", "\\dosdevices\\"):
        if low.startswith(prefix) and len(nt_path) > len(prefix) + 2 and nt_path[len(prefix) + 1] == ":":
            return nt_path[len(prefix) :]
    for device, letter in drive_map:
        if low.startswith(device):
            rest = nt_path[len(device) :]
            if not rest.startswith("\\"):
                rest = "\\" + rest
            return letter + rest
    return None


def _query_object_name(handle) -> str | None:
    size = 1024
    while size <= 64 * 1024:
        buf = ctypes.create_string_buffer(size)
        ret_len = wt.ULONG(0)
        status = NTDLL.NtQueryObject(
            handle, ObjectNameInformation, buf, size, ctypes.byref(ret_len)
        )
        st = _ntstatus(status)
        if st in (STATUS_INFO_LENGTH_MISMATCH, STATUS_BUFFER_OVERFLOW, STATUS_BUFFER_TOO_SMALL):
            size = max(size * 2, ret_len.value + 64)
            continue
        if st != STATUS_SUCCESS:
            return None
        us = UNICODE_STRING.from_buffer_copy(buf.raw[: ctypes.sizeof(UNICODE_STRING)])
        if not us.Length or not us.Buffer:
            return None
        name_bytes = ctypes.string_at(us.Buffer, us.Length)
        return name_bytes.decode("utf-16-le", errors="ignore")
    return None


def find_system_handle_lockers(target: Path, time_budget_sec: float = 12.0) -> list[Locker]:
    """扫描系统句柄表中的 File 句柄（限时），补全其它 API 漏检的占用。"""
    import threading
    import time

    target_n = _norm(target)
    drive_map = _drive_device_map()
    if not drive_map:
        return []

    deadline = time.monotonic() + time_budget_sec

    # 用目标路径打开探测句柄，识别 File 的 ObjectTypeIndex
    probe_path = str(target.resolve() if target.exists() else target)
    probe = _open_path_for_query(probe_path, is_dir=target.is_dir())
    file_type_indices: set[int] = set()
    probe_value = int(probe) if probe else 0
    my_pid = os.getpid()

    size = 1 << 22
    buf = None
    while size <= 1 << 28:
        if time.monotonic() > deadline:
            if probe:
                KERNEL32.CloseHandle(probe)
            return []
        buf = ctypes.create_string_buffer(size)
        ret = wt.ULONG(0)
        status = NTDLL.NtQuerySystemInformation(
            SystemExtendedHandleInformation, buf, size, ctypes.byref(ret)
        )
        st = _ntstatus(status)
        if st in (STATUS_INFO_LENGTH_MISMATCH, STATUS_BUFFER_OVERFLOW, STATUS_BUFFER_TOO_SMALL):
            size = max(size * 2, ret.value + 0x10000)
            continue
        if st != STATUS_SUCCESS:
            if probe:
                KERNEL32.CloseHandle(probe)
            return []
        break
    else:
        if probe:
            KERNEL32.CloseHandle(probe)
        return []

    number = int.from_bytes(buf.raw[0 : ctypes.sizeof(ctypes.c_size_t)], "little")
    entry_size = ctypes.sizeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX)
    header = ctypes.sizeof(ctypes.c_size_t) * 2
    max_entries = min(number, (len(buf.raw) - header) // entry_size)

    # 先找 File 类型索引
    if probe and probe_value:
        for i in range(max_entries):
            off = header + i * entry_size
            entry = SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX.from_buffer_copy(
                buf.raw[off : off + entry_size]
            )
            if int(entry.UniqueProcessId) != my_pid:
                continue
            hv = int(entry.HandleValue)
            if hv == probe_value or (hv & 0xFFFFFFFF) == (probe_value & 0xFFFFFFFF):
                file_type_indices.add(int(entry.ObjectTypeIndex))
        KERNEL32.CloseHandle(probe)
        probe = None
    elif probe:
        KERNEL32.CloseHandle(probe)

    # 若识别失败，不扫全表（太慢），直接放弃句柄表补充
    if not file_type_indices:
        return []

    by_pid: dict[int, list[int]] = {}
    for i in range(max_entries):
        if time.monotonic() > deadline:
            break
        off = header + i * entry_size
        entry = SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX.from_buffer_copy(
            buf.raw[off : off + entry_size]
        )
        if int(entry.ObjectTypeIndex) not in file_type_indices:
            continue
        pid = int(entry.UniqueProcessId)
        if not pid or pid == my_pid or pid in (0, 4):
            continue
        by_pid.setdefault(pid, []).append(int(entry.HandleValue))

    # 释放巨大缓冲
    buf = None

    current = KERNEL32.GetCurrentProcess()
    pid_reasons: dict[int, str] = {}
    lock = threading.Lock()

    def _check_pid(pid: int, handles: list[int]) -> None:
        if time.monotonic() > deadline:
            return
        proc = KERNEL32.OpenProcess(PROCESS_DUP_HANDLE, False, pid)
        if not proc:
            return
        try:
            for hv in handles[:256]:
                if time.monotonic() > deadline:
                    break
                dup = wt.HANDLE()
                ok = KERNEL32.DuplicateHandle(
                    proc,
                    ctypes.c_void_p(hv),
                    current,
                    ctypes.byref(dup),
                    0,
                    False,
                    DUPLICATE_SAME_ACCESS,
                )
                if not ok:
                    continue
                try:
                    name = _query_object_name(dup)
                    if not name:
                        continue
                    dos = _nt_to_dos(name, drive_map)
                    if dos and _under(dos, target_n):
                        with lock:
                            pid_reasons[pid] = f"系统句柄: {dos}"
                        return
                finally:
                    KERNEL32.CloseHandle(dup)
        finally:
            KERNEL32.CloseHandle(proc)

    # 句柄多的进程优先；守护线程避免超时后拖住进程
    ordered = sorted(by_pid.items(), key=lambda kv: len(kv[1]), reverse=True)
    threads: list[threading.Thread] = []
    for pid, handles in ordered[:64]:
        if time.monotonic() > deadline:
            break
        t = threading.Thread(
            target=_check_pid, args=(pid, handles), daemon=True
        )
        t.start()
        threads.append(t)
        if len(threads) >= HANDLE_SCAN_NAME_WORKERS:
            for old in threads:
                rem = deadline - time.monotonic()
                if rem <= 0:
                    break
                old.join(timeout=min(0.05, rem))
            threads = [x for x in threads if x.is_alive()]

    for t in threads:
        rem = deadline - time.monotonic()
        if rem <= 0:
            break
        t.join(timeout=rem)

    return [_make_locker(pid, reason) for pid, reason in pid_reasons.items()]


def find_cmdline_lockers(target: Path) -> list[Locker]:
    """命令行中包含目标路径的进程（常见于脚本/编译器/打包工具）。"""
    target_n = _norm(target)
    # 也匹配未规范化的原始写法
    needles = {
        target_n,
        str(target).replace("/", "\\").lower().rstrip("\\"),
        str(target.resolve()).replace("/", "\\").lower().rstrip("\\") if target.exists() else "",
    }
    needles = {n for n in needles if n}
    result: list[Locker] = []
    try:
        out = subprocess.check_output(
            [
                "powershell",
                "-NoProfile",
                "-Command",
                "Get-CimInstance Win32_Process | Select-Object ProcessId,Name,CommandLine | ConvertTo-Json -Compress",
            ],
            text=True,
            encoding="utf-8",
            errors="replace",
            creationflags=CREATE_NO_WINDOW,
            timeout=8,
        )
    except Exception:
        return result
    if not out.strip():
        return result
    import json

    try:
        data = json.loads(out)
    except json.JSONDecodeError:
        return result
    if isinstance(data, dict):
        data = [data]
    for row in data:
        pid = int(row.get("ProcessId") or 0)
        if not pid or pid == os.getpid():
            continue
        cmd = (row.get("CommandLine") or "").replace("/", "\\").lower()
        if not cmd:
            continue
        if any(n in cmd for n in needles):
            result.append(
                _make_locker(
                    pid,
                    "命令行引用了目标路径",
                    name=row.get("Name") or "",
                )
            )
    return result


def find_system_handle_lockers_safe(target: Path, timeout_sec: float = 8.0) -> list[Locker]:
    """句柄表补检：守护线程限时执行，超时立即返回且不拖住进程。"""
    import threading

    box: list[list[Locker]] = [[]]
    done = threading.Event()

    def _worker() -> None:
        try:
            box[0] = find_system_handle_lockers(
                target, max(2.0, timeout_sec - 0.5)
            )
        except Exception:
            box[0] = []
        finally:
            done.set()

    t = threading.Thread(target=_worker, name="handle-scan", daemon=True)
    t.start()
    if done.wait(timeout=timeout_sec):
        return box[0]
    return []


# ---------------------------------------------------------------------------
# 结束进程
# ---------------------------------------------------------------------------

def terminate_process(pid: int, force: bool = True) -> tuple[bool, str]:
    if pid <= 0:
        return False, "无效 PID"
    if pid == os.getpid():
        return False, "不能结束自己"
    if pid in (0, 4):
        return False, "系统关键进程，已跳过"

    args = ["taskkill", "/PID", str(pid), "/T"]
    if force:
        args.append("/F")
    try:
        completed = subprocess.run(
            args,
            capture_output=True,
            creationflags=CREATE_NO_WINDOW,
        )

        def _dec(b: bytes) -> str:
            if not b:
                return ""
            for enc in ("utf-8", "gbk", "cp936"):
                try:
                    return b.decode(enc).strip()
                except UnicodeDecodeError:
                    continue
            return b.decode("utf-8", errors="replace").strip()

        msg = _dec(completed.stdout) or _dec(completed.stderr)
        if completed.returncode == 0:
            return True, msg or "已结束"
        if "没有找到" in msg or "not found" in msg.lower():
            return True, "进程已不存在（可能已被一并结束）"
        return False, msg or f"taskkill 返回 {completed.returncode}"
    except Exception as e:
        return False, str(e)


def merge_lockers(*groups: list[Locker]) -> list[Locker]:
    by_pid: dict[int, Locker] = {}
    for group in groups:
        for item in group:
            if item.app_type == RmCritical:
                continue
            if item.pid in by_pid:
                old = by_pid[item.pid]
                if item.reason and item.reason not in old.reason:
                    old.reason = f"{old.reason}; {item.reason}"
                if item.exe_path and not old.exe_path:
                    old.exe_path = item.exe_path
                if item.name and (not old.name or old.name.startswith("PID ")):
                    old.name = item.name
                if item.app_type and old.app_type == RmUnknownApp:
                    old.app_type = item.app_type
            else:
                by_pid[item.pid] = item
    return sorted(by_pid.values(), key=lambda x: x.pid)


def should_skip(p: Locker) -> str | None:
    if p.app_type == RmCritical:
        return "系统关键"
    exe = (p.exe_path or "").lower()
    base = Path(exe).name.lower() if exe else (p.name or "").lower()
    # System / Registry / smss 等
    protected = {
        "system",
        "registry",
        "smss.exe",
        "csrss.exe",
        "wininit.exe",
        "services.exe",
        "lsass.exe",
        "winlogon.exe",
    }
    if base in protected or p.pid in (0, 4):
        return "系统关键进程"
    if p.app_type == RmExplorer or base == "explorer.exe":
        return "资源管理器（请先关掉对应文件夹窗口）"
    return None


def do_kill(lockers: list[Locker]) -> list[str]:
    lines: list[str] = []
    for p in lockers:
        skip = should_skip(p)
        if skip:
            lines.append(f"[跳过] PID {p.pid} {p.name} — {skip}")
            continue
        ok, msg = terminate_process(p.pid, force=True)
        lines.append(f"[{'成功' if ok else '失败'}] PID {p.pid} {p.name}: {msg}")
    return lines


def find_process_by_basenames(basenames: set[str]) -> list[Locker]:
    """按进程名查找（任务管理器里明面上的那些）。"""
    want: set[str] = set()
    for b in basenames:
        b = (b or "").strip().lower()
        if not b:
            continue
        want.add(b)
        if b.endswith(".exe"):
            want.add(b[:-4])
        else:
            want.add(b + ".exe")
    if not want:
        return []
    result: list[Locker] = []
    self_pid = os.getpid()
    for pid, exe_name in _iter_pids():
        if not pid or pid == self_pid:
            continue
        base = (exe_name or "").lower()
        image = _process_image_path_cached(pid)
        image_base = Path(image).name.lower() if image else ""
        if base in want or image_base in want:
            shown = exe_name or image_base or f"PID {pid}"
            result.append(
                _make_locker(
                    pid,
                    f"进程名匹配（任务管理器可见）: {shown}",
                    name=shown,
                )
            )
    return result


def _resolve_lnk(path: Path) -> Path | None:
    """解析 .lnk 快捷方式的真实目标路径。"""
    try:
        if path.suffix.lower() != ".lnk" or not path.is_file():
            return None
    except Exception:
        return None
    import json as _json

    try:
        # WScript.Shell：比手工解析 .lnk 二进制稳妥
        ps = (
            "$s=(New-Object -ComObject WScript.Shell).CreateShortcut("
            + _json.dumps(str(path))
            + "); if($s.TargetPath){$s.TargetPath}"
        )
        out = subprocess.check_output(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps],
            text=True,
            encoding="utf-8",
            errors="replace",
            creationflags=CREATE_NO_WINDOW,
            timeout=8,
        ).strip().strip('"')
        if not out:
            return None
        dest = Path(out)
        return dest
    except Exception:
        return None


def _expand_scan_roots(path: Path) -> tuple[list[Path], list[str]]:
    """路径展开：.lnk → 真实目标（便于找到正在运行的程序）。"""
    notes: list[str] = []
    roots: list[Path] = []
    seen: set[str] = set()

    def _add(p: Path, why: str = "") -> None:
        try:
            key = str(p.resolve()) if p.exists() else str(p)
        except Exception:
            key = str(p)
        key_n = key.replace("/", "\\").lower().rstrip("\\")
        if key_n in seen:
            return
        seen.add(key_n)
        roots.append(p)
        if why:
            notes.append(why)

    _add(path)
    if path.suffix.lower() == ".lnk":
        _progress("检测到快捷方式，正在解析真实目标…")
        dest = _resolve_lnk(path)
        if dest is None:
            notes.append("快捷方式未能解析目标（可改为直接选 .exe 或安装目录）")
        else:
            _progress(f"快捷方式 → {dest}")
            _add(dest, f"已解析快捷方式 → {dest}")
            # 目标是可执行文件时，再扫其所在目录一层意义不大且很慢；
            # 进程映像检测会对「正好是这个 exe」命中正在跑的 QQ。
    return roots, notes


def scan_target(path_str: str) -> tuple[list[Locker], str]:
    """全面扫描占用进程（多线程并行，不减少任何检查项）。"""
    import time

    path_str = path_str.strip().strip('"')
    if not path_str:
        return [], "请先指定路径"
    target = Path(path_str)
    notes: list[str] = []
    _clear_scan_caches()
    t0 = time.perf_counter()

    _progress("开始扫描占用…")
    _progress(f"路径: {target}")

    if not _is_admin():
        notes.append("非管理员，部分句柄可能看不到")
        _progress("提示: 未以管理员运行，部分占用可能看不到")

    roots, expand_notes = _expand_scan_roots(target)
    notes.extend(expand_notes)

    all_groups: list[list[Locker]] = []

    # 先按进程名捞一遍：任务管理器里明面上的 QQ 等，不能漏
    name_keys: set[str] = {f"{target.stem}.exe", target.name}
    for r in roots:
        name_keys.add(r.name)
        if r.suffix.lower() == ".exe":
            name_keys.add(f"{r.stem}.exe")
    name_keys = {k for k in name_keys if k and k.lower() not in {".lnk", "lnk"}}
    # 去掉纯 .lnk 文件名当进程名
    name_keys = {k for k in name_keys if not k.lower().endswith(".lnk")}
    if name_keys:
        _progress(f"按进程名查找: {', '.join(sorted(name_keys))}")
        try:
            by_name = find_process_by_basenames(name_keys)
            all_groups.append(by_name)
            if by_name:
                _progress(f"进程名命中 {len(by_name)} 个（任务管理器可见）")
                notes.append(f"进程名命中 {len(by_name)}")
            else:
                _progress("进程名未命中")
        except Exception as e:
            _progress(f"进程名查找异常: {e}")

    for idx, root in enumerate(roots, 1):
        if len(roots) > 1:
            _progress(f"扫描目标 {idx}/{len(roots)}: {root}")
        lockers_one, notes_one = _scan_one_root(root)
        all_groups.append(lockers_one)
        for n in notes_one:
            if n not in notes:
                notes.append(n)

    # 若目标是 .exe（含快捷方式解析出的），再查「同目录正在跑的进程」
    # 例：QQ.lnk → QQScLauncher.exe，真正登录的可能是同目录 QQ.exe
    exe_parents: list[Path] = []
    seen_par: set[str] = set()
    for r in roots:
        if r.suffix.lower() == ".exe" and r.parent.exists():
            key = str(r.parent.resolve()).lower()
            if key not in seen_par:
                seen_par.add(key)
                exe_parents.append(r.parent)
    for parent in exe_parents:
        _progress(f"查找同目录在跑进程: {parent}")
        try:
            sib = find_process_attr_lockers(parent)
            all_groups.append(sib)
            if sib:
                _progress(f"同目录命中 {len(sib)} 个进程")
                notes.append(f"同目录进程 {len(sib)}")
            else:
                _progress("同目录未发现在跑进程")
        except Exception as e:
            _progress(f"同目录进程查找异常: {e}")

    lockers = merge_lockers(*all_groups)
    elapsed = time.perf_counter() - t0
    if not lockers:
        notes.append("未发现占用进程")
        _progress("未发现占用进程")
        if target.suffix.lower() == ".lnk":
            _progress("说明: 已按快捷方式名/解析目标名查找进程；仍为空请直接选安装目录")
    else:
        notes.append(f"合计 {len(lockers)} 个占用进程")
        _progress(f"找到 {len(lockers)} 个占用进程")
    notes.append(f"{elapsed:.1f}s")
    _progress(f"扫描结束 · {elapsed:.1f}s")
    return lockers, " · ".join(notes)


def _scan_one_root(target: Path) -> tuple[list[Locker], list[str]]:
    """对单个路径跑完整检测套件。"""
    notes: list[str] = []
    all_paths: list[str] = []
    files_only: list[str] = []
    truncated = False
    if target.exists():
        _progress("收集路径项…")
        all_paths, files_only, truncated = _collect_scan_paths(target)
        _progress(f"路径项 {len(all_paths)}" + ("（已截断）" if truncated else ""))
    else:
        notes.append("路径不存在，已跳过文件/句柄类检测")
        _progress("路径不存在，仅做进程名/命令行相关检测")

    results: dict[str, object] = {}
    errors: dict[str, str] = {}
    labels = {
        "proc": "进程属性（工作目录/映像/模块）",
        "cmdline": "命令行引用",
        "open": "打开文件句柄",
        "rm": "Restart Manager",
        "handle": "系统句柄表补检",
    }

    def _run(name: str, fn) -> None:
        _progress(f"进行中 · {labels.get(name, name)}…")
        try:
            results[name] = fn()
            _progress(f"完成 · {labels.get(name, name)}")
        except Exception as e:
            errors[name] = str(e)
            _progress(f"异常 · {labels.get(name, name)}: {e}")

    tasks = [
        ("proc", lambda: find_process_attr_lockers(target)),
        ("cmdline", lambda: find_cmdline_lockers(target)),
    ]
    if target.exists():
        tasks.extend(
            [
                (
                    "open",
                    lambda: find_open_file_lockers(
                        target,
                        paths=all_paths,
                        truncated=truncated,
                        files=files_only,
                    ),
                ),
                ("rm", lambda: find_rm_lockers(target, files=files_only)),
            ]
        )

    with ThreadPoolExecutor(max_workers=SCAN_POOL_WORKERS) as pool:
        futs = [pool.submit(_run, name, fn) for name, fn in tasks]
        for fut in as_completed(futs):
            fut.result()

    # 句柄表补检单独跑：与打开句柄扫描并行会抢内核、整体更慢
    if target.exists():
        _run(
            "handle",
            lambda: find_system_handle_lockers_safe(target, timeout_sec=6.0),
        )

    groups: list[list[Locker]] = []

    if "proc" in results:
        groups.append(results["proc"])  # type: ignore[arg-type]
    elif "proc" in errors:
        notes.append(f"进程属性检测异常: {errors['proc']}")

    if "cmdline" in results:
        groups.append(results["cmdline"])  # type: ignore[arg-type]
    elif "cmdline" in errors:
        notes.append(f"命令行检测异常: {errors['cmdline']}")

    if "open" in results:
        open_lockers, scanned, trunc = results["open"]  # type: ignore[misc]
        groups.append(open_lockers)
        notes.append(f"路径项 {scanned}")
        if trunc:
            notes.append("路径项达上限，可能仍有遗漏")
    elif "open" in errors:
        notes.append(f"打开句柄检测异常: {errors['open']}")

    if "rm" in results:
        rm_lockers, nfiles = results["rm"]  # type: ignore[misc]
        groups.append(rm_lockers)
        notes.append(f"RM文件 {nfiles}")
    elif "rm" in errors:
        notes.append(f"RM 检测异常: {errors['rm']}")

    if "handle" in results:
        handle_lockers = results["handle"]
        groups.append(handle_lockers)  # type: ignore[arg-type]
        if handle_lockers:
            notes.append(f"句柄表补检 {len(handle_lockers)}")  # type: ignore[arg-type]
    elif "handle" in errors:
        notes.append(f"句柄表补检异常: {errors['handle']}")

    return merge_lockers(*groups), notes


# ---------------------------------------------------------------------------
# ---------------------------------------------------------------------------
# 网络诊断 / 修复（针对 WiFi 驱动/服务异常需重启才能恢复的情况）
# ---------------------------------------------------------------------------

_WIFI_NAME_RE = (
    r"Wi-?Fi|WLAN|Wireless|802\.11|MediaTek|Intel\(R\).*Wi-?Fi|"
    r"Realtek.*Wireless|Broadcom.*802|Killer.*Wireless|Qualcomm.*Wireless|MERCURY Wireless"
)
_WIFI_EXCLUDE_RE = r"Virtual|Direct|Hosted Network|Bluetooth|WAN Miniport|Loopback"


def _run_hidden(
    args: list[str], timeout: float = 60.0, shell: bool = False
) -> tuple[int, str]:
    """跑外部命令；超时强制杀进程树（Windows 上 net stop / powershell 子进程常杀不干净）。"""
    def _dec(b: bytes) -> str:
        if not b:
            return ""
        for enc in ("utf-8", "gbk", "cp936"):
            try:
                return b.decode(enc).strip()
            except UnicodeDecodeError:
                continue
        return b.decode("utf-8", errors="replace").strip()

    # CREATE_NEW_PROCESS_GROUP 便于超时整树杀掉
    flags = CREATE_NO_WINDOW | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0x00000200)
    try:
        proc = subprocess.Popen(
            args,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=shell,
            creationflags=flags,
        )
    except Exception as e:
        return 1, str(e)

    try:
        out_b, err_b = proc.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        # 先杀整树，再杀自身
        try:
            subprocess.run(
                ["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                capture_output=True,
                timeout=8,
                creationflags=CREATE_NO_WINDOW,
            )
        except Exception:
            pass
        try:
            proc.kill()
        except Exception:
            pass
        try:
            proc.communicate(timeout=3)
        except Exception:
            pass
        return 124, f"命令超时({int(timeout)}s): {' '.join(args[:3])}"

    out = _dec(out_b)
    err = _dec(err_b)
    text = out if out else err
    if out and err and err not in out:
        text = f"{out}\n{err}"
    return proc.returncode if proc.returncode is not None else 1, text


def _run_powershell(script: str, timeout: float = 45.0) -> tuple[int, str]:
    # 强制 UTF-8 输出，避免中文网卡名在管道里变成乱码
    wrapped = (
        "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false; "
        "$OutputEncoding = [Console]::OutputEncoding; "
        "$ErrorActionPreference = 'SilentlyContinue'; "
        + script
    )
    return _run_hidden(
        [
            "powershell",
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            wrapped,
        ],
        timeout=timeout,
    )


def _progress(msg: str) -> None:
    """实时进度：写到 stderr，宿主立刻刷到 UI 日志（stdout 留给最终 JSON）。"""
    try:
        sys.stderr.write(f":: {msg}\n")
        sys.stderr.flush()
    except Exception:
        pass


def _step(steps: list[str], msg: str) -> None:
    steps.append(msg)
    _progress(msg)


def _network_result(ok: bool, steps: list[str], **extra) -> dict:
    return {"ok": ok, "is_admin": _is_admin(), "steps": steps, **extra}


def _service_state(name: str) -> str:
    """返回 RUNNING / STOPPED / UNKNOWN（快速 sc query）。"""
    code, text = _run_hidden(["sc", "query", name], timeout=8)
    t = (text or "").upper()
    if "RUNNING" in t:
        return "RUNNING"
    if "STOPPED" in t:
        return "STOPPED"
    return "UNKNOWN"


def _ensure_service(name: str, restart: bool = False) -> str:
    """启动服务；必要时软重启。用 sc 代替 net stop，避免依赖锁死卡死数分钟。"""
    if name == "Dhcp":
        _run_hidden(["sc", "start", "WinHttpAutoProxySvc"], timeout=12)
        _run_powershell(
            "Set-Service WinHttpAutoProxySvc -StartupType Manual -ErrorAction SilentlyContinue",
            timeout=15,
        )

    _run_powershell(
        f"Set-Service -Name '{name}' -StartupType Automatic -ErrorAction SilentlyContinue",
        timeout=15,
    )

    state = _service_state(name)
    if state == "RUNNING" and not restart:
        return f"服务 {name}: 已在运行"

    if restart and state == "RUNNING":
        _progress(f"软停 {name}…")
        # sc stop 立即返回；不要用 net stop（会干等依赖）
        _run_hidden(["sc", "stop", name], timeout=10)
        # 最多等 8 秒变 STOPPED，否则继续 start（避免卡死）
        for _ in range(8):
            time.sleep(1)
            if _service_state(name) != "RUNNING":
                break
        else:
            _progress(f"{name} 停止偏慢，继续尝试启动")

    _progress(f"启动 {name}…")
    code, text = _run_hidden(["sc", "start", name], timeout=15)
    # 已在运行时 sc start 会非 0，再查一次状态
    time.sleep(0.4)
    if _service_state(name) == "RUNNING":
        return f"服务 {name}: {'已重启' if restart else '已启动/运行中'}"
    code2, text2 = _run_powershell(
        f"Start-Service -Name '{name}' -ErrorAction SilentlyContinue; "
        f"(Get-Service '{name}').Status",
        timeout=20,
    )
    if "Running" in (text2 or ""):
        return f"服务 {name}: 运行中"
    return f"服务 {name}: 失败 ({(text2 or text or str(code))[:100]})"


def _wlan_interface_alive() -> bool:
    """netsh 有时漏报（驱动/语言差异）；同时参考 Get-NetAdapter 无线网卡是否 Up。"""
    code, text = _run_hidden(["netsh", "wlan", "show", "interfaces"], timeout=15)
    t = (text or "").strip()
    low = t.lower()
    netsh_none = (
        "没有无线接口" in t
        or "no wireless interface" in low
        or "there is no wireless" in low
    )
    if code == 0 and t and not netsh_none:
        if any(
            k in t
            for k in (
                "SSID",
                "GUID",
                "名称",
                "Name",
                "状态",
                "State",
                "无线电",
                "Radio",
                "BSSID",
            )
        ):
            return True
        # 有接口段但字段名不同
        if "接口" in t or "interface" in low:
            return True

    # 回退：网卡层已 Up 的无线适配器
    code2, text2 = _run_powershell(
        rf"""
$wifiRe = '{_WIFI_NAME_RE}'; $exclRe = '{_WIFI_EXCLUDE_RE}'
$up = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {{
  $n=$_.Name; $d=$_.InterfaceDescription
  (($n -match $wifiRe) -or ($d -match $wifiRe)) -and ($n -notmatch $exclRe) -and ($d -notmatch $exclRe) -and ($_.Status -eq 'Up')
}})
if ($up.Count -gt 0) {{ 'UP' }} else {{ 'DOWN' }}
""",
        timeout=20,
    )
    return "UP" in (text2 or "").upper()


def _soft_restart_wlan() -> list[str]:
    """
    仅重启 WLAN 相关服务。
    安全约束：不改 powercfg USB、不写 Enum\\USB、不 pnputil /remove-device，
    避免任何可能导致 USB 总线失灵的操作。
    """
    steps: list[str] = [
        "安全模式：跳过 USB 省电写入与设备节点删除",
        _ensure_service("WinHttpAutoProxySvc", restart=False),
    ]
    for svc in ("WlanSvc", "NlaSvc"):
        steps.append(_ensure_service(svc, restart=True))
    if _wlan_interface_alive():
        steps.append("netsh: 已检测到无线接口")
    else:
        steps.append("netsh: 仍无无线接口（需人工拔插无线网卡或重启，软件不再删设备）")
    return steps


def network_diagnose() -> dict:
    """诊断网卡 / WLAN 服务 / 连通性 / 驱动状态。"""
    steps: list[str] = []
    if not _is_admin():
        _step(steps, "提示: 当前非管理员，部分修复操作可能失败，建议以管理员运行")

    _progress("正在采集网卡、服务与连通性…")
    ps = rf"""
$ErrorActionPreference = 'SilentlyContinue'
$wifiRe = '{_WIFI_NAME_RE}'
$exclRe = '{_WIFI_EXCLUDE_RE}'
$adapters = @(Get-NetAdapter | Select-Object Name, Status, MacAddress, LinkSpeed, InterfaceDescription, ifIndex |
  ForEach-Object {{
    $isWifi = [bool](($_.Name -match $wifiRe -or $_.InterfaceDescription -match $wifiRe) -and ($_.Name -notmatch $exclRe) -and ($_.InterfaceDescription -notmatch $exclRe))
    [PSCustomObject]@{{
      name = $_.Name
      status = [string]$_.Status
      mac = $_.MacAddress
      speed = [string]$_.LinkSpeed
      desc = $_.InterfaceDescription
      ifIndex = $_.ifIndex
      is_wifi = $isWifi
    }}
  }})
$services = @()
foreach ($n in @('WlanSvc','WwanSvc','NlaSvc','netprofm','Dhcp','Dnscache','nsi','Netman','WinHttpAutoProxySvc')) {{
  $s = Get-Service -Name $n -ErrorAction SilentlyContinue
  if ($s) {{
    $services += [PSCustomObject]@{{ name=$s.Name; status=[string]$s.Status; start_type=[string]$s.StartType }}
  }}
}}
$pnp = @(Get-PnpDevice -Class Net | Where-Object {{
  ($_.FriendlyName -match $wifiRe) -and ($_.FriendlyName -notmatch $exclRe)
}} | Select-Object Status, Class, FriendlyName, InstanceId, Problem |
  ForEach-Object {{
    [PSCustomObject]@{{
      status = [string]$_.Status
      name = $_.FriendlyName
      instance_id = $_.InstanceId
      problem = [string]$_.Problem
      is_wifi = $true
    }}
  }})
$wlan = (netsh wlan show interfaces) 2>$null | Out-String
$ipcfg = @(Get-NetIPConfiguration | Where-Object {{ $_.NetAdapter.Status -eq 'Up' }} |
  ForEach-Object {{
    [PSCustomObject]@{{
      iface = $_.InterfaceAlias
      ipv4 = @($_.IPv4Address | ForEach-Object {{ $_.IPAddress }})
      gateway = @($_.IPv4DefaultGateway | ForEach-Object {{ $_.NextHop }})
      dns = @($_.DNSServer | ForEach-Object {{ $_.ServerAddresses }} | ForEach-Object {{ $_ }})
    }}
  }})
$ping_gw = $false
$ping_dns = $false
$gw = ($ipcfg | ForEach-Object {{ $_.gateway }} | Where-Object {{ $_ }} | Select-Object -First 1)
if ($gw) {{ $ping_gw = [bool](Test-Connection -ComputerName $gw -Count 1 -Quiet -ErrorAction SilentlyContinue) }}
$ping_dns = [bool](Test-Connection -ComputerName 1.1.1.1 -Count 1 -Quiet -ErrorAction SilentlyContinue)
[PSCustomObject]@{{
  adapters = $adapters
  services = $services
  pnp = $pnp
  ipcfg = $ipcfg
  wlan_text = $wlan.Trim()
  ping_gateway = $ping_gw
  ping_internet = $ping_dns
  gateway = $gw
}} | ConvertTo-Json -Depth 6 -Compress
"""
    code, text = _run_powershell(ps, timeout=45)
    data: dict = {}
    if code == 0 and text.strip():
        import json

        try:
            data = json.loads(text)
            _progress("采集完成，正在分析…")
        except json.JSONDecodeError:
            _step(steps, f"诊断解析失败: {text[:200]}")
            data = {}
    else:
        _step(steps, f"诊断命令失败: {text[:300] or code}")

    adapters = data.get("adapters") or []
    services = data.get("services") or []
    pnp = data.get("pnp") or []
    if isinstance(adapters, dict):
        adapters = [adapters]
    if isinstance(services, dict):
        services = [services]
    if isinstance(pnp, dict):
        pnp = [pnp]

    wifi_adapters = [a for a in adapters if a.get("is_wifi")]
    wifi_pnp = [d for d in pnp if d.get("is_wifi")]
    down_wifi = [
        a for a in wifi_adapters if str(a.get("status", "")).lower() not in ("up",)
    ]
    bad_svc = [
        s
        for s in services
        if s.get("name") in ("WlanSvc", "NlaSvc", "Dhcp", "Dnscache")
        and str(s.get("status", "")).lower() != "running"
    ]
    bad_pnp = [
        d
        for d in wifi_pnp
        if str(d.get("status", "")).lower() not in ("ok", "started", "running")
        or (
            d.get("problem")
            and str(d.get("problem"))
            not in ("", "0", "CM_PROB_NONE", "None")
        )
    ]
    code10 = [
        d
        for d in wifi_pnp
        if "FAILED_START" in str(d.get("problem", "")).upper()
        or "NEED_RESTART" in str(d.get("problem", "")).upper()
    ]
    _progress("检查无线接口…")
    wifi_up = any(str(a.get("status", "")).lower() == "up" for a in wifi_adapters)
    netsh_alive = False
    # 先看网卡层；再问 netsh（仅作补充，避免误报「无 WiFi」）
    code_n, text_n = _run_hidden(["netsh", "wlan", "show", "interfaces"], timeout=15)
    tn = (text_n or "").strip()
    tnl = tn.lower()
    netsh_none = (
        "没有无线接口" in tn
        or "no wireless interface" in tnl
        or "there is no wireless" in tnl
    )
    if code_n == 0 and tn and not netsh_none:
        netsh_alive = any(
            k in tn
            for k in ("SSID", "GUID", "名称", "Name", "状态", "State", "无线电", "Radio", "BSSID", "接口")
        ) or ("interface" in tnl)
    wlan_alive = wifi_up or netsh_alive or _wlan_interface_alive()
    online_ok = bool(data.get("ping_internet")) and bool(data.get("ping_gateway") or data.get("gateway"))

    suggestions: list[str] = []
    if code10 or (bad_pnp and not wlan_alive):
        suggestions.append(
            "无线芯片驱动已挂死(Code 10/14) → 请手动拔掉 USB 无线网卡等 5 秒再插上，或重启电脑（软件不再删除设备）"
        )
        _step(
            steps,
            "判定: USB 无线驱动无响应；为避免 USB 失灵，请仅人工拔插供电或重启，勿用第三方工具删 USB 设备",
        )
    if bad_svc:
        names = ", ".join(s["name"] for s in bad_svc)
        if wlan_alive and online_ok:
            # 能上网就不当成故障，只提示可选手动拉起服务
            _step(steps, f"提示: 服务 {names} 未运行（当前 WiFi/网络仍可用，可不处理）")
        else:
            suggestions.append(f"关键服务未运行: {names} → 建议「一键修复 WiFi」")
            _step(steps, f"异常服务: {names}")
    if down_wifi and not wifi_up:
        names = ", ".join(a.get("name", "?") for a in down_wifi)
        suggestions.append(f"无线网卡未启用/断开: {names}")
        _step(steps, f"异常无线网卡: {names}")
    if bad_pnp and not wlan_alive:
        names = ", ".join(d.get("name", "?") for d in bad_pnp)
        _step(steps, f"异常驱动: {names}")
    if wlan_alive:
        if wifi_up and not netsh_alive:
            _step(steps, "无线判定: 网卡已 Up（netsh 未列出接口，忽略误报）")
        else:
            _step(steps, "无线判定: 正常")
    else:
        _step(steps, "netsh/网卡: 未检测到可用无线接口（WiFi 入口可能缺失）")
        if not any("拔掉 USB" in s for s in suggestions):
            suggestions.append(
                "WiFi 入口缺失 → 人工拔插 USB 无线网卡，或重启；可先试「一键修复 WiFi」（仅服务）"
            )
    if not data.get("ping_internet"):
        suggestions.append("无法 ping 外网 → 可尝试「全面修复」（仅服务+协议栈，不碰 USB）")
        _step(steps, "外网连通: 失败")
    else:
        _step(steps, "外网连通: 正常")
    if data.get("ping_gateway"):
        _step(steps, f"网关连通: 正常 ({data.get('gateway')})")
    elif data.get("gateway"):
        _step(steps, f"网关连通: 失败 ({data.get('gateway')})")
    if not wifi_adapters and not wifi_pnp:
        suggestions.append("未检测到无线网卡，可能驱动异常或 USB 无线网卡未接通")
        _step(steps, "未找到 WiFi 适配器")
    if wlan_alive and online_ok and not suggestions:
        suggestions.append("WiFi / 网络看起来正常，无需修复")
    elif not suggestions:
        suggestions.append("未发现明显异常；若仍无法上网，可试「全面修复」或人工拔插无线网卡")

    for a in adapters:
        mark = "WiFi" if a.get("is_wifi") else "网卡"
        _step(
            steps,
            f"[{mark}] {a.get('name')}: {a.get('status')} · {a.get('desc') or ''}",
        )
    for d in wifi_pnp:
        _step(
            steps,
            f"[驱动] {d.get('name')}: {d.get('status')} problem={d.get('problem') or '0'}",
        )

    _progress("网络诊断完成")
    return _network_result(
        True,
        steps,
        adapters=adapters,
        services=services,
        pnp=pnp,
        ipcfg=data.get("ipcfg") or [],
        wlan_text=data.get("wlan_text") or "",
        ping_gateway=bool(data.get("ping_gateway")),
        ping_internet=bool(data.get("ping_internet")),
        wlan_alive=wlan_alive,
        suggestions=suggestions,
        need_unplug=bool(code10 or (bad_pnp and not wlan_alive)),
    )


def network_fix_wifi() -> dict:
    """修复 WLAN / 网络相关服务（不碰 USB、不删设备）。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(
            False, ["需要管理员权限才能修复 WiFi"], suggestions=["请右键以管理员身份运行"]
        )

    _step(steps, "=== 修复 WLAN / 网络服务（不碰 USB）===")
    msg = _ensure_service("WinHttpAutoProxySvc", restart=False)
    steps.append(msg)
    _progress(msg)
    for svc in ("WlanSvc", "NlaSvc", "Dnscache", "netprofm", "Netman"):
        _progress(f"处理服务 {svc}…")
        msg = _ensure_service(svc, restart=True)
        steps.append(msg)
        _progress(msg)
    msg = _ensure_service("Dhcp", restart=False)  # 勿强行 Restart，易被代理服务卡住
    steps.append(msg)
    _progress(msg)

    _progress("刷新 DNS 缓存…")
    _run_hidden(["ipconfig", "/flushdns"], timeout=20)
    _step(steps, "已刷新 DNS 缓存")

    alive = _wlan_interface_alive()
    suggestions = []
    if alive:
        suggestions.append("无线接口已出现，可在任务栏连接 WiFi")
        _step(steps, "无线接口: 已检测到")
    else:
        suggestions.append(
            "仍无 WiFi 入口：请人工拔掉 USB 无线网卡等待 5 秒再插上，或点「等待拔插恢复」仅监测"
        )
        suggestions.append("若是内置网卡无法拔插，请保存文件后重启一次")
        _step(steps, "无线接口: 仍未出现")
    _progress("一键修复 WiFi 完成")
    return _network_result(alive, steps, suggestions=suggestions, wlan_alive=alive)


def network_fix_wifi_driver() -> dict:
    """软重置无线：仅重启 WLAN 服务（已移除删设备 / USB 注册表操作）。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(
            False, ["需要管理员权限才能重置驱动"], suggestions=["请右键以管理员身份运行"]
        )

    _step(steps, "=== 软重置无线服务（不删设备）===")
    for line in _soft_restart_wlan():
        steps.append(line)
        _progress(line)
    alive = _wlan_interface_alive()
    suggestions = []
    if alive:
        suggestions.append("服务已重启，WiFi 入口应可用")
        _step(steps, "无线接口: 已检测到")
    else:
        suggestions.append(
            "服务重启后仍无无线接口：请人工拔插 USB 无线网卡，或重启电脑（软件不再删除设备节点）"
        )
        suggestions.append("插上后可点「等待拔插恢复」监测是否回来")
        _step(steps, "无线接口: 仍未出现")
    _progress("软重置完成")
    return _network_result(
        alive, steps, suggestions=suggestions, wlan_alive=alive, need_unplug=not alive
    )


def network_wait_usb_replug(timeout_sec: float = 90.0) -> dict:
    """等待用户人工拔插 USB 无线网卡；只监测，不删设备、不改 USB 电源。"""
    import time

    steps: list[str] = [
        "安全模式：不会删除设备、不会改 USB 省电",
        "请现在拔掉 USB 无线网卡，等待约 5 秒，再插回去…",
        f"最多等待 {int(timeout_sec)} 秒",
    ]
    if not _is_admin():
        return _network_result(False, ["需要管理员权限"], suggestions=["请以管理员运行"])

    _run_hidden(["net", "start", "WlanSvc"], timeout=20)

    deadline = time.monotonic() + timeout_sec
    saw_gone = False
    while time.monotonic() < deadline:
        _run_hidden(["net", "start", "WlanSvc"], timeout=20)
        _code, text = _run_powershell(
            rf"""
$wifiRe = '{_WIFI_NAME_RE}'; $exclRe = '{_WIFI_EXCLUDE_RE}'
$d = @(Get-PnpDevice -Class Net | Where-Object {{ ($_.FriendlyName -match $wifiRe) -and ($_.FriendlyName -notmatch $exclRe) }})
if (-not $d.Count) {{ 'GONE' }} else {{ ($d | ForEach-Object {{ "$($_.Status)|$($_.Problem)|$($_.FriendlyName)" }}) -join ';' }}
""",
            timeout=25,
        )
        state = (text or "").strip()
        if state == "GONE" or not state:
            if not saw_gone:
                steps.append("已检测到设备断开，请插入 USB 网卡…")
                saw_gone = True
        else:
            steps.append(f"设备状态: {state}")
            if "OK|" in state or state.startswith("OK"):
                if _wlan_interface_alive():
                    steps.append("成功：无线接口已恢复")
                    return _network_result(
                        True,
                        steps,
                        suggestions=["WiFi 入口应已出现，请连接热点"],
                        wlan_alive=True,
                    )
                steps.append("设备 OK 但无线接口未就绪，继续等待…")
            elif "FAILED_START" in state:
                steps.append("仍为 Code 10，请再拔插一次或换 USB 口（优先 USB2 口）")
        time.sleep(3)

    alive = _wlan_interface_alive()
    return _network_result(
        alive,
        steps + ["等待超时"],
        suggestions=[
            "请换一个 USB 口再插，或保存文件后重启",
            "属硬件供电复位问题，软件侧不再干预 USB 设备节点",
        ],
        wlan_alive=alive,
        need_unplug=not alive,
    )


def network_reset_stack() -> dict:
    """重置 Winsock / TCP-IP，并刷新 DNS（部分步骤可能提示需要重启）。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(
            False, ["需要管理员权限才能重置协议栈"], suggestions=["请右键以管理员身份运行"]
        )

    steps.append(_ensure_service("WinHttpAutoProxySvc", restart=False))
    commands = [
        (["netsh", "winsock", "reset"], "Winsock 重置"),
        (["netsh", "int", "ip", "reset"], "IPv4 协议栈重置"),
        (["netsh", "int", "ipv6", "reset"], "IPv6 协议栈重置"),
        (["ipconfig", "/flushdns"], "刷新 DNS"),
        (["ipconfig", "/registerdns"], "注册 DNS"),
    ]
    need_reboot = False
    for args, label in commands:
        code, text = _run_hidden(args, timeout=60)
        low = (text or "").lower()
        if "restart" in low or "重新启动" in (text or "") or "重启" in (text or ""):
            need_reboot = True
        steps.append(
            f"{label}: {'成功' if code == 0 else '失败'} {(text or '')[:160]}"
        )

    for svc in ("NlaSvc", "Dnscache", "WlanSvc"):
        steps.append(_ensure_service(svc, restart=True))
    steps.append(_ensure_service("Dhcp", restart=False))

    suggestions = [
        "协议栈已重置；若提示需重启，请保存工作后重启一次",
        "WiFi 入口仍没有时请人工拔插 USB 无线网卡，或重启电脑",
    ]
    if need_reboot:
        suggestions.insert(0, "系统提示需要重启后网络设置才完全生效")
    return _network_result(True, steps, need_reboot=need_reboot, suggestions=suggestions)


def network_fix_ethernet() -> dict:
    """有线优先：启用/硬复位物理网卡、关节能、DHCP 续租（不碰 USB、不删设备、不需外网）。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(
            False, ["需要管理员权限"], suggestions=["请右键以管理员身份运行"]
        )

    _step(steps, "=== 有线网卡急救（识别了但网线不通）===")
    _progress("正在修复有线/物理网卡…")
    ps = r"""
$ErrorActionPreference = 'SilentlyContinue'
function Test-Virt([string]$n,[string]$d) {
  return [bool]("$n $d" -match 'Hyper-V|vEthernet|VMware|VirtualBox|TAP-|OpenVPN|WireGuard|Wintun|VPN|Loopback|Pseudo|Bluetooth|Wi-Fi Direct|Hosted Network')
}
$log = New-Object System.Collections.Generic.List[string]
foreach ($svc in @('nsi','NlaSvc','netprofm','Netman','Dhcp','Dnscache','WinHttpAutoProxySvc','LanmanWorkstation','BFE')) {
  $s = Get-Service $svc -EA SilentlyContinue; if (-not $s) { continue }
  $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
  $cfg = Get-ItemProperty $key -EA SilentlyContinue
  if ($cfg -and $cfg.Start -eq 4) {
    Set-ItemProperty $key Start 2 -Type DWord -EA SilentlyContinue
    [void]$log.Add("启用服务 $svc")
  }
  if ($s.Status -ne 'Running') {
    if ($svc -eq 'Dhcp') { Start-Service $svc -EA SilentlyContinue }
    else { Restart-Service $svc -Force -EA SilentlyContinue; Start-Service $svc -EA SilentlyContinue }
    if ((Get-Service $svc -EA SilentlyContinue).Status -eq 'Running') { [void]$log.Add("启动 $svc") }
  }
}
pnputil /scan-devices | Out-Null
Get-PnpDevice -Class Net -EA SilentlyContinue | ForEach-Object {
  if (Test-Virt $_.FriendlyName $_.FriendlyName) { return }
  $prob = 0; try { $prob = [int]$_.Problem } catch {}
  if ($_.Status -eq 'OK' -and $prob -eq 0) { return }
  Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -EA SilentlyContinue
  pnputil /enable-device "$($_.InstanceId)" | Out-Null
  pnputil /restart-device "$($_.InstanceId)" | Out-Null
  [void]$log.Add("启用设备 $($_.FriendlyName)")
}
$adapters = @(Get-NetAdapter -EA SilentlyContinue | Sort-Object {
  if ($_.MediaType -match '802\.3|Ethernet' -or $_.InterfaceDescription -match 'Ethernet|PCI|GbE|LAN') { 0 }
  elseif ($_.Name -match 'Wi-?Fi|Wireless|WLAN' -or $_.InterfaceDescription -match 'Wi-?Fi|Wireless|WLAN|802\.11') { 1 }
  else { 2 }
})
foreach ($a in $adapters) {
  if (Test-Virt $a.Name $a.InterfaceDescription) { continue }
  [void]$log.Add("网卡 $($a.Name): $($a.Status) · $($a.InterfaceDescription)")
  if ($a.AdminStatus -eq 'Down' -or $a.Status -eq 'Disabled') {
    Enable-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue
    [void]$log.Add("已启用 $($a.Name)")
  }
  try {
    $p = Get-NetAdapterPowerManagement -Name $a.Name -EA SilentlyContinue
    if ($p -and $p.AllowComputerToTurnOffDevice -eq 'Enabled') {
      Set-NetAdapterPowerManagement -Name $a.Name -AllowComputerToTurnOffDevice Disabled -EA SilentlyContinue
      [void]$log.Add("关闭节能 $($a.Name)")
    }
  } catch {}
  Disable-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue
  Start-Sleep -Milliseconds 500
  Enable-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue
  Restart-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue
  [void]$log.Add("硬复位 $($a.Name)")
}
Start-Sleep -Seconds 2
netsh winhttp reset proxy | Out-Null
$inet = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
if (Test-Path $inet) {
  Set-ItemProperty $inet ProxyEnable 0 -Type DWord -EA SilentlyContinue
  Remove-ItemProperty $inet ProxyServer -Force -EA SilentlyContinue
  Remove-ItemProperty $inet AutoConfigURL -Force -EA SilentlyContinue
  [void]$log.Add('已关闭用户代理')
}
arp -d * 2>$null | Out-Null
ipconfig /flushdns | Out-Null
foreach ($a in @(Get-NetAdapter -EA SilentlyContinue)) {
  if (Test-Virt $a.Name $a.InterfaceDescription) { continue }
  if ($a.Status -notin @('Up','Disconnected','Not Present')) { continue }
  Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -EA SilentlyContinue |
    Where-Object { $_.PrefixOrigin -eq 'Manual' -or $_.IPAddress -like '169.254.*' } |
    ForEach-Object {
      Remove-NetIPAddress -InterfaceIndex $_.InterfaceIndex -IPAddress $_.IPAddress -Confirm:$false -EA SilentlyContinue
      [void]$log.Add("清除坏地址 $($_.IPAddress)")
    }
  Set-NetIPInterface -InterfaceIndex $a.ifIndex -Dhcp Enabled -EA SilentlyContinue
  ipconfig /release "$($a.Name)" 2>$null | Out-Null
  ipconfig /renew "$($a.Name)" 2>$null | Out-Null
  [void]$log.Add("DHCP 续租 $($a.Name)")
}
$up = @(Get-NetAdapter -EA SilentlyContinue | Where-Object { -not (Test-Virt $_.Name $_.InterfaceDescription) -and $_.Status -eq 'Up' })
$ipOk = @(Get-NetIPConfiguration -EA SilentlyContinue | Where-Object {
  $_.NetAdapter.Status -eq 'Up' -and $_.IPv4Address -and ($_.IPv4Address.IPAddress -notlike '169.254.*')
})
[void]$log.Add("复检 Up=$($up.Count) 有效IP=$($ipOk.Count)")
foreach ($c in $ipOk) {
  $ip = ($c.IPv4Address | Select-Object -First 1).IPAddress
  $gw = ($c.IPv4DefaultGateway | Select-Object -First 1).NextHop
  [void]$log.Add("IP $($c.InterfaceAlias)=$ip 网关=$gw")
}
[PSCustomObject]@{ steps = @($log); up = $up.Count; ip = $ipOk.Count } | ConvertTo-Json -Compress -Depth 4
"""
    code, text = _run_powershell(ps, timeout=120)
    data: dict = {}
    if code == 0 and (text or "").strip():
        import json

        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            _step(steps, f"有线修复输出解析失败: {(text or '')[:200]}")
    else:
        _step(steps, f"有线修复失败: {(text or '')[:300] or code}")

    for s in data.get("steps") or []:
        _step(steps, str(s))
        _progress(str(s))

    up_n = int(data.get("up") or 0)
    ip_n = int(data.get("ip") or 0)
    suggestions: list[str] = []
    ok = up_n > 0 and ip_n > 0
    if ok:
        suggestions.append("有线/物理网卡已拿到地址，可试浏览器")
    elif up_n > 0:
        suggestions.append("网卡已 Up 但仍无有效 IP：查网线/交换机/路由器 DHCP，或继续「重置协议栈」后重启")
    else:
        suggestions.append(
            "网卡仍未 Up：确认已插网线；若设备管理器里网卡正常仍不行，多半是口坏/对端故障（软件无法改）"
        )
    _progress("有线网卡急救完成")
    return _network_result(ok, steps, suggestions=suggestions)


def network_full_repair() -> dict:
    """全面修复：有线优先 → WiFi 服务 → 协议栈（不碰 USB、不删设备、不需外网下驱动）。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(
            False, ["需要管理员权限"], suggestions=["请右键以管理员身份运行"]
        )

    _step(steps, "=== 1/4 有线/物理网卡急救 ===")
    r1 = network_fix_ethernet()
    for s in r1.get("steps") or []:
        steps.append(str(s))
        _progress(str(s))
    if r1.get("ok"):
        _progress("有线已恢复")
        return _network_result(
            True,
            steps,
            suggestions=["有线网络已恢复"] + list(r1.get("suggestions") or []),
            need_reboot=False,
        )

    _step(steps, "=== 2/4 软重启 WLAN 服务（不删设备）===")
    r2 = network_fix_wifi_driver()
    for s in r2.get("steps") or []:
        steps.append(str(s))
        _progress(str(s))

    _step(steps, "=== 3/4 修复服务与 DNS ===")
    for msg in (
        _ensure_service("WinHttpAutoProxySvc", restart=False),
        *(_ensure_service(svc, restart=True) for svc in ("WlanSvc", "NlaSvc", "Dnscache", "netprofm", "Netman")),
        _ensure_service("Dhcp", restart=False),
    ):
        steps.append(msg)
        _progress(msg)

    _step(steps, "=== 4/4 重置协议栈 ===")
    r3 = network_reset_stack()
    for s in r3.get("steps") or []:
        steps.append(str(s))
        _progress(str(s))

    # 协议栈后再跑一轮有线续租
    _progress("协议栈后再次续租 DHCP…")
    r4 = network_fix_ethernet()
    for s in (r4.get("steps") or [])[-8:]:
        steps.append(str(s))

    _step(steps, "=== 修复后诊断 ===")
    diag = network_diagnose()
    for s in (diag.get("steps") or [])[:16]:
        steps.append(str(s))
        _progress(str(s))
    suggestions = list(diag.get("suggestions") or [])
    online = bool(diag.get("ping_internet") or diag.get("ping_gateway"))
    wlan_ok = bool(diag.get("wlan_alive"))
    eth_ok = bool(r4.get("ok") or r1.get("ok"))
    if not online and not eth_ok:
        suggestions.insert(
            0,
            "服务/协议栈已处理仍无网络：请确认网线已插、换口/换线；无线则人工拔插或重启（软件不再动 USB）",
        )
    ok = online or eth_ok or wlan_ok
    _progress("网络全面修复完成")
    return _network_result(
        ok,
        steps,
        suggestions=suggestions,
        wlan_alive=wlan_ok,
        need_reboot=bool(r3.get("need_reboot")),
        need_unplug=bool(diag.get("need_unplug")),
    )


def _app_dir() -> Path:
    """可写的软件目录（exe 旁；开发时为脚本目录）。"""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


def _wifi_backup_dir() -> Path:
    d = _app_dir() / "wifi_backup"
    d.mkdir(parents=True, exist_ok=True)
    return d


def _list_wlan_profile_names() -> list[str]:
    import re

    _code, text = _run_hidden(["netsh", "wlan", "show", "profiles"], timeout=30)
    names: list[str] = []
    pat = re.compile(
        r"(?:所有用户配置文件|用户配置文件|All User Profile|User Profile)\s*:\s*(.+?)\s*$",
        re.IGNORECASE,
    )
    for line in (text or "").splitlines():
        m = pat.search(line.strip())
        if m:
            name = m.group(1).strip()
            if name and name not in names:
                names.append(name)
    return names


def _profile_password(name: str) -> tuple[str, str, str]:
    _c, detail = _run_hidden(
        ["netsh", "wlan", "show", "profile", f"name={name}", "key=clear"],
        timeout=25,
    )
    password = auth = cipher = ""
    for line in (detail or "").splitlines():
        if ":" not in line:
            continue
        key, val = line.split(":", 1)
        key_s, val_s = key.strip(), val.strip()
        kl = key_s.lower()
        if "关键内容" in key_s or "key content" in kl:
            password = val_s
        elif key_s == "身份验证" or kl == "authentication":
            auth = val_s
        elif key_s == "密码" or "cipher" in kl:
            if not cipher:
                cipher = val_s
    if not password:
        password = "(无密码/开放网络)" if _is_admin() else "(需管理员才能显示密码)"
    return password, auth, cipher


def _ssid_from_wlan_xml(path: Path) -> str:
    try:
        import re

        text = path.read_text(encoding="utf-8", errors="ignore")
        m = re.search(
            r"<SSID>\s*<name>([^<]+)</name>",
            text,
            re.IGNORECASE | re.DOTALL,
        )
        if m:
            return m.group(1).strip()
        m2 = re.search(r"<name>([^<]+)</name>", text, re.IGNORECASE)
        if m2:
            return m2.group(1).strip()
    except Exception:
        pass
    return path.stem.replace("WLAN-", "").replace("Wi-Fi-", "")


def network_backup_wifi() -> dict:
    """备份本机 WiFi 配置到软件目录 wifi_backup（含密码的 XML，可直接再导入）。"""
    from datetime import datetime

    steps: list[str] = []
    profiles: list[dict] = []
    if not _is_admin():
        return _network_result(
            False,
            ["需要管理员权限才能导出带密码的 WiFi 配置"],
            suggestions=["请右键以管理员身份运行"],
            profiles=[],
        )

    bak = _wifi_backup_dir()
    steps.append(f"备份目录: {bak}")

    # 清理旧 xml，保持目录为当前完整备份
    for old in bak.glob("*.xml"):
        try:
            old.unlink()
        except Exception:
            pass

    names = _list_wlan_profile_names()
    if not names:
        return _network_result(
            False,
            steps + ["本机没有已保存的 WiFi 配置可备份"],
            suggestions=["先手动连一次 WiFi 并勾选自动连接，再备份"],
            profiles=[],
            export_path=str(bak),
        )

    steps.append(f"共 {len(names)} 个配置，开始导出…")
    ok_n = 0
    for name in names:
        code, text = _run_hidden(
            [
                "netsh",
                "wlan",
                "export",
                "profile",
                f"name={name}",
                f"folder={bak}",
                "key=clear",
            ],
            timeout=30,
        )
        password, auth, cipher = _profile_password(name)
        profiles.append(
            {"ssid": name, "password": password, "auth": auth, "cipher": cipher}
        )
        if code == 0:
            ok_n += 1
            steps.append(f"已导出: {name}")
        else:
            steps.append(f"导出失败: {name} — {(text or '')[:80]}")

    # 可读摘要
    summary = bak / "wifi_profiles.txt"
    try:
        lines = [
            "WiFi 备份摘要（XML 可一键导入，无需手输密码）",
            f"时间: {datetime.now():%Y-%m-%d %H:%M:%S}",
            f"目录: {bak}",
            "",
        ]
        for p in profiles:
            lines += [
                f"名称: {p['ssid']}",
                f"密码: {p['password']}",
                f"认证: {p.get('auth') or '-'}",
                "-" * 32,
            ]
        summary.write_text("\n".join(lines), encoding="utf-8")
        steps.append(f"摘要: {summary}")
    except Exception as e:
        steps.append(f"写摘要失败: {e}")

    xml_count = len(list(bak.glob("*.xml")))
    suggestions = [
        f"已备份 {ok_n} 个配置，目录内 {xml_count} 个 XML",
        "换机或重装后点「导入 WiFi」即可自动写入系统，无需再输密码",
    ]
    return _network_result(
        ok_n > 0,
        steps,
        profiles=profiles,
        export_path=str(bak),
        suggestions=suggestions,
    )


def network_restore_wifi() -> dict:
    """从软件目录 wifi_backup 导入全部 WiFi，并尝试自动连接（无需手输密码）。"""
    steps: list[str] = []
    profiles: list[dict] = []
    if not _is_admin():
        return _network_result(
            False,
            ["需要管理员权限才能导入 WiFi"],
            suggestions=["请右键以管理员身份运行"],
            profiles=[],
        )

    bak = _wifi_backup_dir()
    xmls = sorted(bak.glob("*.xml"))
    steps.append(f"备份目录: {bak}")
    if not xmls:
        return _network_result(
            False,
            steps + ["未找到备份 XML，请先点「备份 WiFi」"],
            suggestions=["先在本机连上 WiFi 后执行备份"],
            profiles=[],
            export_path=str(bak),
        )

    _ensure_service("WlanSvc", restart=False)
    imported: list[str] = []
    for xml in xmls:
        ssid = _ssid_from_wlan_xml(xml)
        code, text = _run_hidden(
            ["netsh", "wlan", "add", "profile", f"filename={xml}", "user=all"],
            timeout=30,
        )
        if code != 0:
            # 回退当前用户
            code, text = _run_hidden(
                ["netsh", "wlan", "add", "profile", f"filename={xml}", "user=current"],
                timeout=30,
            )
        if code == 0:
            imported.append(ssid)
            password, auth, cipher = _profile_password(ssid)
            profiles.append(
                {"ssid": ssid, "password": password, "auth": auth, "cipher": cipher}
            )
            steps.append(f"已导入: {ssid}")
        else:
            steps.append(f"导入失败: {xml.name} — {(text or '')[:100]}")

    if not imported:
        return _network_result(
            False,
            steps,
            suggestions=["导入失败，请确认 XML 完整且以管理员运行"],
            profiles=profiles,
            export_path=str(bak),
        )

    # 尝试自动连接：优先已导入且当前能扫到的网络
    steps.append("=== 尝试自动连接 ===")
    _code, nets = _run_hidden(["netsh", "wlan", "show", "networks", "mode=bssid"], timeout=40)
    visible = (nets or "").lower()
    connected = False
    # 先连当前能扫到的
    order = [s for s in imported if s.lower() in visible] + [
        s for s in imported if s.lower() not in visible
    ]
    for ssid in order:
        code, text = _run_hidden(
            ["netsh", "wlan", "connect", f"name={ssid}", f"ssid={ssid}"],
            timeout=40,
        )
        if code != 0:
            code, text = _run_hidden(
                ["netsh", "wlan", "connect", f"name={ssid}"],
                timeout=40,
            )
        msg = (text or "").strip()
        if code == 0 or "已完成" in msg or "successfully" in msg.lower():
            steps.append(f"正在连接: {ssid} — {msg or '已发送连接请求'}")
            connected = True
            # 等一会看是否关联上
            import time

            time.sleep(4)
            if _wlan_interface_alive():
                _c, iface = _run_hidden(["netsh", "wlan", "show", "interfaces"], timeout=15)
                if ssid in (iface or "") or "已连接" in (iface or "") or "connected" in (iface or "").lower():
                    steps.append(f"已连接到: {ssid}")
                    break
            break
        steps.append(f"连接未成功: {ssid} — {msg[:80]}")

    suggestions = [
        f"已导入 {len(imported)} 个 WiFi 到系统（无需手输密码）",
        "可在任务栏 WiFi 列表直接点选连接" if not connected else "已尝试自动连接",
    ]
    return _network_result(
        True,
        steps,
        profiles=profiles,
        export_path=str(bak),
        suggestions=suggestions,
        wlan_alive=_wlan_interface_alive(),
    )


def network_recover_wifi_passwords() -> dict:
    """兼容旧接口：执行备份（软件目录）。"""
    return network_backup_wifi()


# ---------------------------------------------------------------------------
# 共享文件 / SMB 疑难杂症修复（Win11 24H2 NAS、网络发现、防火墙等）
# ---------------------------------------------------------------------------

_SHARE_SERVICES = (
    "LanmanServer",       # Server — 本机被访问
    "LanmanWorkstation",  # Workstation — 访问别人
    "fdPHost",            # Function Discovery Provider Host
    "FDResPub",           # Function Discovery Resource Publication
    "SSDPSRV",            # SSDP Discovery
    "upnphost",           # UPnP Device Host
    "lmhosts",            # TCP/IP NetBIOS Helper
    "dnscache",
)


def share_diagnose() -> dict:
    """诊断共享相关：配置文件、服务、防火墙、SMB 来宾/签名、445 监听。"""
    steps: list[str] = []
    suggestions: list[str] = []
    if not _is_admin():
        steps.append("提示: 非管理员，部分项可能看不到")

    ps = r"""
$ErrorActionPreference = 'SilentlyContinue'
$out = [ordered]@{}

# 网络配置文件
$profiles = @(Get-NetConnectionProfile | ForEach-Object {
  [PSCustomObject]@{ name=$_.Name; category=[string]$_.NetworkCategory; iface=$_.InterfaceAlias }
})
$out.profiles = $profiles

# 关键服务
$svcs = @()
foreach ($n in @('LanmanServer','LanmanWorkstation','fdPHost','FDResPub','SSDPSRV','upnphost','lmhosts','dnscache')) {
  $s = Get-Service -Name $n -ErrorAction SilentlyContinue
  if ($s) { $svcs += [PSCustomObject]@{ name=$s.Name; status=[string]$s.Status; start=[string]$s.StartType } }
}
$out.services = $svcs

# SMB 客户端配置（Win11 24H2 关键）
try {
  $c = Get-SmbClientConfiguration
  $out.smb_client = [PSCustomObject]@{
    EnableInsecureGuestLogons = [bool]$c.EnableInsecureGuestLogons
    RequireSecuritySignature = [bool]$c.RequireSecuritySignature
    EnableSecuritySignature = [bool]$c.EnableSecuritySignature
  }
} catch {
  $out.smb_client = $null
}

# 防火墙组（中英）——按组名启用统计，避免 Get-NetFirewallRule 全表扫描卡死
$fw = @()
foreach ($g in @('File and Printer Sharing','Network Discovery','文件和打印机共享','网络发现')) {
  $rules = @(Get-NetFirewallRule -DisplayGroup $g -ErrorAction SilentlyContinue)
  if ($rules.Count -gt 0) {
    $enabled = @($rules | Where-Object { $_.Enabled -eq 'True' }).Count
    $fw += [PSCustomObject]@{ group=$g; total=$rules.Count; enabled=$enabled }
  }
}
$out.firewall = $fw

# 445 监听
$listen = [bool](Get-NetTCPConnection -LocalPort 445 -State Listen -ErrorAction SilentlyContinue)
$out.port445 = $listen

# SMB1：跳过 Get-WindowsOptionalFeature（DISM 经常卡数分钟）
$out.smb1 = 'skipped'

# 本机共享列表
try {
  $shares = @(Get-SmbShare | Where-Object { $_.Name -notin @('IPC$') } | Select-Object -First 20 Name, Path, Description)
  $out.shares = $shares
} catch { $out.shares = @() }

$out | ConvertTo-Json -Depth 6 -Compress
"""
    code, text = _run_powershell(ps, timeout=35)
    data: dict = {}
    if code == 0 and text.strip():
        import json

        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            steps.append(f"诊断解析失败: {text[:200]}")
    else:
        steps.append(f"诊断失败: {(text or str(code))[:200]}")

    # 解析建议
    for p in data.get("profiles") or []:
        cat = str(p.get("category", ""))
        steps.append(f"[网络] {p.get('iface') or p.get('name')}: {cat}")
        if cat.lower() == "public" or cat == "公用":
            suggestions.append("网络配置文件为公用 → 点「设为专用网络」")

    for s in data.get("services") or []:
        st = str(s.get("status", ""))
        steps.append(f"[服务] {s.get('name')}: {st} ({s.get('start')})")
        if st.lower() != "running":
            suggestions.append(f"服务未运行: {s.get('name')} → 点「修复网络发现」或「全面修复共享」")

    smb = data.get("smb_client") or {}
    if smb:
        guest = smb.get("EnableInsecureGuestLogons")
        req_sig = smb.get("RequireSecuritySignature")
        steps.append(f"[SMB] 允许不安全来宾={guest}  强制签名={req_sig}")
        if guest is False:
            suggestions.append(
                "已禁用不安全来宾登录（Win11 常见）→ 访问 NAS/匿名共享请点「修复 Win11/NAS 来宾」"
            )
        if req_sig is True:
            suggestions.append(
                "已强制 SMB 签名（24H2 默认）→ 旧 NAS/Samba 常连不上，点「修复 Win11/NAS 来宾」"
            )

    for f in data.get("firewall") or []:
        group = str(f.get("group") or "")
        enabled = int(f.get("enabled") or 0)
        total = int(f.get("total") or 0)
        steps.append(f"[防火墙] {group}: 启用 {enabled}/{total}")
        # 「限制」组常为 0，不算异常；只盯主规则组
        gl = group.lower()
        if "限制" in group or "restrict" in gl:
            continue
        if total > 0 and enabled == 0:
            suggestions.append("防火墙未放行共享/发现 → 点「放行共享防火墙」")

    steps.append(f"[端口] 本机 445 监听: {'是' if data.get('port445') else '否'}")
    if not data.get("port445"):
        suggestions.append("本机未监听 445 → 检查 LanmanServer，或本机不需要被访问可忽略")

    steps.append(f"[SMB1] 状态: {data.get('smb1') or '未知'}")
    if str(data.get("smb1", "")).lower() in ("disabled", "disablepending"):
        suggestions.append("极旧设备需要 SMB1 时再点「启用 SMB1（不推荐）」")
    elif str(data.get("smb1", "")).lower() == "skipped":
        steps.append("[SMB1] 已跳过慢速检测（需要时再手动启用）")

    host = os.environ.get("COMPUTERNAME", ".")
    for sh in data.get("shares") or []:
        if not isinstance(sh, dict):
            continue
        name = sh.get("Name") or sh.get("name") or ""
        path = sh.get("Path") or sh.get("path") or ""
        if not name:
            continue
        steps.append(f"[共享] \\\\{host}\\{name} → {path}")

    if not suggestions:
        suggestions.append("未发现明显异常；若仍无法访问，清理凭据后用 \\\\IP\\共享名 直连测试")

    suggestions.append("安全提示: 来宾/关签名仅建议家庭局域网；办公室请用账号密码映射")
    return _network_result(
        True,
        steps,
        suggestions=suggestions,
        share_info=data,
    )


def share_fix_win11_nas() -> dict:
    """修复 Win11 24H2 访问 NAS/匿名共享：允许来宾 + 取消强制 SMB 签名。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(
            False, ["需要管理员权限"], suggestions=["请以管理员运行"]
        )

    steps.append("⚠ 将降低 SMB 安全性，仅用于家庭/信任局域网")
    _progress(steps[-1])
    # PowerShell SMB 客户端
    _progress("配置 SMB 客户端（来宾/签名）…")
    code, text = _run_powershell(
        "Set-SmbClientConfiguration -EnableInsecureGuestLogons $true -Force; "
        "Set-SmbClientConfiguration -RequireSecuritySignature $false -Force; "
        "Get-SmbClientConfiguration | "
        "Select-Object EnableInsecureGuestLogons,RequireSecuritySignature,EnableSecuritySignature | "
        "ConvertTo-Json -Compress",
        timeout=45,
    )
    steps.append(f"SMB 客户端配置: {(text or code)!s}"[:200])
    _progress(steps[-1])

    # 注册表兜底（部分版本策略优先）
    _progress("写入 AllowInsecureGuestAuth 注册表…")
    code2, text2 = _run_powershell(
        r"""
$ErrorActionPreference='Continue'
$path = 'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters'
if (-not (Test-Path $path)) { New-Item $path -Force | Out-Null }
New-ItemProperty -Path $path -Name AllowInsecureGuestAuth -Value 1 -PropertyType DWord -Force | Out-Null
# 策略路径
$gp = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\LanmanWorkstation'
if (-not (Test-Path $gp)) { New-Item $gp -Force | Out-Null }
New-ItemProperty -Path $gp -Name AllowInsecureGuestAuth -Value 1 -PropertyType DWord -Force | Out-Null
'registry ok'
""",
        timeout=30,
    )
    steps.append(f"注册表 AllowInsecureGuestAuth: {(text2 or code2)}")
    _progress(steps[-1])

    # 组策略模板项（若存在）
    _progress("刷新 LanmanWorkstation…")
    _run_powershell(
        "Set-SmbServerConfiguration -RequireSecuritySignature $false -Force -ErrorAction SilentlyContinue"
    )
    steps.append(_ensure_service("LanmanWorkstation", restart=True))
    _progress(steps[-1])

    suggestions = [
        "已允许不安全来宾并关闭强制 SMB 签名",
        "请再试 \\\\NAS的IP\\共享名；仍失败可清凭据或启用 SMB1（极旧设备）",
    ]
    return _network_result(code == 0, steps, suggestions=suggestions)


def share_fix_discovery() -> dict:
    """修复网络发现：相关服务 + 高级共享设置中的网络发现/文件共享。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(False, ["需要管理员权限"], suggestions=["请以管理员运行"])

    # 多数服务只需确保在跑；全量 restart 容易被依赖卡住
    for svc in ("fdPHost", "FDResPub", "SSDPSRV", "upnphost", "lmhosts", "dnscache"):
        _progress(f"网络发现 · 确保服务 {svc}…")
        _run_powershell(
            f"Set-Service -Name '{svc}' -StartupType Automatic -ErrorAction SilentlyContinue",
            timeout=12,
        )
        # 仅对核心发现服务做软重启，其余只启动
        do_restart = svc in ("fdPHost", "FDResPub")
        msg = _ensure_service(svc, restart=do_restart)
        steps.append(msg)
        _progress(msg)

    cmds = [
        (["netsh", "advfirewall", "firewall", "set", "rule", "group=Network Discovery", "new", "enable=Yes"], "网络发现防火墙(英)"),
        (["netsh", "advfirewall", "firewall", "set", "rule", "group=网络发现", "new", "enable=Yes"], "网络发现防火墙(中)"),
    ]
    for args, label in cmds:
        _progress(f"{label}…")
        c, t = _run_hidden(args, timeout=15)
        msg = f"{label}: {'成功' if c == 0 else '跳过'}"
        steps.append(msg)
        _progress(msg)

    suggestions = [
        "网络发现服务已修复；请确认网络为「专用」",
        "资源管理器左侧「网络」应能逐渐刷出其它电脑",
    ]
    return _network_result(True, steps, suggestions=suggestions)


def share_fix_firewall() -> dict:
    """放行「文件和打印机共享」与「网络发现」防火墙规则。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(False, ["需要管理员权限"], suggestions=["请以管理员运行"])

    _progress("正在放行共享防火墙规则…")
    # 只按 DisplayGroup 启用，避免 Get-NetFirewallRule 全表扫描卡很久
    ps = r"""
$ErrorActionPreference='Continue'
$groups = @('File and Printer Sharing','Network Discovery','文件和打印机共享','网络发现')
foreach ($g in $groups) {
  try {
    Enable-NetFirewallRule -DisplayGroup $g -ErrorAction Stop | Out-Null
    "enabled: $g"
  } catch {
    $null = netsh advfirewall firewall set rule group="$g" new enable=Yes 2>&1
    "netsh: $g"
  }
}
"done"
"""
    code, text = _run_powershell(ps, timeout=45)
    for ln in (text or "").splitlines():
        if ln.strip():
            steps.append(ln.strip())
            _progress(ln.strip())
    suggestions = ["防火墙已放行共享相关规则；公用网络下仍可能受限，建议切专用"]
    return _network_result(code == 0 or bool(text), steps, suggestions=suggestions)


def share_fix_private_profile() -> dict:
    """将当前已连接网络设为专用（Private），否则发现/共享常被挡。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(False, ["需要管理员权限"], suggestions=["请以管理员运行"])

    _progress("读取并设置网络配置文件…")
    code, text = _run_powershell(
        r"""
$ErrorActionPreference='Continue'
$list = @(Get-NetConnectionProfile)
if (-not $list.Count) { '无活动网络配置文件'; exit 1 }
foreach ($p in $list) {
  $before = [string]$p.NetworkCategory
  try {
    Set-NetConnectionProfile -InterfaceIndex $p.InterfaceIndex -NetworkCategory Private
    $after = [string](Get-NetConnectionProfile -InterfaceIndex $p.InterfaceIndex).NetworkCategory
    "$($p.InterfaceAlias): $before → $after"
  } catch {
    "$($p.InterfaceAlias): 失败 $($_.Exception.Message)"
  }
}
""",
        timeout=40,
    )
    for ln in (text or "").splitlines():
        if ln.strip():
            steps.append(ln.strip())
            _progress(ln.strip())
    ok = code == 0 and any("→" in s for s in steps)
    return _network_result(
        ok,
        steps or [text or "失败"],
        suggestions=["已尝试设为专用网络；Wi‑Fi/以太网请在设置里确认「专用」"],
    )


def share_restart_smb_services() -> dict:
    """确保 SMB 核心服务在跑；仅对 Workstation 做软重启（Server 停掉易卡死）。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(False, ["需要管理员权限"], suggestions=["请以管理员运行"])
    _progress("检查 WinHttpAutoProxySvc…")
    steps.append(_ensure_service("WinHttpAutoProxySvc", restart=False))
    _progress(steps[-1])
    # LanmanServer 只确保启动，不做 stop（文件占用时会卡死）
    for svc, restart in (("LanmanWorkstation", True), ("LanmanServer", False), ("lmhosts", False)):
        _progress(f"{'软重启' if restart else '确保'} {svc}…")
        steps.append(_ensure_service(svc, restart=restart))
        _progress(steps[-1])
    return _network_result(True, steps, suggestions=["SMB 服务已处理，请重试 \\\\IP\\共享"])


def share_clear_credentials() -> dict:
    """清理凭据管理器中可能过期的网络共享凭据。"""
    steps: list[str] = []
    _progress("枚举凭据管理器…")
    code, text = _run_hidden(["cmdkey", "/list"], timeout=20)
    lines = (text or "").splitlines()
    targets: list[str] = []
    for i, line in enumerate(lines):
        if "目标" in line or "Target" in line:
            # 目标: Domain:target=xxx  or LegacyGeneric:target=...
            if ":" in line:
                t = line.split(":", 1)[1].strip()
                if t:
                    targets.append(t)
    steps.append(f"发现 {len(targets)} 条凭据条目")
    _progress(steps[-1])
    removed = 0
    for t in targets:
        # 只删明显是网络共享的
        low = t.lower()
        if not any(
            x in low
            for x in (
                "domain:target=",
                "terrasync",
                "smb:",
                "microsoftaccount:target=SSO_POP",
            )
        ):
            # Domain:target=192.168... or computername
            if "domain:target=" not in low and "legacygeneric:target=" not in low:
                continue
        # 跳过明显非共享
        if "microsoftaccount" in low or "git:" in low:
            continue
        c, msg = _run_hidden(["cmdkey", "/delete", t], timeout=15)
        if c == 0:
            removed += 1
            steps.append(f"已删除: {t}")
            _progress(steps[-1])
        else:
            # 有的目标需要去掉前缀再删
            if "target=" in t:
                short = t.split("target=", 1)[-1].strip()
                c2, _ = _run_hidden(["cmdkey", f"/delete:{short}"], timeout=15)
                if c2 == 0:
                    removed += 1
                    steps.append(f"已删除: {short}")
                    _progress(steps[-1])
    if removed == 0:
        steps.append("未删除条目（可能没有过期共享凭据，或需手动在「凭据管理器」清理）")
        _progress(steps[-1])
    suggestions = [
        f"已清理 {removed} 条凭据",
        "请重新访问共享并输入正确账号密码（勾选记住）",
    ]
    return _network_result(True, steps, suggestions=suggestions)


def share_enable_smb1() -> dict:
    """启用 SMB1（仅极旧 NAS/XP 设备需要，有安全风险）。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(False, ["需要管理员权限"], suggestions=["请以管理员运行"])
    steps.append("⚠ SMB1 不安全，仅临时用于无法升级的老设备")
    code, text = _run_powershell(
        "Enable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart -All; "
        "(Get-WindowsOptionalFeature -Online -FeatureName SMB1Protocol).State",
        timeout=180,
    )
    steps.append((text or str(code))[:300])
    suggestions = [
        "SMB1 已请求启用；可能需要重启后生效",
        "设备升级后请尽快禁用 SMB1",
    ]
    return _network_result(
        "Enabled" in (text or "") or code == 0,
        steps,
        suggestions=suggestions,
        need_reboot="Enabled" not in (text or "") and "Pending" in (text or ""),
    )


def share_set_password_protected(enabled: bool) -> dict:
    """开关「密码保护的共享」（对应控制面板高级共享设置）。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(False, ["需要管理员权限"], suggestions=["请以管理员运行"])

    # enabled=True → 需要账号密码；False → 家庭局域网免密（ForceGuest + Guest）
    force_guest = 0 if enabled else 1
    limit_blank = 1 if enabled else 0
    guest_active = "no" if enabled else "yes"
    ps = rf"""
$ErrorActionPreference='Continue'
$path = 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa'
New-ItemProperty -Path $path -Name ForceGuest -Value {force_guest} -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $path -Name LimitBlankPasswordUse -Value {limit_blank} -PropertyType DWord -Force | Out-Null
net user guest /active:{guest_active} 2>&1 | Out-String
Restart-Service LanmanServer -Force -ErrorAction SilentlyContinue
"ForceGuest={force_guest}; LimitBlankPasswordUse={limit_blank}; guest={guest_active}"
"""
    code, text = _run_powershell(ps, timeout=45)
    for ln in (text or "").splitlines():
        if ln.strip():
            steps.append(ln.strip()[:180])
    label = "已开启密码保护共享（访问需账号密码）" if enabled else "已关闭密码保护共享（家庭免密互访）"
    return _network_result(
        code == 0 or bool(text),
        steps,
        suggestions=[label, "仅建议在信任局域网关闭密码保护"],
        password_protected=enabled,
    )


def share_disable_password_protected() -> dict:
    """关闭「密码保护的共享」（家庭互访常用）。"""
    return share_set_password_protected(False)


def share_enable_password_protected() -> dict:
    """开启「密码保护的共享」（更安全）。"""
    return share_set_password_protected(True)


def _share_local_ips() -> list[str]:
    code, text = _run_powershell(
        r"""
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
  Where-Object { $_.IPAddress -notlike '127.*' -and $_.PrefixOrigin -ne 'WellKnown' } |
  Select-Object -ExpandProperty IPAddress
""",
        timeout=25,
    )
    ips: list[str] = []
    for ln in (text or "").splitlines():
        s = ln.strip()
        if s and s not in ips:
            ips.append(s)
    return ips


def _share_parse_json(text: str) -> dict | list | None:
    import json

    raw = (text or "").strip()
    if not raw:
        return None
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        # 截取第一个 { 或 [
        for i, ch in enumerate(raw):
            if ch in "{[":
                try:
                    return json.loads(raw[i:])
                except json.JSONDecodeError:
                    break
    return None


def share_get_settings() -> dict:
    """读取共享设置状态、本机访问地址、共享列表（设置面板用）。"""
    steps: list[str] = []
    host = os.environ.get("COMPUTERNAME", ".")
    ips = _share_local_ips()

    ps = r"""
$ErrorActionPreference='SilentlyContinue'
$out = [ordered]@{}
$out.computer = $env:COMPUTERNAME
$out.profiles = @(Get-NetConnectionProfile | ForEach-Object {
  [PSCustomObject]@{ name=$_.Name; category=[string]$_.NetworkCategory; iface=$_.InterfaceAlias }
})
$svc = Get-Service LanmanServer -ErrorAction SilentlyContinue
$out.server = if ($svc) { [string]$svc.Status } else { 'Missing' }
$fg = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name ForceGuest -ErrorAction SilentlyContinue).ForceGuest
$out.force_guest = [int]($fg)
$out.password_protected = -not ([bool]$fg)
$guest = (net user guest 2>&1 | Out-String)
$out.guest_active = ($guest -match 'Account active\s+Yes' -or $guest -match '帐户启用\s+Yes' -or $guest -match '帐户已启用')
try {
  $c = Get-SmbClientConfiguration
  $out.smb_guest = [bool]$c.EnableInsecureGuestLogons
  $out.smb_require_sign = [bool]$c.RequireSecuritySignature
} catch {
  $out.smb_guest = $null
  $out.smb_require_sign = $null
}
$fwOn = @(Get-NetFirewallRule -DisplayGroup '文件和打印机共享','File and Printer Sharing' -ErrorAction SilentlyContinue |
  Where-Object { $_.Enabled -eq 'True' }).Count
$out.firewall_file_share_enabled = ($fwOn -gt 0)
$discOn = @(Get-NetFirewallRule -DisplayGroup '网络发现','Network Discovery' -ErrorAction SilentlyContinue |
  Where-Object { $_.Enabled -eq 'True' }).Count
$out.firewall_discovery_enabled = ($discOn -gt 0)
$shares = @()
Get-SmbShare -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin @('IPC$') } | ForEach-Object {
  $acc = @((Get-SmbShareAccess -Name $_.Name -ErrorAction SilentlyContinue |
    ForEach-Object { "$($_.AccountName):$($_.AccessRight)" }) -join '; ')
  $shares += [PSCustomObject]@{
    name = $_.Name; path = $_.Path; description = [string]$_.Description;
    special = [bool]$_.Special; encrypt = [string]$_.EncryptData; access = $acc
  }
}
$out.shares = $shares
$out | ConvertTo-Json -Depth 6 -Compress
"""
    code, text = _run_powershell(ps, timeout=60)
    data = _share_parse_json(text) if code == 0 else None
    if not isinstance(data, dict):
        data = {}
        steps.append(f"读取设置失败: {(text or str(code))[:200]}")

    for p in data.get("profiles") or []:
        steps.append(f"[网络] {p.get('iface') or p.get('name')}: {p.get('category')}")
    steps.append(f"[电脑名] {host}")
    for ip in ips:
        steps.append(f"[本机IP] {ip}")
    steps.append(f"[Server] {data.get('server')}")
    steps.append(
        f"[密码保护共享] {'开' if data.get('password_protected') else '关'}  "
        f"(ForceGuest={data.get('force_guest')})"
    )
    steps.append(
        f"[防火墙] 文件共享={'开' if data.get('firewall_file_share_enabled') else '关'}  "
        f"网络发现={'开' if data.get('firewall_discovery_enabled') else '关'}"
    )

    shares_out: list[dict] = []
    for sh in data.get("shares") or []:
        if not isinstance(sh, dict):
            continue
        name = sh.get("name") or sh.get("Name") or ""
        path = sh.get("path") or sh.get("Path") or ""
        if not name:
            continue
        item = {
            "name": name,
            "path": path,
            "description": sh.get("description") or sh.get("Description") or "",
            "special": bool(sh.get("special") if "special" in sh else sh.get("Special")),
            "access": sh.get("access") or "",
            "unc_name": f"\\\\{host}\\{name}",
            "unc_ips": [f"\\\\{ip}\\{name}" for ip in ips],
        }
        shares_out.append(item)
        steps.append(f"[共享] {item['unc_name']} → {path}")

    access_lines = [f"\\\\{host}"] + [f"\\\\{ip}" for ip in ips]
    for sh in shares_out:
        if not sh.get("special") and not str(sh["name"]).endswith("$"):
            access_lines.append(sh["unc_name"])
            access_lines.extend(sh["unc_ips"][:2])

    suggestions = [
        "把下面的 \\\\IP\\共享名 发给局域网其它电脑即可访问",
        "首次被访问：点「一键开启本机共享」；访问别人/NAS：用下方疑难修复",
    ]
    return _network_result(
        True,
        steps,
        suggestions=suggestions,
        computer=host,
        ips=ips,
        access_lines=access_lines,
        shares=shares_out,
        settings={
            "password_protected": bool(data.get("password_protected")),
            "guest_active": bool(data.get("guest_active")),
            "server_running": str(data.get("server", "")).lower() == "running",
            "firewall_file_share": bool(data.get("firewall_file_share_enabled")),
            "firewall_discovery": bool(data.get("firewall_discovery_enabled")),
            "smb_guest": data.get("smb_guest"),
            "smb_require_sign": data.get("smb_require_sign"),
            "profiles": data.get("profiles") or [],
        },
    )


def share_enable_hosting(close_password: bool = True) -> dict:
    """一键开启「本机可被局域网访问」：专用网络 + 发现 + 文件共享 + 防火墙 + Server。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(False, ["需要管理员权限"], suggestions=["请以管理员运行"])

    steps.append("=== 设为专用网络 ===")
    steps.extend(share_fix_private_profile().get("steps") or [])

    steps.append("=== 网络发现服务 ===")
    steps.extend(share_fix_discovery().get("steps") or [])

    steps.append("=== 放行防火墙 ===")
    steps.extend(share_fix_firewall().get("steps") or [])

    steps.append("=== 启动文件共享服务 ===")
    for svc in ("LanmanServer", "LanmanWorkstation", "fdPHost", "FDResPub"):
        steps.append(_ensure_service(svc, restart=(svc == "LanmanServer")))

    # 启用「文件和打印机共享」语义上的发布
    _run_powershell(
        r"""
$ErrorActionPreference='SilentlyContinue'
# 启用 NetBIOS over TCP/IP（部分局域网名称解析依赖）
Get-CimInstance Win32_NetworkAdapterConfiguration | Where-Object { $_.IPEnabled } | ForEach-Object {
  try { $_.SetTcpipNetbios(1) | Out-Null } catch {}
}
""",
        timeout=40,
    )
    steps.append("已尝试启用 NetBIOS（名称解析）")

    if close_password:
        steps.append("=== 关闭密码保护共享（家庭） ===")
        steps.extend(share_set_password_protected(False).get("steps") or [])

    overview = share_get_settings()
    steps.append("=== 当前访问地址 ===")
    steps.extend((overview.get("access_lines") or [])[:12])

    suggestions = [
        "本机共享通道已打开；请在「共享功能」里创建文件夹共享",
        "其它电脑请访问：" + "  或  ".join((overview.get("access_lines") or [])[:3]),
    ]
    return _network_result(
        True,
        steps,
        suggestions=suggestions,
        computer=overview.get("computer"),
        ips=overview.get("ips") or [],
        access_lines=overview.get("access_lines") or [],
        shares=overview.get("shares") or [],
        settings=overview.get("settings"),
    )


def share_enable_public_folder() -> dict:
    """启用公用文件夹共享（Users\\Public）。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(False, ["需要管理员权限"], suggestions=["请以管理员运行"])
    public = str(Path(os.environ.get("PUBLIC", r"C:\Users\Public")))
    code, text = _run_powershell(
        rf"""
$ErrorActionPreference='Continue'
$path = '{public.replace("'", "''")}'
$name = 'UsersPublic'
$exist = Get-SmbShare -Name $name -ErrorAction SilentlyContinue
if ($exist) {{
  'already: ' + $exist.Path
}} else {{
  New-SmbShare -Name $name -Path $path -FullAccess 'Everyone' -Description 'Public folder' -ErrorAction Stop
  'created'
}}
# NTFS 给 Everyone 读取（不强制改写过严 ACL）
icacls $path /grant '*S-1-1-0:(OI)(CI)(RX)' /T /C 2>&1 | Select-Object -First 3
""",
        timeout=60,
    )
    for ln in (text or "").splitlines():
        if ln.strip():
            steps.append(ln.strip()[:160])
    host = os.environ.get("COMPUTERNAME", ".")
    suggestions = [
        f"公用目录已共享为 \\\\{host}\\UsersPublic",
        f"路径: {public}",
    ]
    overview = share_get_settings()
    return _network_result(
        code == 0 or "already" in (text or "") or "created" in (text or ""),
        steps,
        suggestions=suggestions,
        shares=overview.get("shares") or [],
        access_lines=overview.get("access_lines") or [],
    )


def share_list() -> dict:
    """刷新本机共享列表。"""
    return share_get_settings()


def share_create(path: str, name: str = "", access: str = "full") -> dict:
    """创建文件夹共享，并同步授予共享权限 + NTFS 权限。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(False, ["需要管理员权限"], suggestions=["请以管理员运行"])

    target = Path(str(path or "").strip().strip('"'))
    if not target.exists() or not target.is_dir():
        return _network_result(False, [f"路径无效或不是文件夹: {path}"])

    share_name = (name or target.name or "Share").strip()
    # SMB 共享名限制
    share_name = "".join(c for c in share_name if c not in '\\/:*?"<>|')[:80]
    if not share_name:
        share_name = "Share"

    access_key = (access or "full").lower()
    if access_key in ("read", "r", "读取"):
        smb_right, ntfs = "Read", "(OI)(CI)(RX)"
    elif access_key in ("change", "modify", "更改", "修改"):
        smb_right, ntfs = "Change", "(OI)(CI)(M)"
    else:
        smb_right, ntfs = "Full", "(OI)(CI)(F)"

    path_lit = str(target).replace("'", "''")
    name_lit = share_name.replace("'", "''")
    ps = rf"""
$ErrorActionPreference='Stop'
$path = '{path_lit}'
$name = '{name_lit}'
$right = '{smb_right}'
$exist = Get-SmbShare -Name $name -ErrorAction SilentlyContinue
if ($exist) {{
  if ($exist.Path -ne $path) {{
    throw "共享名已存在且指向其它路径: $($exist.Path)"
  }}
  'exists'
}} else {{
  if ($right -eq 'Read') {{
    New-SmbShare -Name $name -Path $path -ReadAccess 'Everyone' -Description '畅通匣'
  }} elseif ($right -eq 'Change') {{
    New-SmbShare -Name $name -Path $path -ChangeAccess 'Everyone' -Description '畅通匣'
  }} else {{
    New-SmbShare -Name $name -Path $path -FullAccess 'Everyone' -Description '畅通匣'
  }}
  'created'
}}
# 确保 Everyone 共享权限
Revoke-SmbShareAccess -Name $name -AccountName 'Everyone' -Force -ErrorAction SilentlyContinue | Out-Null
if ($right -eq 'Read') {{
  Grant-SmbShareAccess -Name $name -AccountName 'Everyone' -AccessRight Read -Force | Out-Null
}} elseif ($right -eq 'Change') {{
  Grant-SmbShareAccess -Name $name -AccountName 'Everyone' -AccessRight Change -Force | Out-Null
}} else {{
  Grant-SmbShareAccess -Name $name -AccountName 'Everyone' -AccessRight Full -Force | Out-Null
}}
# NTFS
icacls $path /grant "*S-1-1-0:{ntfs}" /T /C 2>&1 | Select-Object -First 5
Get-SmbShare -Name $name | Select-Object Name,Path | ConvertTo-Json -Compress
"""
    code, text = _run_powershell(ps, timeout=90)
    for ln in (text or "").splitlines():
        if ln.strip():
            steps.append(ln.strip()[:200])

    overview = share_get_settings()
    host = overview.get("computer") or os.environ.get("COMPUTERNAME", ".")
    ips = overview.get("ips") or []
    unc = f"\\\\{host}\\{share_name}"
    ok = code == 0 or "created" in (text or "") or "exists" in (text or "")
    suggestions = [
        f"共享已就绪: {unc}",
        "局域网访问也可用: " + " / ".join(f"\\\\{ip}\\{share_name}" for ip in ips[:3]),
        "若对方连不上，先对本机点「一键开启本机共享」",
    ]
    return _network_result(
        ok,
        steps,
        suggestions=suggestions,
        share_name=share_name,
        unc=unc,
        shares=overview.get("shares") or [],
        access_lines=overview.get("access_lines") or [],
        computer=host,
        ips=ips,
    )


def share_remove(name: str) -> dict:
    """删除指定共享（不删除磁盘文件）。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(False, ["需要管理员权限"], suggestions=["请以管理员运行"])
    share_name = str(name or "").strip()
    if not share_name:
        return _network_result(False, ["请指定共享名"])
    if share_name.upper() in ("IPC$", "ADMIN$", "C$", "D$", "E$", "F$", "G$", "H$"):
        return _network_result(False, [f"系统共享不可删除: {share_name}"])

    name_lit = share_name.replace("'", "''")
    code, text = _run_powershell(
        rf"""
$ErrorActionPreference='Continue'
Remove-SmbShare -Name '{name_lit}' -Force -ErrorAction Stop
'removed'
""",
        timeout=40,
    )
    steps.append((text or str(code))[:200])
    overview = share_get_settings()
    return _network_result(
        code == 0 or "removed" in (text or ""),
        steps,
        suggestions=[f"已删除共享 {share_name}（文件夹本身未删）"],
        shares=overview.get("shares") or [],
        access_lines=overview.get("access_lines") or [],
    )


def share_open_path(target: str = "") -> dict:
    """用资源管理器打开 \\\\电脑 或指定 UNC/本地路径。"""
    steps: list[str] = []
    host = os.environ.get("COMPUTERNAME", ".")
    path = (target or f"\\\\{host}").strip()
    code, text = _run_hidden(["explorer.exe", path], timeout=15)
    steps.append(f"已打开: {path}")
    if code != 0 and text:
        steps.append(text[:120])
    return _network_result(True, steps, suggestions=["若空白，请确认本机已开启共享并存在共享文件夹"])


def share_open_advanced_panel() -> dict:
    """打开系统「高级共享设置」面板。"""
    steps: list[str] = []
    # 控制面板经典页最稳
    code, text = _run_hidden(
        ["control.exe", "/name", "Microsoft.NetworkAndSharingCenter"],
        timeout=15,
    )
    steps.append("已打开「网络和共享中心」（可点左侧更改高级共享设置）")
    _run_hidden(
        ["cmd", "/c", "start", "", "ms-settings:network-advancedsettings"],
        timeout=10,
    )
    steps.append("同时尝试打开 Windows 设置 · 高级网络设置")
    if text:
        steps.append(text[:80])
    return _network_result(code == 0 or True, steps)


def share_map_drive(unc: str, letter: str = "") -> dict:
    """映射网络驱动器（持久）。"""
    steps: list[str] = []
    unc_path = str(unc or "").strip().strip('"')
    if not unc_path.startswith("\\\\"):
        return _network_result(False, ["UNC 路径格式应为 \\\\IP\\共享名"])
    drive = (letter or "*").strip().rstrip(":").upper()
    if drive != "*" and (len(drive) != 1 or not drive.isalpha()):
        return _network_result(False, [f"盘符无效: {letter}"])
    # net use Z: \\server\share /persistent:yes
    args = ["net", "use"]
    if drive == "*":
        args += ["*", unc_path, "/persistent:yes"]
    else:
        args += [f"{drive}:", unc_path, "/persistent:yes"]
    code, text = _run_hidden(args, timeout=45)
    steps.append((text or str(code))[:300])
    ok = code == 0
    return _network_result(
        ok,
        steps,
        suggestions=["映射成功后可在「此电脑」看到网络驱动器"] if ok else ["映射失败：检查路径、账号或先清理凭据"],
    )


def share_full_repair() -> dict:
    """共享疑难全面修复（按常见根因顺序执行）。"""
    steps: list[str] = []
    if not _is_admin():
        return _network_result(False, ["需要管理员权限"], suggestions=["请以管理员运行"])

    # 子步骤内部已实时刷过的行，外层 _absorb 不再重复刷
    def _absorb(title: str, fn) -> None:
        _step(steps, title)
        result = fn() if callable(fn) else fn
        for s in result.get("steps") or []:
            if s:
                steps.append(str(s))

    _absorb("=== 1/6 设为专用网络 ===", share_fix_private_profile)
    _absorb("=== 2/6 网络发现服务 ===", share_fix_discovery)
    _absorb("=== 3/6 防火墙放行 ===", share_fix_firewall)
    _absorb("=== 4/6 SMB 服务重启 ===", share_restart_smb_services)
    _absorb("=== 5/6 Win11/NAS 来宾与签名 ===", share_fix_win11_nas)
    _absorb("=== 6/6 清理过期凭据 ===", share_clear_credentials)

    _step(steps, "=== 修复后诊断 ===")
    _progress("正在诊断共享状态…")
    diag = share_diagnose()
    for s in (diag.get("steps") or [])[:18]:
        if s:
            steps.append(str(s))
            _progress(str(s))

    suggestions = list(diag.get("suggestions") or [])
    suggestions.insert(0, "全面修复已完成；请用 \\\\对方IP\\共享名 测试（比电脑名更稳）")
    _progress("共享全面修复完成")
    return _network_result(
        True,
        steps,
        suggestions=suggestions,
        share_info=diag.get("share_info"),
    )


# GUI (pywebview + shadcn/ui 前端)
# ---------------------------------------------------------------------------

def _decode_drop_path(item) -> str:
    if isinstance(item, bytes):
        for enc in ("utf-8", "gbk", "mbcs"):
            try:
                return item.decode(enc)
            except UnicodeDecodeError:
                continue
        return item.decode("utf-8", errors="replace")
    return str(item)


def _resource_dir() -> Path:
    if getattr(sys, "frozen", False) and hasattr(sys, "_MEIPASS"):
        return Path(sys._MEIPASS)  # type: ignore[attr-defined]
    return Path(__file__).resolve().parent


def _ui_index() -> Path:
    return _resource_dir() / "web" / "dist" / "index.html"


def _locker_dict(p: Locker) -> dict:
    return {
        "pid": p.pid,
        "name": p.name,
        "type_name": p.type_name,
        "reason": p.reason,
        "exe_path": p.exe_path,
        "skip": should_skip(p),
    }


class DesktopApi:
    def __init__(self, initial_path: str = ""):
        self.initial_path = initial_path
        self._window = None
        self._last_lockers: dict[int, Locker] = {}

    def bind_window(self, window) -> None:
        self._window = window

    def get_status(self) -> dict:
        return {"is_admin": _is_admin(), "initial_path": self.initial_path}

    def browse_folder(self) -> str | None:
        import webview

        if not self._window:
            return None
        result = self._window.create_file_dialog(webview.FOLDER_DIALOG)
        if result and len(result) > 0:
            return str(result[0])
        return None

    def browse_file(self) -> str | None:
        import webview

        if not self._window:
            return None
        result = self._window.create_file_dialog(
            webview.OPEN_DIALOG, allow_multiple=False
        )
        if result and len(result) > 0:
            return str(result[0])
        return None

    def take_dropped_paths(self, names=None) -> list[str]:
        """配合前端 drop：从 WebView2 FilesDropped 缓冲取出完整路径。"""
        import urllib.parse

        from webview.dom import _dnd_state

        names = names or []
        name_set = {urllib.parse.unquote(str(n)) for n in names}
        found: list[str] = []
        remain = []
        for item in list(_dnd_state.get("paths") or []):
            try:
                base, full = item[0], item[1]
            except Exception:
                continue
            base_u = urllib.parse.unquote(str(base))
            if (not name_set) or (base_u in name_set) or (str(base) in name_set):
                if full and full not in found:
                    found.append(str(full))
            else:
                remain.append(item)
        _dnd_state["paths"] = remain
        return found

    def scan(self, path: str) -> dict:
        try:
            path = (path or "").strip().strip('"')
            lockers, note = scan_target(path)
            self._last_lockers = {p.pid: p for p in lockers}
            display = str(Path(path).resolve()) if path and Path(path).exists() else path
            return {
                "ok": True,
                "path": display,
                "note": note,
                "is_admin": _is_admin(),
                "lockers": [_locker_dict(p) for p in lockers],
            }
        except Exception as e:
            return {
                "ok": False,
                "path": path,
                "note": "",
                "is_admin": _is_admin(),
                "lockers": [],
                "error": str(e),
            }

    def kill(self, pids) -> dict:
        try:
            pid_list = [int(x) for x in (pids or [])]
            targets: list[Locker] = []
            for pid in pid_list:
                if pid in self._last_lockers:
                    targets.append(self._last_lockers[pid])
                else:
                    targets.append(
                        Locker(
                            pid=pid,
                            name=_process_name(pid),
                            app_type=RmUnknownApp,
                            reason="手动选择",
                            exe_path=_process_image_path(pid),
                        )
                    )
            messages = do_kill(targets)
            return {"ok": True, "messages": messages}
        except Exception as e:
            return {"ok": False, "messages": [], "error": str(e)}

    def network_diagnose(self) -> dict:
        try:
            return network_diagnose()
        except Exception as e:
            return _network_result(False, [str(e)])

    def network_fix_wifi(self) -> dict:
        try:
            return network_fix_wifi()
        except Exception as e:
            return _network_result(False, [str(e)])

    def network_fix_driver(self) -> dict:
        try:
            return network_fix_wifi_driver()
        except Exception as e:
            return _network_result(False, [str(e)])

    def network_wait_usb(self) -> dict:
        try:
            return network_wait_usb_replug(timeout_sec=90.0)
        except Exception as e:
            return _network_result(False, [str(e)])

    def network_reset_stack(self) -> dict:
        try:
            return network_reset_stack()
        except Exception as e:
            return _network_result(False, [str(e)])

    def network_full_repair(self) -> dict:
        try:
            return network_full_repair()
        except Exception as e:
            return _network_result(False, [str(e)])

    def network_wifi_passwords(self) -> dict:
        """兼容旧前端：改为备份。"""
        try:
            return network_backup_wifi()
        except Exception as e:
            return _network_result(False, [str(e)], profiles=[])

    def network_backup_wifi(self) -> dict:
        try:
            return network_backup_wifi()
        except Exception as e:
            return _network_result(False, [str(e)], profiles=[])

    def network_restore_wifi(self) -> dict:
        try:
            return network_restore_wifi()
        except Exception as e:
            return _network_result(False, [str(e)], profiles=[])

    def share_diagnose(self) -> dict:
        try:
            return share_diagnose()
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_fix_win11_nas(self) -> dict:
        try:
            return share_fix_win11_nas()
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_fix_discovery(self) -> dict:
        try:
            return share_fix_discovery()
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_fix_firewall(self) -> dict:
        try:
            return share_fix_firewall()
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_fix_private_profile(self) -> dict:
        try:
            return share_fix_private_profile()
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_restart_smb(self) -> dict:
        try:
            return share_restart_smb_services()
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_clear_credentials(self) -> dict:
        try:
            return share_clear_credentials()
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_enable_smb1(self) -> dict:
        try:
            return share_enable_smb1()
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_disable_password_protected(self) -> dict:
        try:
            return share_disable_password_protected()
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_enable_password_protected(self) -> dict:
        try:
            return share_enable_password_protected()
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_get_settings(self) -> dict:
        try:
            return share_get_settings()
        except Exception as e:
            return _network_result(False, [str(e)], shares=[], ips=[], access_lines=[])

    def share_enable_hosting(self, close_password: bool = True) -> dict:
        try:
            return share_enable_hosting(bool(close_password))
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_enable_public_folder(self) -> dict:
        try:
            return share_enable_public_folder()
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_list(self) -> dict:
        try:
            return share_list()
        except Exception as e:
            return _network_result(False, [str(e)], shares=[])

    def share_create(self, path: str, name: str = "", access: str = "full") -> dict:
        try:
            return share_create(path or "", name or "", access or "full")
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_remove(self, name: str) -> dict:
        try:
            return share_remove(name or "")
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_open_path(self, target: str = "") -> dict:
        try:
            return share_open_path(target or "")
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_open_advanced_panel(self) -> dict:
        try:
            return share_open_advanced_panel()
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_map_drive(self, unc: str, letter: str = "") -> dict:
        try:
            return share_map_drive(unc or "", letter or "")
        except Exception as e:
            return _network_result(False, [str(e)])

    def share_full_repair(self) -> dict:
        try:
            return share_full_repair()
        except Exception as e:
            return _network_result(False, [str(e)])


_DROP_BRIDGE_JS = r"""
(function () {
  if (window.__unlockDropBridgeInstalled) return;
  window.__unlockDropBridgeInstalled = true;

  function prevent(e) {
    e.preventDefault();
    e.stopPropagation();
  }

  async function onDrop(e) {
    prevent(e);
    try {
      var files = e.dataTransfer && e.dataTransfer.files;
      if (!files || !files.length) return;

      // WebView2: 把 File 对象交给宿主，才能拿到真实路径
      if (window.chrome && chrome.webview && chrome.webview.postMessageWithAdditionalObjects) {
        chrome.webview.postMessageWithAdditionalObjects('FilesDropped', files);
      }

      var names = [];
      for (var i = 0; i < files.length; i++) names.push(files[i].name);

      // 等宿主处理 FilesDropped（异步）
      var paths = null;
      for (var t = 0; t < 10; t++) {
        await new Promise(function (r) { setTimeout(r, 40); });
        paths = await window.pywebview.api.take_dropped_paths(names);
        if (paths && paths.length && paths[0]) break;
      }
      if (paths && paths.length && paths[0]) {
        window.dispatchEvent(new CustomEvent('native-path', { detail: paths[0] }));
      } else {
        window.dispatchEvent(new CustomEvent('native-path-error', {
          detail: '未能解析拖入路径，请用「选文件夹 / 选文件」'
        }));
      }
    } catch (err) {
      window.dispatchEvent(new CustomEvent('native-path-error', {
        detail: String(err)
      }));
    }
  }

  document.addEventListener('dragenter', prevent, true);
  document.addEventListener('dragover', prevent, true);
  document.addEventListener('drop', onDrop, true);
})();
"""


def _install_drop_bridge(window) -> None:
    """启用 WebView2 拖放取路径：抬高 num_listeners + 注入前端桥接脚本。"""
    try:
        from webview.dom import _dnd_state

        _dnd_state["num_listeners"] = max(int(_dnd_state.get("num_listeners") or 0), 1)
    except Exception:
        pass
    try:
        window.evaluate_js(_DROP_BRIDGE_JS)
    except Exception:
        pass

    # WinForms 标题栏/边缘拖放兜底
    try:
        form = getattr(window, "native", None)
        if form is None:
            return
        from System.Windows.Forms import DataFormats, DragDropEffects

        def on_drag_enter(sender, e):
            if e.Data.GetDataPresent(DataFormats.FileDrop):
                e.Effect = DragDropEffects.Copy
            else:
                e.Effect = getattr(DragDropEffects, "None")

        def on_drag_drop(sender, e):
            try:
                files = list(e.Data.GetData(DataFormats.FileDrop) or [])
                if not files:
                    return
                path = str(files[0])
                import json

                payload = json.dumps(path, ensure_ascii=False)
                window.evaluate_js(
                    f"window.dispatchEvent(new CustomEvent('native-path', {{ detail: {payload} }}));"
                )
            except Exception:
                pass

        form.AllowDrop = True
        form.DragEnter += on_drag_enter
        form.DragDrop += on_drag_drop
    except Exception:
        pass


def run_gui(initial_path: str = "") -> int:
    import webview

    ui = _ui_index()
    if not ui.is_file():
        # 开发态：若未构建，提示
        msg = (
            f"未找到前端页面:\n{ui}\n\n"
            "请先在 web 目录执行: npm install && npm run build"
        )
        try:
            ctypes.windll.user32.MessageBoxW(0, msg, "畅通匣", 0x10)
        except Exception:
            print(msg)
        return 1

    api = DesktopApi(initial_path=initial_path)
    icon_path = _resource_dir() / "unlock_folder.ico"
    if not icon_path.is_file():
        icon_path = Path(__file__).resolve().parent / "unlock_folder.ico"
    window = webview.create_window(
        title="畅通匣",
        url=ui.as_uri(),
        js_api=api,
        width=980,
        height=820,
        min_size=(860, 700),
        background_color="#FAFAF9",
    )
    api.bind_window(window)

    def _inject_path(path: str) -> None:
        import json

        payload = json.dumps(path, ensure_ascii=False)
        try:
            window.evaluate_js(
                f"""
                (function() {{
                  var p = {payload};
                  window.dispatchEvent(new CustomEvent('native-path', {{ detail: p }}));
                }})();
                """
            )
        except Exception:
            pass

    def _set_native_icon() -> None:
        if not icon_path.is_file():
            return
        user32 = ctypes.windll.user32
        IMAGE_ICON = 1
        LR_LOADFROMFILE = 0x0010
        LR_DEFAULTSIZE = 0x0040
        WM_SETICON = 0x0080
        ICON_SMALL = 0
        ICON_BIG = 1
        hwnd = user32.FindWindowW(None, "畅通匣")
        if not hwnd:
            return
        hicon = user32.LoadImageW(
            None, str(icon_path), IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE
        )
        if hicon:
            user32.SendMessageW(hwnd, WM_SETICON, ICON_SMALL, hicon)
            user32.SendMessageW(hwnd, WM_SETICON, ICON_BIG, hicon)

    def _on_loaded():
        try:
            _install_drop_bridge(window)
        except Exception:
            pass

        def later():
            import time

            time.sleep(0.2)
            try:
                _set_native_icon()
            except Exception:
                pass
            # 再注入一次，防止被前端路由覆盖
            try:
                _install_drop_bridge(window)
            except Exception:
                pass

        import threading

        threading.Thread(target=later, daemon=True).start()

    window.events.loaded += _on_loaded
    webview.start()
    return 0


def main() -> int:
    argv = sys.argv[1:]
    if "--json" in argv or "--json-file" in argv:
        return _json_api_main(argv)
    if "--cli" in argv:
        argv = [a for a in argv if a != "--cli"]
        return _cli_main(argv)

    initial = ""
    if argv and not argv[0].startswith("-"):
        initial = argv[0]
    return run_gui(initial)


def _json_api_main(argv: list[str]) -> int:
    """供 USB-Fix-Kit 宿主调用：--json '{...}' 或 --json-file path"""
    import json

    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

    raw = ""
    if "--json-file" in argv:
        i = argv.index("--json-file")
        if i + 1 < len(argv):
            raw = Path(argv[i + 1]).read_text(encoding="utf-8-sig")
    elif "--json" in argv:
        i = argv.index("--json")
        if i + 1 < len(argv) and not argv[i + 1].startswith("-"):
            raw = argv[i + 1]
    if not raw:
        raw = sys.stdin.read()
    if not raw.strip():
        print(json.dumps({"ok": False, "error": "缺少 JSON 请求"}, ensure_ascii=False))
        return 2

    try:
        req = json.loads(raw)
    except json.JSONDecodeError as e:
        print(json.dumps({"ok": False, "error": f"JSON 无效: {e}"}, ensure_ascii=False))
        return 2

    cmd = str(req.get("cmd") or "").strip()
    try:
        api = DesktopApi()
        if cmd == "network_diagnose":
            result = api.network_diagnose()
        elif cmd == "network_fix_wifi":
            result = api.network_fix_wifi()
        elif cmd == "network_fix_driver":
            result = api.network_fix_driver()
        elif cmd == "network_wait_usb":
            result = api.network_wait_usb()
        elif cmd == "network_reset_stack":
            result = api.network_reset_stack()
        elif cmd == "network_full_repair":
            result = api.network_full_repair()
        elif cmd == "network_backup_wifi":
            result = api.network_backup_wifi()
        elif cmd == "network_restore_wifi":
            result = api.network_restore_wifi()
        elif cmd == "scan":
            result = api.scan(str(req.get("path") or ""))
        elif cmd == "kill":
            pids = req.get("pids") or []
            result = api.kill(pids)
        elif cmd == "share_diagnose":
            result = api.share_diagnose()
        elif cmd == "share_full_repair":
            result = api.share_full_repair()
        elif cmd == "share_enable_hosting":
            result = api.share_enable_hosting(bool(req.get("close_password", True)))
        elif cmd == "share_fix_win11_nas":
            result = api.share_fix_win11_nas()
        elif cmd == "share_get_settings":
            result = api.share_get_settings()
        elif cmd == "share_list":
            result = api.share_list()
        elif cmd == "get_status":
            result = api.get_status()
        else:
            result = {"ok": False, "error": f"未知命令: {cmd}"}
    except Exception as e:
        result = {"ok": False, "error": str(e)}

    print(json.dumps(result, ensure_ascii=False, default=str))
    return 0 if result.get("ok", True) is not False else 1


def _cli_main(argv: list[str]) -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

    p = argparse.ArgumentParser(description="畅通匣（命令行）")
    p.add_argument("path", nargs="?", help="路径")
    p.add_argument("-k", "--kill", action="store_true")
    p.add_argument("-y", "--yes", action="store_true")
    p.add_argument("--list-only", action="store_true")
    args = p.parse_args(argv)

    path_str = args.path or ""
    if not path_str:
        print("用法: 畅通匣.exe --cli \"路径\" [-k -y]")
        return 1

    lockers, note = scan_target(path_str)
    print(note)
    print_lockers(lockers)
    if not lockers or args.list_only:
        return 0
    if args.kill:
        if not args.yes:
            if input("确认结束？[y/N]: ").strip().lower() not in ("y", "yes", "是"):
                return 0
        for line in do_kill(lockers):
            print(line)
    return 0


def print_lockers(lockers: list[Locker]) -> None:
    if not lockers:
        print("未发现占用该路径的进程。")
        return
    for p in lockers:
        print(f"PID {p.pid}\t{p.name}\t{p.type_name}\t{p.reason}")


if __name__ == "__main__":
    import multiprocessing

    multiprocessing.freeze_support()
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
