from __future__ import annotations

import ctypes
import gzip
import http.client
import http.cookiejar
import json
import os
import re
import subprocess
import struct
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from contextlib import contextmanager
from ctypes import wintypes
from dataclasses import dataclass
from functools import wraps
from pathlib import Path
from typing import Any, Callable, Iterable, Iterator

import websocket

from .config_store import AppConfig


class _PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", ctypes.c_ulong),
        ("cntUsage", ctypes.c_ulong),
        ("th32ProcessID", ctypes.c_ulong),
        ("th32DefaultHeapID", ctypes.c_void_p),
        ("th32ModuleID", ctypes.c_ulong),
        ("cntThreads", ctypes.c_ulong),
        ("th32ParentProcessID", ctypes.c_ulong),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", ctypes.c_ulong),
        ("szExeFile", ctypes.c_wchar * 260),
    ]


class _POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class _MSG(ctypes.Structure):
    _fields_ = [
        ("hwnd", ctypes.c_void_p),
        ("message", ctypes.c_uint),
        ("wParam", ctypes.c_size_t),
        ("lParam", ctypes.c_ssize_t),
        ("time", ctypes.c_ulong),
        ("pt", _POINT),
        ("lPrivate", ctypes.c_ulong),
    ]


_KERNEL32 = ctypes.windll.kernel32
_KERNEL32.CreateToolhelp32Snapshot.argtypes = [ctypes.c_ulong, ctypes.c_ulong]
_KERNEL32.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
_KERNEL32.Process32FirstW.argtypes = [ctypes.c_void_p, ctypes.POINTER(_PROCESSENTRY32W)]
_KERNEL32.Process32FirstW.restype = ctypes.c_int
_KERNEL32.Process32NextW.argtypes = [ctypes.c_void_p, ctypes.POINTER(_PROCESSENTRY32W)]
_KERNEL32.Process32NextW.restype = ctypes.c_int
_KERNEL32.CloseHandle.argtypes = [ctypes.c_void_p]
_KERNEL32.CloseHandle.restype = ctypes.c_int
_KERNEL32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
_KERNEL32.OpenProcess.restype = wintypes.HANDLE
_KERNEL32.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
_KERNEL32.TerminateProcess.restype = wintypes.BOOL
_KERNEL32.GetExitCodeProcess.argtypes = [wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)]
_KERNEL32.GetExitCodeProcess.restype = wintypes.BOOL


_EXTERNAL_LAUNCH_LOCK = threading.RLock()


def _external_client_environment() -> dict[str, str]:
    env = dict(os.environ)
    for name in list(env):
        if name.startswith(('_PYI_', '_MEIPASS')) or name in ('ELECTRON_RUN_AS_NODE','ELECTRON_NO_ATTACH_CONSOLE'):
            env.pop(name, None)
    bundle = getattr(sys, '_MEIPASS', None)
    if bundle:
        root = os.path.normcase(os.path.abspath(bundle))
        def outside_bundle(value):
            path = os.path.normcase(os.path.abspath(value.strip('"')))
            return path != root and not path.startswith(root + os.sep)
        env['PATH'] = os.pathsep.join(part for part in env.get('PATH','').split(os.pathsep) if part and outside_bundle(part))
    return env


@contextmanager
def _external_client_dll_search():
    """Do not let Electron inherit the frozen Python application's DLL directory."""
    with _EXTERNAL_LAUNCH_LOCK:
        if not getattr(sys, 'frozen', False):
            yield
            return
        kernel32=ctypes.windll.kernel32
        kernel32.GetDllDirectoryW.argtypes=[wintypes.DWORD,wintypes.LPWSTR]
        kernel32.GetDllDirectoryW.restype=wintypes.DWORD
        kernel32.SetDllDirectoryW.argtypes=[wintypes.LPCWSTR]
        kernel32.SetDllDirectoryW.restype=wintypes.BOOL
        size=kernel32.GetDllDirectoryW(0,None)
        buffer=ctypes.create_unicode_buffer(size+1)
        kernel32.GetDllDirectoryW(len(buffer),buffer)
        previous=buffer.value
        if not kernel32.SetDllDirectoryW(None):
            raise BilibiliError('无法恢复客户端启动所需的系统DLL搜索路径')
        try:
            yield
        finally:
            kernel32.SetDllDirectoryW(previous or None)


class BilibiliError(RuntimeError):
    pass


class ChargeRequired(BilibiliError):
    """The official client confirms this account cannot play this charged video."""


class ClientRestartRequired(BilibiliError):
    pass


class LoginRequired(BilibiliError):
    pass


class ManualIntervention(BilibiliError):
    pass


class PreviewLookupError(BilibiliError):
    """Raised when a BV id / share link cannot be resolved into a previewable video."""


_BVID_RE = re.compile(r"(?i)\bBV[0-9A-Za-z]{6,}")
_AV_RE = re.compile(r"(?i)\bav(\d{1,12})\b")
_BV_NORMAL_RE = re.compile(r"^BV[0-9A-Za-z]+$")


def _extract_bvid(identifier: str) -> str:
    """Pull a canonical ``BVxxxxxxxxxx`` token out of any share text.

    Accepts bare ids (``BV1xx``), ``av170001`` ids, full URL
    (``https://www.bilibili.com/video/BV1xx?p=2``), shortened ``b23.tv`` style
    links, or just plaintext containing a BV token somewhere inside.

    NOTE: Bilibili BV ids are case-sensitive (Base58 alphabet mixing
    upper/lower). We only uppercase the leading ``BV`` prefix and preserve the
    rest verbatim — calling ``str.upper()`` on the whole token would silently
    remap to a different video.
    """
    text = (identifier or "").strip()
    if not text:
        return ""
    candidate = _BVID_RE.search(text)
    if candidate:
        token = candidate.group(0)
        if token[:2].upper() != "BV" or len(token) < 3:
            return ""
        if not _BV_NORMAL_RE.match(token[:2].upper() + token[2:]):
            return ""
        return token[:2].upper() + token[2:]
    av = _AV_RE.search(text)
    if av:
        return f"av{av.group(1)}"
    return ""


def _normalize_cover_url(value: str) -> str:
    text = (value or "").strip()
    if not text:
        return ""
    if text.startswith("//"):
        return "https:" + text
    if text.startswith("/"):
        return "https://www.bilibili.com" + text
    return text.replace("http://", "https://", 1) if text.lower().startswith("http://") else text


def _format_duration(seconds: int) -> str:
    total = max(0, int(seconds or 0))
    hours, remainder = divmod(total, 3600)
    minutes, secs = divmod(remainder, 60)
    if hours:
        return f"{hours}:{minutes:02d}:{secs:02d}"
    return f"{minutes}:{secs:02d}"


def _read_preview_response(response: Any) -> str:
    body = response.read()
    if body.startswith(b"\x1f\x8b"):
        body = gzip.decompress(body)
    return body.decode("utf-8")


def _fetch_preview_page(token: str, writer: Callable[[str], None]) -> dict[str, Any]:
    """Read public page metadata once when the metadata API rejects a request."""
    writer("封面预览：接口受限，尝试读取视频公开页面")
    request = urllib.request.Request(
        f"https://www.bilibili.com/video/{token}",
        headers={
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            "Referer": "https://www.bilibili.com/",
        },
    )
    try:
        opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar())
        )
        with opener.open(request, timeout=20) as response:
            page = _read_preview_response(response)
        match = re.search(r"(?:window\.)?__INITIAL_STATE__\s*=\s*", page)
        if not match:
            raise ValueError("页面未提供视频数据")
        state, _ = json.JSONDecoder().raw_decode(page[match.end():])
        data = state.get("videoData") if isinstance(state, dict) else None
        if not isinstance(data, dict):
            raise ValueError("页面未提供视频数据")
        matches = (str(data.get("bvid")) == token if token.startswith("BV")
                   else str(data.get("aid")) == str(int(token[2:])))
        if not matches or not data.get("title") or not data.get("pic"):
            raise ValueError("页面视频信息不完整或与输入不匹配")
        return {"code": 0, "data": data}
    except (urllib.error.URLError, TimeoutError, ValueError, OSError) as exc:
        writer(f"封面预览：公开页面读取失败：{exc}")
        raise PreviewLookupError(
            "B 站暂时限制了接口请求（412/风控），公开页面也未能取得封面。"
            "请稍后重试，或先在浏览器打开该视频检查是否需要登录或验证。"
        ) from exc


def fetch_preview(identifier: str, *, log: Callable[[str], None] | None = None) -> dict[str, Any]:
    """Look up a single video by BV/av/URL and return previewable metadata.

    Uses the public ``/x/web-interface/view`` endpoint which does **not**
    require login cookies; the caller may still get ``code != 0`` for region-
    locked or premium-only videos.
    """
    writer = log or (lambda msg: None)
    raw_input = (identifier or "").strip()
    token = _extract_bvid(identifier)
    writer(f"封面预览：原始输入={raw_input!r} 提取token={token!r}")
    if not token:
        raise PreviewLookupError("无法从输入中识别出 BV 号或 av 号")
    url = "https://api.bilibili.com/x/web-interface/view"
    # BV ids are case-sensitive (Base58 mixes upper/lower). ``token`` already
    # has only the ``BV`` prefix upper-cased (see ``_extract_bvid``); calling
    # ``token.upper()`` here would remap to a different video.
    if token[:2].upper() == "BV":
        query = f"bvid={urllib.parse.quote(token, safe='-_')}"
    else:
        query = f"aid={urllib.parse.quote(token[2:])}"
    request_url = f"{url}?{query}"
    writer(f"封面预览：请求 {request_url}")
    request = urllib.request.Request(
        request_url,
        headers={
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
            "Referer": "https://www.bilibili.com/",
            "Accept": "application/json,text/plain,*/*",
        },
    )
    last_exc: Exception | None = None
    payload: Any = None
    # B 站 API 偶发 12s 内的 timeout/URLError，retry 一次以提升体感
    for attempt in range(2):
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                payload = json.loads(_read_preview_response(response))
            break
        except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError, json.JSONDecodeError) as exc:
            last_exc = exc
            writer(f"封面预览：第 {attempt + 1} 次请求失败：{exc}")
            if isinstance(exc, urllib.error.HTTPError) and exc.code == 412:
                payload = _fetch_preview_page(token, writer)
                break
            if attempt == 0:
                time.sleep(0.6)
                continue
            raise PreviewLookupError(f"调用 B 站接口失败：{exc}") from exc
    if payload is None:
        # 防御：理论上不会到这里
        raise PreviewLookupError(f"调用 B 站接口失败：{last_exc}")
    writer(f"封面预览：API 返回 code={payload.get('code') if isinstance(payload, dict) else '?'} message={payload.get('message') if isinstance(payload, dict) else '?'!r}")
    code = int(payload.get("code", -1)) if isinstance(payload, dict) else -1
    if code in (-352, -412):
        payload = _fetch_preview_page(token, writer)
        code = 0
    data = payload.get("data") if isinstance(payload, dict) else None
    if code != 0 or not isinstance(data, dict):
        message = payload.get("message") if isinstance(payload, dict) else "未知错误"
        if code in (-101, -111):
            raise LoginRequired(f"该视频需要登录后才能访问：{message}")
        if code == -404:
            raise PreviewLookupError(f"找不到该视频（BV号错误或视频已被删除/审核中），请检查链接是否正确")
        if code in (-400, -403):
            raise PreviewLookupError(f"B 站拒绝请求：{message}")
        raise PreviewLookupError(f"B 站接口异常 code={code}：{message}")
    # BV 号是 Base58 编码，大小写敏感——绝不可整体 upper/lower。
    # 只保证 "BV" 前缀大写，后面字符保持 B 站返回的原样。
    raw_bvid = str(data.get("bvid") or token).strip()
    if len(raw_bvid) >= 2 and raw_bvid[:2].upper() == "BV":
        bvid = "BV" + raw_bvid[2:]
    else:
        bvid = raw_bvid
    aid = int(data.get("aid") or 0)
    cid = int(data.get("cid") or 0)
    pages = data.get("pages") or []
    page_count = max(1, len(pages))
    duration = int(data.get("duration") or 0)
    title = str(data.get("title") or "").strip()
    uploader = ""
    owner = data.get("owner") or {}
    if isinstance(owner, dict):
        uploader = str(owner.get("name") or "").strip()
    pic = _normalize_cover_url(str(data.get("pic") or ""))
    short_link = f"https://www.bilibili.com/video/{bvid}"
    writer(f"封面预览命中：bvid={bvid} title={title[:24]}")
    return {
        "stable_key": f"preview:{bvid}",
        "bvid": bvid,
        "aid": aid,
        "cid": cid,
        "biz_id": short_link,
        "title": title,
        "uploader": uploader,
        "duration_seconds": duration,
        "duration_text": _format_duration(duration),
        "part_count": page_count,
        "source_hint": pic,
        "cover": pic,
        "url": short_link,
    }


def normalize_preview_item(item: dict[str, Any]) -> dict[str, Any]:
    """Strip the renderer-only fields (``cover``/``url``) so the row can be INSERTed."""
    preview = dict(item or {})
    preview.pop("cover", None)
    preview.pop("url", None)
    preview.setdefault("source_hint", "")
    return preview


@dataclass(slots=True)
class CacheResult:
    actual_quality: str
    message: str


CHARGE_PERMISSION_JS = r"""
(wanted => {
  const states=Object.values(document.querySelector('#app')?.__vue_app__?.config?.globalProperties?.$pinia?.state?.value || {});
  for (const state of states) {
    const view=state?.videoData?.View || state?.View;
    if (!view) continue;
    const matches=(wanted.bvid && String(view.bvid || '').toLowerCase()===wanted.bvid.toLowerCase()) ||
      (wanted.aid && String(view.aid || '')===wanted.aid);
    if (!matches) continue;
    // A charge badge alone does not imply that the current account lacks access.
    if (view.is_upower_exclusive && (view.is_upower_play===false || view.is_upower_play===0)) return true;
    if (view.is_upower_exclusive && view.is_upower_play == null && states.some(s=>s?.isChargeRequired===true)) return true;
  }
  return false;
})
"""


DOWNLOAD_ITEMS_JS = r"""
(mode => [...document.querySelectorAll('.mine-download .tab-content.' + (mode === 'completed' ? 'downloaded' : 'downloading') + ' .download-item')].flatMap(card => {
  const clean = value => (value || '').replace(/\s+/g, ' ').trim();
  // DownloadItem receives either a video or a recursively nested itemList.
  // Read the same props used by the official renderer, without expanding or playing it.
  let instance = card.__vueParentComponent;
  let data = card.__vnode?.props?.item || null;
  for (let depth = 0; instance && depth < 12; depth++, instance = instance.parent) {
    if (instance.props?.item) { data = instance.props.item; break; }
  }
  // Folded collections often render through a fragment, leaving the card with
  // no __vueParentComponent. Locate its component from the mounted root vnode.
  if (!data) {
    const app = document.querySelector('#app')?.__vue_app__;
    const visited = new Set();
    const visitVNode = vnode => {
      if (!vnode || typeof vnode !== 'object' || visited.has(vnode) || data) return;
      visited.add(vnode);
      const component = vnode.component;
      if (component) {
        const el = vnode.el;
        const props = component.props || component.vnode?.props || {};
        const candidate = props.item || props.downloadItem || props.downloadInfo || props.data || props.info;
        if (el && (el === card || card.contains?.(el)) && candidate && typeof candidate === 'object') data = candidate;
        visitVNode(component.subTree);
      }
      if (Array.isArray(vnode.children)) vnode.children.forEach(visitVNode);
      else visitVNode(vnode.children);
      visitVNode(vnode.suspense?.activeBranch);
    };
    visitVNode(app?._container?._vnode);
  }
  if (data) {
    const leaves = [];
    const visit = (item, depth = 0, parent = {}) => {
      if (!item || depth > 30) throw new Error('合集结构不完整，保留原缓存状态');
      const children = [item.itemList, item.children, item.items, item.list, item.episodes, item.videoList]
        .find(value => Array.isArray(value));
      if (children) {
        if (!children.length) throw new Error('合集内容未加载，保留原缓存状态');
        children.forEach(child => visit(child, depth + 1, item));
        return;
      }
      const statuses = {completed:'已完成',downloading:'正在缓存',connecting:'正在缓存',pending:'已提交',paused:'已暂停',error:'失败',damaged:'失败',notAllowed:'不支持缓存'};
      const video = item.video || item.archive || item;
      let status = statuses[item.status || parent.status];
      const errorMessage = clean(item.errorMsg || parent.errorMsg);
      if (status !== '已完成' && /充电专属|需要充电|请.*充电|充电后.*观看/.test(errorMessage)) status = '需充电';
      if (!status || !video.title) throw new Error('视频状态无法识别，保留原缓存状态');
      const progress = Number(item.progress ?? parent.progress ?? (item.totalSize ? Number(item.loadedSize || 0) / Number(item.totalSize) * 100 : 0));
      leaves.push({title:clean(video.title),uploader:clean(video.uname || item.uname || parent.uname),bvid:String(video.bvid || item.bvid || ''),
        aid:String(video.aid || item.aid || ''),cid:String(video.cid || item.cid || ''),item_id:String(video.itemId || item.itemId || video.id || ''),
        duration_seconds:Math.round(Number(video.duration || item.duration) || 0),source_hint:clean(video.coverUrl || item.coverUrl),
        cache_size_bytes:Math.max(0,Number(item.totalSize ?? parent.totalSize ?? video.totalSize ?? 0) || 0),
        status,progress:status === '已完成' ? 100 : Math.max(0,Math.min(99,Math.round(progress || 0))),
        message:errorMessage || (status === '已完成' ? 'B站客户端已完成离线缓存' : status)});
    };
    visit(data);
    return leaves;
  }
  if (card.querySelector('.collection')) throw new Error('无法读取合集内视频，本次保留原缓存状态');
  const title = clean(card.querySelector('.info-title')?.textContent);
  const uploader = clean(card.querySelector('.up-name')?.textContent);
  const detail = clean(card.querySelector('.downloading-text')?.textContent);
  const control = clean(card.querySelector('.control-button')?.textContent);
  let progress = Number.parseFloat(card.querySelector('.progress-percent')?.style?.width || '') || 0;
  let cacheSizeBytes = 0;
  if (detail) {
    const sizes = detail.match(/([0-9.]+)\s*(KB|MB|GB)\s*\/\s*([0-9.]+)\s*(KB|MB|GB)/i);
    const scale = unit => ({KB:1,MB:1024,GB:1024*1024})[(unit||'').toUpperCase()] || 1;
    if (sizes && Number(sizes[3])) { cacheSizeBytes = Math.round(Number(sizes[3]) * scale(sizes[4]) * 1024); if (!progress) progress = Number(sizes[1]) * scale(sizes[2]) / (Number(sizes[3]) * scale(sizes[4])) * 100; }
    if (!progress) progress = Number.parseFloat(detail.match(/([0-9.]+)\s*%/)?.[1] || '') || 0;
  }
  let status = mode === 'completed' ? '已完成' : '正在缓存';
  if (mode !== 'completed') {
    if (/充电专属|需要充电|请.*充电|充电后.*观看/.test(detail)) status = '需充电';
    else if (/继续缓存|已暂停|暂停中/.test(control + detail)) status = '已暂停';
    else if (/失败|错误/.test(detail)) status = '失败';
    else if (/等待/.test(detail)) status = '已提交';
  }
  return {
    title,
    uploader,
    cache_size_bytes: cacheSizeBytes,
    status,
    progress: mode === 'completed' ? 100 : Math.max(0, Math.min(99, Math.round(progress))),
    message: mode === 'completed' ? 'B站客户端已完成离线缓存' : (detail || control || '已进入B站客户端缓存队列')
  };
}).filter(item => item.title))(MODE)
"""


def parse_duration(value: str) -> int:
    parts = [int(part) for part in value.strip().split(":") if part.isdigit()]
    if len(parts) == 2:
        return parts[0] * 60 + parts[1]
    if len(parts) == 3:
        return parts[0] * 3600 + parts[1] * 60 + parts[2]
    return 0


def _process_ids_by_name(name: str) -> list[int]:
    TH32CS_SNAPPROCESS = 0x00000002
    snapshot = _KERNEL32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snapshot in (None, ctypes.c_void_p(-1).value):
        return []
    found: list[int] = []
    entry = _PROCESSENTRY32W()
    entry.dwSize = ctypes.sizeof(entry)
    try:
        ok = _KERNEL32.Process32FirstW(snapshot, ctypes.byref(entry))
        while ok:
            if entry.szExeFile.casefold() == name.casefold():
                found.append(int(entry.th32ProcessID))
            ok = _KERNEL32.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        _KERNEL32.CloseHandle(snapshot)
    return found


def _show_windows_for_pids(pids: Iterable[int]) -> None:
    user32 = ctypes.windll.user32
    SW_RESTORE = 9
    handles = _main_window_handles_for_pids(pids) or _window_handles_for_pids(pids)
    for hwnd in handles:
        user32.ShowWindow(hwnd, SW_RESTORE)
        user32.SetForegroundWindow(hwnd)


def _center_window(hwnd: int) -> None:
    class MonitorInfo(ctypes.Structure):
        _fields_ = [("size", wintypes.DWORD), ("monitor", wintypes.RECT),
                    ("work", wintypes.RECT), ("flags", wintypes.DWORD)]

    user32 = ctypes.windll.user32
    user32.MonitorFromWindow.argtypes = [wintypes.HWND, wintypes.DWORD]
    user32.MonitorFromWindow.restype = wintypes.HANDLE
    user32.GetMonitorInfoW.argtypes = [wintypes.HANDLE, ctypes.POINTER(MonitorInfo)]
    user32.GetForegroundWindow.restype = wintypes.HWND
    monitor = user32.MonitorFromWindow(user32.GetForegroundWindow(), 2)
    info = MonitorInfo()
    info.size = ctypes.sizeof(info)
    if not user32.GetMonitorInfoW(monitor, ctypes.byref(info)):
        raise BilibiliError("无法获取当前显示器工作区")
    user32.ShowWindow(wintypes.HWND(hwnd), 9)
    rect = wintypes.RECT()
    user32.GetWindowRect(wintypes.HWND(hwnd), ctypes.byref(rect))
    work = info.work
    width = min(max(640, rect.right-rect.left), work.right-work.left)
    height = min(max(480, rect.bottom-rect.top), work.bottom-work.top)
    x = work.left + (work.right-work.left-width)//2
    y = work.top + (work.bottom-work.top-height)//2
    user32.SetWindowPos(wintypes.HWND(hwnd), None, x, y, width, height, 0x0040)
    user32.SetForegroundWindow(wintypes.HWND(hwnd))


def _window_handles_for_pids(pids: Iterable[int]) -> set[int]:
    wanted = {int(pid) for pid in pids}
    if not wanted:
        return set()
    user32 = ctypes.windll.user32
    callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    found: set[int] = set()

    @callback_type
    def enum_callback(hwnd: int, _lparam: int) -> bool:
        pid = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if (
            int(pid.value) in wanted
            and user32.GetWindowTextLengthW(hwnd) > 0
            and not user32.GetWindow(hwnd, 4)  # GW_OWNER: exclude IME/helper windows
        ):
            found.add(int(hwnd))
        return True

    user32.EnumWindows(enum_callback, 0)
    return found


def _window_title(hwnd: int) -> str:
    user32 = ctypes.windll.user32
    length = user32.GetWindowTextLengthW(hwnd)
    if length <= 0:
        return ""
    value = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, value, length + 1)
    return value.value


def _main_window_handles_for_pids(pids: Iterable[int]) -> set[int]:
    handles = _window_handles_for_pids(pids)
    return {
        hwnd
        for hwnd in handles
        if "bilibili" in _window_title(hwnd).casefold() or "哔哩哔哩" in _window_title(hwnd)
    }


def _minimize_windows_for_handles(hwnds: Iterable[int]) -> None:
    user32 = ctypes.windll.user32
    SW_SHOWMINNOACTIVE = 7
    for hwnd in {int(value) for value in hwnds}:
        if user32.IsWindow(hwnd) and not user32.IsIconic(hwnd):
            user32.ShowWindow(hwnd, SW_SHOWMINNOACTIVE)


def _minimize_windows_for_pids(pids: Iterable[int]) -> None:
    _minimize_windows_for_handles(_window_handles_for_pids(pids))


def _suppress_windows_for_handles(hwnds: Iterable[int]) -> None:
    """Keep one pixel on-screen so Electron renders without exposing the window."""
    user32 = ctypes.windll.user32
    HWND_BOTTOM = 1
    SW_SHOWNOACTIVATE = 4
    SWP_NOSIZE = 0x0001
    SWP_NOACTIVATE = 0x0010
    SWP_NOOWNERZORDER = 0x0200
    SWP_NOSENDCHANGING = 0x0400
    flags = SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING
    for hwnd in {int(value) for value in hwnds}:
        if user32.IsWindow(hwnd):
            rect = wintypes.RECT()
            if user32.GetWindowRect(hwnd, ctypes.byref(rect)):
                width = max(100, int(rect.right - rect.left))
            else:
                width = 1_600
            user32.SetWindowPos(hwnd, HWND_BOTTOM, 1 - width, 0, 0, 0, flags)
            user32.ShowWindow(hwnd, SW_SHOWNOACTIVATE)


def _has_visible_windows_for_pids(pids: Iterable[int]) -> bool:
    wanted = {int(pid) for pid in pids}
    if not wanted:
        return False
    user32 = ctypes.windll.user32
    callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    found = ctypes.c_bool(False)

    @callback_type
    def enum_callback(hwnd: int, _lparam: int) -> bool:
        pid = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if (
            int(pid.value) in wanted
            and user32.IsWindowVisible(hwnd)
            and user32.GetWindowTextLengthW(hwnd) > 0
        ):
            found.value = True
            return False
        return True

    user32.EnumWindows(enum_callback, 0)
    return bool(found.value)


def _terminate_processes(pids: Iterable[int]) -> None:
    kernel32 = ctypes.windll.kernel32
    PROCESS_TERMINATE = 0x1001
    for pid in pids:
        handle = kernel32.OpenProcess(PROCESS_TERMINATE, False, int(pid))
        if handle:
            try:
                code=wintypes.DWORD()
                if kernel32.GetExitCodeProcess(handle,ctypes.byref(code)) and code.value!=259:
                    continue
                if not kernel32.TerminateProcess(handle, 0):
                    if kernel32.GetExitCodeProcess(handle,ctypes.byref(code)) and code.value!=259:
                        continue
                    raise BilibiliError(f'无法关闭B站客户端进程 PID={pid}，请正常退出客户端后重试')
            finally:
                kernel32.CloseHandle(handle)


class CdpPage:
    def __init__(self, target: dict[str, Any]) -> None:
        self.target_id = str(target.get("id", ""))
        self.title = str(target.get("title", ""))
        self.url = str(target.get("url", ""))
        ws_url = str(target.get("webSocketDebuggerUrl", ""))
        if not ws_url:
            raise BilibiliError("页面没有可用的调试连接")
        self._socket = websocket.create_connection(
            ws_url,
            # A renderer-side fetch can legitimately take a little longer than
            # the old 10 second receive timeout.  Treating that as a disconnected
            # client produced false "客户端连接超时" errors for search and refresh.
            timeout=20,
            suppress_origin=True,
            http_proxy_host=None,
            http_proxy_port=None,
        )
        self._counter = 0
        self._lock = threading.Lock()

    def command(self, method: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
        try:
            with self._lock:
                self._counter += 1
                request_id = self._counter
                self._socket.send(json.dumps({"id": request_id, "method": method, "params": params or {}}))
                deadline = time.time() + 20
                while time.time() < deadline:
                    message = json.loads(self._socket.recv())
                    if message.get("id") != request_id:
                        continue
                    if "error" in message:
                        raise BilibiliError(f"客户端调试命令失败：{message['error'].get('message', message['error'])}")
                    return dict(message.get("result", {}))
        except (websocket.WebSocketException, OSError, TimeoutError) as exc:
            self.close()
            raise BilibiliError("B站客户端连接超时或已断开") from exc
        raise BilibiliError("客户端调试命令超时")

    def evaluate(self, expression: str) -> Any:
        result = self.command(
            "Runtime.evaluate",
            {"expression": expression, "returnByValue": True, "awaitPromise": True, "userGesture": True},
        )
        if result.get("exceptionDetails"):
            details = result["exceptionDetails"]
            exception = details.get("exception") or {}
            message = (
                exception.get("description")
                or exception.get("value")
                or details.get("text")
                or str(details)
            )
            line = details.get("lineNumber")
            suffix = f"（页面脚本第 {int(line) + 1} 行）" if isinstance(line, int) else ""
            raise BilibiliError(f"页面脚本执行失败：{message}{suffix}")
        remote = result.get("result", {})
        if "value" in remote:
            return remote["value"]
        return None

    def body_text(self) -> str:
        return str(self.evaluate("document.body ? document.body.innerText : ''") or "")

    def dispatch_click(self, x: float, y: float) -> None:
        for event_type, buttons in (("mouseMoved", 0), ("mousePressed", 1), ("mouseReleased", 0)):
            params: dict[str, Any] = {
                "type": event_type,
                "x": float(x),
                "y": float(y),
                "button": "left",
                "buttons": buttons,
                "clickCount": 1,
            }
            self.command("Input.dispatchMouseEvent", params)

    def close_window(self) -> None:
        try:
            # Page.close destroys Electron's webContents without running the
            # client's window registry cleanup. Later download-list reads then
            # fail in getScopeWindowId with "Object has been destroyed".
            queued = self.evaluate("""(()=>{
              if(typeof window.biliBridgePc?.callNative!=='function')return false;
              document.querySelectorAll('video,audio').forEach(media=>media.pause());
              setTimeout(()=>window.biliBridgePc.callNative('window/close').catch(()=>{}),50);
              return true;
            })()""")
            if not queued:
                raise BilibiliError("播放窗口没有官方关闭接口，已保留窗口以免破坏缓存状态")
        finally:
            self.close()

    def close(self) -> None:
        try:
            self._socket.close()
        except Exception:
            pass


CLICK_TEXT_JS = r"""
(text => {
  const clean = value => (value || '').replace(/\s+/g, ' ').trim();
  const visible = el => {
    const r = el.getBoundingClientRect();
    const s = getComputedStyle(el);
    return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden';
  };
  const nodes = [...document.querySelectorAll('body *')]
    .filter(el => visible(el) && clean(el.textContent) === text)
    .sort((a,b) => a.children.length - b.children.length);
  if (!nodes.length) return false;
  const el = nodes[0];
  el.scrollIntoView({block:'center'});
  el.click();
  return true;
})
"""


EXTRACT_CARDS_JS = r"""
(() => {
  const visible = el => {
    const r = el.getBoundingClientRect();
    const s = getComputedStyle(el);
    return r.width > 20 && r.height > 10 && s.display !== 'none' && s.visibility !== 'hidden';
  };
  const durationRe = /^(?:\d{1,2}:)?\d{1,2}:\d{2}$/;
  const clean = value => (value || '').replace(/\s+/g, ' ').trim();
  const cards = [...document.querySelectorAll('.wl-item, [class*="watch-later"] [class*="item"]')].filter(visible);
  const seen = new Set();
  const result = [];
  for (const card of cards) {
    const lines = (card.innerText || '').split(/\n+/).map(clean).filter(Boolean);
    const durationIndex = lines.findIndex(line => durationRe.test(line));
    const duration = durationIndex >= 0 ? lines[durationIndex] : '';
    if (!duration) continue;
    const titleNode = card.querySelector('[class*="title"]');
    const uploaderNode = card.querySelector('[class*="up"], [class*="author"], [class*="owner"], [class*="user"]');
    const title = clean(titleNode?.textContent || lines[durationIndex + 1] || '');
    if (!title || title === '稍后再看') continue;
    const uploader = clean(uploaderNode?.textContent || lines[durationIndex + 2] || '');
    const image = card.querySelector('img')?.src || '';
    const bvidMatch = (card.outerHTML || '').match(/BV[0-9A-Za-z]+/i);
    const bvid = bvidMatch ? bvidMatch[0] : '';
    const key = bvid || `${title}|${uploader}|${duration}`;
    if (seen.has(key)) continue;
    seen.add(key);
    result.push({stable_key:key, bvid, title, uploader, duration_text:duration, source_hint:image, part_count:1});
  }
  return result;
})()
"""


EXTRACT_QUEUE_API_JS = r"""
(async()=>{
  const readAccount=()=>{const states=document.querySelector('#app')?.__vue_app__?.config?.globalProperties?.$pinia?.state?.value||{};for(const state of Object.values(states)){const u=state?.userInfo;if(u&&typeof u.isLogin==='boolean')return u.isLogin?String(u.mid||u.uid||''):'guest';}return '';};
  const account=readAccount();let last;
  for(let attempt=0;attempt<2;attempt++){
    const controller=new AbortController();const timer=setTimeout(()=>controller.abort(),6000);
    try{
      const r=await fetch('https://api.bilibili.com/x/v2/history/toview',{credentials:'include',signal:controller.signal});
      if(!r.ok)throw Error('稍后再看接口 HTTP '+r.status);
      const payload=await r.json();if(account&&account!==readAccount())throw Error('WATCH_ACCOUNT_CHANGED');
      if(payload.code===-101)throw Error('WATCH_LOGIN_REQUIRED');
      if(payload.code!==0||!Array.isArray(payload.data?.list))throw Error(payload.message||'稍后再看返回结构无效');
      if(Number(payload.data.count)>payload.data.list.length)throw Error('稍后再看返回不完整，保留原列表');
      return payload;
    }catch(error){last=error;if(/WATCH_|HTTP 4/.test(String(error)))throw error;}
    finally{clearTimeout(timer);}
    if(attempt===0)await new Promise(resolve=>setTimeout(resolve,300));
  }
  throw Error('稍后再看接口读取失败（已限定超时并重试，原列表保留）：'+String(last?.message||last));
})()
  .then(payload => {
    if (payload.code !== 0) throw new Error(payload.message || `API ${payload.code}`);
    const formatDuration = value => {
      const seconds = Math.max(0, Number(value) || 0);
      const h = Math.floor(seconds / 3600);
      const m = Math.floor((seconds % 3600) / 60);
      const s = seconds % 60;
      return h ? `${h}:${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')}` : `${m}:${String(s).padStart(2,'0')}`;
    };
    return (payload.data?.list || []).map((video, index) => ({
      stable_key: video.bvid || String(video.aid),
      queue_index: index + 1,
      aid: video.aid || 0,
      bvid: video.bvid || '',
      cid: video.cid || video.page?.cid || 0,
      biz_id: ((video.uri || '').match(/playpage\/(\d+)/) || [])[1] || '',
      title: video.title || '',
      uploader: video.owner?.name || '',
      duration_text: formatDuration(video.duration),
      duration_seconds: Number(video.duration) || 0,
      source_hint: video.pic || '',
      part_count: Number(video.videos || video.count) || 1,
      progress_seconds: Number(video.progress) || 0,
      added_at: Number(video.add_at) || 0,
      view_count: Number(video.stat?.view) || 0,
      danmaku_count: Number(video.stat?.danmaku) || 0
    }));
  })
"""


SCROLL_JS = r"""
(() => {
  const preferred = document.querySelector('.app_mine--content');
  const candidates = [preferred, document.scrollingElement, ...document.querySelectorAll('main, section, div')]
    .filter(Boolean)
    .filter(el => el.scrollHeight > el.clientHeight + 100);
  candidates.sort((a,b) => (a===preferred?-1:0) - (b===preferred?-1:0) || (b.scrollHeight-b.clientHeight) - (a.scrollHeight-a.clientHeight));
  const el = preferred || candidates[0] || document.scrollingElement;
  const before = el.scrollTop;
  el.scrollTop = Math.min(el.scrollHeight, el.scrollTop + Math.max(500, el.clientHeight * 0.85));
  return {before, after:el.scrollTop, max:el.scrollHeight-el.clientHeight};
})()
"""


def serialized_client_operation(method):
    @wraps(method)
    def call(self, *args, **kwargs):
        with self.operation_lock:
            return method(self, *args, **kwargs)
    return call


class BilibiliClient:
    def __init__(self, config: AppConfig, log: Callable[[str], None] | None = None) -> None:
        self.config = config
        self.log = log or (lambda _message: None)
        self.process: subprocess.Popen[bytes] | None = None
        self.main_page: CdpPage | None = None
        self.background_mode = True
        self._background_applied = False
        self._background_guard_stop = threading.Event()
        self._background_guard_thread: threading.Thread | None = None
        self._background_guard_depth = 0
        self._background_baseline_hwnds: set[int] = set()
        self._background_automatic_hwnds: set[int] = set()
        self._background_guard_pids: set[int] = set()
        self._startup_active = False
        self._startup_foreground = 0
        self._startup_noactivate_hwnds: set[int] = set()
        self._startup_style_lock = threading.RLock()
        self._background_guard_ready = threading.Event()
        self.operation_lock = threading.RLock()
        self.expected_cache_count = 0
        self.manual_intervention = threading.Event()
        self.player_centered = False

    @property
    def endpoint(self) -> str:
        return f"http://127.0.0.1:{self.config.cdp_port}"

    def _json(self, path: str, timeout: float = 1.5) -> Any:
        # CDP is a local plain-HTTP service. Building urllib's default opener
        # also initializes unrelated HTTPS/certificate handlers in frozen apps.
        connection=http.client.HTTPConnection('127.0.0.1',self.config.cdp_port,timeout=timeout)
        try:
            connection.request('GET',path)
            response=connection.getresponse()
            if response.status!=200:
                raise OSError(f'本机客户端读取服务返回 HTTP {response.status}')
            return json.loads(response.read().decode('utf-8'))
        finally:
            connection.close()

    def _debug_endpoint_ready(self) -> bool:
        try:
            data=self._json("/json/version", 0.8)
            if not isinstance(data,dict) or not data.get('webSocketDebuggerUrl'):
                self._last_endpoint_error='端口未返回有效的客户端调试服务'
                return False
            self._last_endpoint_error=''
            return True
        except (OSError, ValueError, urllib.error.URLError, http.client.HTTPException) as exc:
            self._last_endpoint_error=str(exc)[:240]
            return False

    def _target_infos(self) -> list[dict[str, Any]]:
        try:
            return [item for item in self._json("/json/list", 2.0) if item.get("type") in {"page", "webview"}]
        except Exception as exc:
            raise BilibiliError(f"无法读取B站客户端页面列表：{exc}") from exc

    @serialized_client_operation
    def ensure_connected(self) -> None:
        if self._debug_endpoint_ready():
            self._apply_initial_background()
            self._resolve_main_page()
            return
        existing = _process_ids_by_name(Path(self.config.bilibili_exe).name)
        if existing:
            raise ClientRestartRequired(
                "B站客户端已由你手动打开，但未开放读取端口。确认后可由本软件安全重启客户端并继续使用。"
            )
        self._launch_client()
        self._resolve_main_page()

    def _client_pids(self) -> list[int]:
        return _process_ids_by_name(Path(self.config.bilibili_exe).name)

    def _apply_initial_background(self) -> None:
        if self.background_mode and not self._background_applied:
            _minimize_windows_for_pids(self._client_pids())
            self._background_applied = True

    def _minimize_automatic_windows(self) -> None:
        if not self.background_mode:
            return
        pids = self._background_guard_pids or set(self._client_pids())
        current = _window_handles_for_pids(pids)
        if self._startup_active:
            self._protect_startup_windows(current)
        main_windows = _main_window_handles_for_pids(pids)
        new_main_windows = main_windows - self._background_baseline_hwnds
        if new_main_windows:
            _minimize_windows_for_handles(new_main_windows)
            self._background_baseline_hwnds.update(new_main_windows)
        self._background_automatic_hwnds.update(current - self._background_baseline_hwnds)
        _suppress_windows_for_handles(self._background_automatic_hwnds)

    def _protect_startup_windows(self, handles: Iterable[int]) -> None:
        """Temporarily prevent activation while Electron creates/restores its windows."""
        with self._startup_style_lock:
            if self._startup_active:
                self._protect_startup_windows_locked(handles)

    def _protect_startup_windows_locked(self, handles: Iterable[int]) -> None:
        user32 = ctypes.windll.user32
        user32.GetForegroundWindow.restype = wintypes.HWND
        foreground = user32.GetForegroundWindow()
        handles = {int(handle) for handle in handles}
        if foreground:
            foreground_pid = ctypes.c_ulong()
            user32.GetWindowThreadProcessId(wintypes.HWND(foreground), ctypes.byref(foreground_pid))
            if int(foreground_pid.value) not in self._background_guard_pids:
                self._startup_foreground = int(foreground)
        for handle in handles:
            hwnd = wintypes.HWND(handle)
            style = user32.GetWindowLongW(hwnd, -20)
            if not style & 0x08000000:  # WS_EX_NOACTIVATE
                user32.SetWindowLongW(hwnd, -20, style | 0x08000000)
                self._startup_noactivate_hwnds.add(handle)
        _minimize_windows_for_handles(handles)
        if foreground in handles and user32.GetForegroundWindow() in handles and self._startup_foreground and user32.IsWindow(wintypes.HWND(self._startup_foreground)):
            user32.SetForegroundWindow(wintypes.HWND(self._startup_foreground))

    def _restore_startup_window_styles(self) -> None:
        with self._startup_style_lock:
            self._restore_startup_window_styles_locked()

    def _restore_startup_window_styles_locked(self) -> None:
        user32 = ctypes.windll.user32
        for handle in self._startup_noactivate_hwnds:
            hwnd = wintypes.HWND(handle)
            if user32.IsWindow(hwnd):
                style = user32.GetWindowLongW(hwnd, -20)
                user32.SetWindowLongW(hwnd, -20, style & ~0x08000000)
        self._startup_noactivate_hwnds.clear()

    def _background_guard_loop(self) -> None:
        user32 = ctypes.windll.user32
        EVENT_SYSTEM_FOREGROUND = 0x0003
        EVENT_OBJECT_SHOW = 0x8002
        WINEVENT_OUTOFCONTEXT = 0x0000
        PM_REMOVE = 0x0001
        callback_type = ctypes.WINFUNCTYPE(
            None,
            ctypes.c_void_p,
            ctypes.c_ulong,
            ctypes.c_void_p,
            ctypes.c_long,
            ctypes.c_long,
            ctypes.c_ulong,
            ctypes.c_ulong,
        )

        @callback_type
        def window_event(
            _hook: int,
            _event: int,
            hwnd: int,
            object_id: int,
            _child_id: int,
            _thread_id: int,
            _event_time: int,
        ) -> None:
            if not self.background_mode or not hwnd or object_id != 0:
                return
            pid = ctypes.c_ulong()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            handle = int(hwnd)
            if self._startup_active:
                # The first SHOW event may precede the next polling PID refresh.
                if int(pid.value) not in self._background_guard_pids:
                    self._background_guard_pids = set(self._client_pids())
                if int(pid.value) in self._background_guard_pids:
                    self._protect_startup_windows({handle})
                    return
            if (
                int(pid.value) in self._background_guard_pids
                and handle not in self._background_baseline_hwnds
                and not user32.GetWindow(hwnd, 4)
                and user32.GetWindowTextLengthW(hwnd) > 0
            ):
                title = _window_title(handle)
                if "bilibili" in title.casefold() or "哔哩哔哩" in title:
                    self._background_baseline_hwnds.add(handle)
                    _minimize_windows_for_handles({handle})
                else:
                    self._background_automatic_hwnds.add(handle)
                    _suppress_windows_for_handles({handle})

        user32.SetWinEventHook.restype = ctypes.c_void_p
        show_hook = user32.SetWinEventHook(
            EVENT_OBJECT_SHOW,
            EVENT_OBJECT_SHOW,
            None,
            window_event,
            0,
            0,
            WINEVENT_OUTOFCONTEXT,
        )
        foreground_hook = user32.SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            None,
            window_event,
            0,
            0,
            WINEVENT_OUTOFCONTEXT,
        )
        last_pid_refresh = 0.0
        message = _MSG()
        self._background_guard_ready.set()
        try:
            while not self._background_guard_stop.wait(0.002):
                while user32.PeekMessageW(ctypes.byref(message), None, 0, 0, PM_REMOVE):
                    user32.TranslateMessage(ctypes.byref(message))
                    user32.DispatchMessageW(ctypes.byref(message))
                now = time.monotonic()
                if now - last_pid_refresh >= 0.08 or not self._background_guard_pids:
                    self._background_guard_pids = set(self._client_pids())
                    last_pid_refresh = now
                self._minimize_automatic_windows()
        finally:
            if show_hook:
                user32.UnhookWinEvent(show_hook)
            if foreground_hook:
                user32.UnhookWinEvent(foreground_hook)

    @contextmanager
    def background_operation(self) -> Iterator[None]:
        """Minimize only windows created by automation; never fight the user's main window."""
        if not self.background_mode:
            yield
            return
        self._background_guard_depth += 1
        if self._background_guard_depth == 1:
            self._background_guard_stop.clear()
            self._background_guard_ready.clear()
            current = _window_handles_for_pids(self._client_pids())
            self._background_guard_pids = set(self._client_pids())
            self._background_baseline_hwnds = _main_window_handles_for_pids(self._client_pids())
            self._background_automatic_hwnds = current - self._background_baseline_hwnds
            _suppress_windows_for_handles(self._background_automatic_hwnds)
            self._background_guard_thread = threading.Thread(
                target=self._background_guard_loop,
                name="bilibili-window-guard",
                daemon=True,
            )
            self._background_guard_thread.start()
            self._background_guard_ready.wait(timeout=0.8)
        try:
            yield
        finally:
            self._background_guard_depth -= 1
            if self._background_guard_depth == 0:
                # Electron sometimes creates or restores a detail window shortly
                # after the final click, so watch only those automatic windows.
                for _ in range(20):
                    self._minimize_automatic_windows()
                    time.sleep(0.05)
                self._background_guard_stop.set()
                thread = self._background_guard_thread
                if thread and thread is not threading.current_thread():
                    thread.join(timeout=0.4)
                self._background_guard_thread = None
                self._background_baseline_hwnds.clear()
                self._background_automatic_hwnds.clear()
                self._background_guard_pids.clear()

    def _launch_client(self) -> None:
        if not self.background_mode:
            self._launch_client_process()
            if self.config.cache_directory:
                self.apply_cache_directory()
            return
        self._startup_active = True
        user32 = ctypes.windll.user32
        user32.GetForegroundWindow.restype = wintypes.HWND
        self._startup_foreground = int(user32.GetForegroundWindow() or 0)
        try:
            # Install the event hooks before starting Electron, and keep them
            # until its main renderer is ready and late show events settle.
            with self.background_operation():
                self._launch_client_process()
                self._resolve_main_page()
                if self.config.cache_directory:
                    self.apply_cache_directory()
                deadline = time.monotonic() + 3
                while time.monotonic() < deadline:
                    self._minimize_automatic_windows()
                    time.sleep(0.05)
        finally:
            self._startup_active = False
            self._restore_startup_window_styles()

    @serialized_client_operation
    def apply_cache_directory(self) -> str:
        from .client_directory import ClientFolderDialog
        from .config_store import normalize_cache_directory
        import ntpath

        page = self._resolve_main_page()
        def read_directory():
            result = page.evaluate("(async()=>await window.biliBridgePc.callNative('config/getDownloadInfo'))()")
            directory = (result or {}).get('data', {}).get('downloadPath', '')
            if not directory:
                raise BilibiliError('无法读取客户端缓存文件夹路径')
            self.cache_directory_current = directory
            return directory

        current = read_directory()
        desired = normalize_cache_directory(self.config.cache_directory)
        if not desired or ntpath.normcase(ntpath.normpath(current)) == ntpath.normcase(desired):
            return current
        try:
            Path(desired).mkdir(parents=True, exist_ok=True)
            dialog = ClientFolderDialog(self._client_pids())
            # Start without awaiting: the native call resolves only after the
            # Windows folder picker closes. No unsupported setter is guessed.
            page.evaluate("window.__managerCacheDirectory={done:false};window.biliBridgePc.callNative('config/setDownloadPath',{type:'video',toDefault:false}).then(()=>window.__managerCacheDirectory={done:true}).catch(e=>window.__managerCacheDirectory={done:true,error:String(e)});true")
            try:
                dialog.choose(desired)
                deadline = time.monotonic() + 8
                while time.monotonic() < deadline:
                    result = page.evaluate('window.__managerCacheDirectory') or {}
                    if result.get('error'):
                        raise RuntimeError(result['error'])
                    actual = read_directory()
                    if ntpath.normcase(ntpath.normpath(actual)) == ntpath.normcase(desired):
                        break
                    time.sleep(.15)
                else:
                    raise RuntimeError(f'客户端未确认目录选择，当前目录仍为 {actual}')
                self.log(f'客户端缓存目录已应用：{actual}')
                return actual
            finally:
                dialog.cancel()
        except (OSError, RuntimeError) as exc:
            raise BilibiliError(f'缓存目录应用失败，下次拉起时会重试：{exc}') from exc

    def _launch_client_process(self) -> None:
        exe = Path(self.config.bilibili_exe)
        if not exe.exists():
            raise BilibiliError(f"未找到B站客户端：{exe}")
        command = [
            str(exe),
            f"--remote-debugging-port={self.config.cdp_port}",
            "--remote-allow-origins=*",
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--start-minimized",
        ]
        self.log("正在启动B站客户端…")
        startupinfo = subprocess.STARTUPINFO()
        startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startupinfo.wShowWindow = 7
        with _external_client_dll_search():
            self.process = subprocess.Popen(
                command,
                cwd=str(exe.parent),
                startupinfo=startupinfo,
                creationflags=subprocess.CREATE_NO_WINDOW,
                env=_external_client_environment(),
            )
        self._background_applied=False
        self.log(f'客户端进程已创建：PID={self.process.pid}，读取端口={self.config.cdp_port}，打包运行={bool(getattr(sys,"frozen",False))}')
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            _minimize_windows_for_pids(self._client_pids())
            if self._debug_endpoint_ready():
                for _ in range(8):
                    _minimize_windows_for_pids(self._client_pids())
                    time.sleep(0.08)
                self._background_applied = True
                self.log(f'客户端读取端口已就绪：{self.config.cdp_port}')
                return
            exit_code=self.process.poll()
            if isinstance(exit_code,int) and (exit_code!=0 or not self._client_pids()):
                raise BilibiliError(f'B站客户端启动后退出：PID={self.process.pid}，退出码={exit_code} (0x{exit_code & 0xffffffff:08X})。请查看日志中的启动信息')
            time.sleep(0.06)
        pids=self._client_pids()
        self.log(f'客户端连接超时：启动PID={self.process.pid}，当前客户端PID={pids}，端口={self.config.cdp_port}，最后错误={getattr(self,"_last_endpoint_error","")}')
        raise BilibiliError(f'B站客户端读取端口 {self.config.cdp_port} 未就绪，已保留客户端进程。请正常退出客户端后重试，详见日志')

    def _resolve_main_page(self) -> CdpPage:
        if self.main_page:
            try:
                # The same shell hosts the home, dynamic and cache routes.  Once a
                # route changes its visible navigation text is no longer a reliable
                # way to tell that this is still the desktop client's main window.
                current_url = str(self.main_page.evaluate("location.href") or self.main_page.url)
                if "bilipc.bilibili.com/index.html" in current_url:
                    return self.main_page
            except Exception:
                self.main_page.close()
                self.main_page = None
        candidates: list[tuple[int, CdpPage]] = []
        # Electron can expose its debugging endpoint before it has created the
        # main webview.  Wait briefly rather than reporting a misleading main-page
        # error during normal client startup.
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline and not candidates:
            for info in self._target_infos():
                try:
                    page = CdpPage(info)
                    text = page.body_text()[:1_500]
                except Exception:
                    continue
                score = 0
                if "bilipc.bilibili.com/index.html" in page.url:
                    score += 20
                if "哔哩哔哩" in page.title:
                    score += 4
                if all(label in text for label in ("首页", "精选", "动态", "我的")):
                    score += 10
                if "稍后再看" in text and "离线缓存" in text:
                    score += 3
                candidates.append((score, page))
            if not candidates:
                time.sleep(0.25)
        if not candidates:
            raise BilibiliError("B站客户端未提供可读取页面，请先在客户端打开首页后重试；若仍无效，请正常退出客户端后由本程序重新启动")
        candidates.sort(key=lambda pair: pair[0], reverse=True)
        self.main_page = candidates[0][1]
        for _, page in candidates[1:]:
            page.close()
        return self.main_page

    @staticmethod
    def _click_text(page: CdpPage, text: str) -> None:
        clicked = page.evaluate(f"({CLICK_TEXT_JS})({json.dumps(text, ensure_ascii=False)})")
        if not clicked:
            raise BilibiliError(f"没有找到可点击的“{text}”控件；客户端页面结构可能已变化")

    def open_watch_later(self) -> CdpPage:
        self.ensure_connected()
        page = self._resolve_main_page()
        deadline = time.time() + 12
        text = ""
        while time.time() < deadline:
            text = page.body_text()
            if all(label in text for label in ("首页", "精选", "动态", "我的")):
                break
            time.sleep(0.25)
        if not all(label in text for label in ("首页", "精选", "动态", "我的")):
            raise BilibiliError("B站客户端主界面尚未加载完成，请稍后重试")
        if "稍后再看" not in text or "离线缓存" not in text:
            self._click_text(page, "我的")
            self._wait_for_text(page, "稍后再看")
        self._click_text(page, "稍后再看")
        time.sleep(max(0.9, self.config.action_delay_ms * 2 / 1000))
        for _ in range(5):
            text = page.body_text()
            if "哎呀！好像加载失败了" in text:
                if page.evaluate(f"({CLICK_TEXT_JS})('刷新页面')"):
                    time.sleep(1.2)
                    continue
            if re.search(r"\d{1,2}:\d{2}", text):
                return page
            time.sleep(0.8)
        if "登录" in text and "稍后再看" not in text:
            raise LoginRequired("B站登录状态已失效，请在客户端中重新登录")
        if "哎呀！好像加载失败了" in text:
            raise BilibiliError("稍后再看页面连续加载失败，请检查客户端网络后重试")
        return page

    def _wait_for_text(self, page: CdpPage, text: str) -> None:
        for _ in range(40):
            if text in page.body_text():
                return
            time.sleep(0.25)
        raise BilibiliError(f"客户端页面尚未加载完成：{text}，请重试")

    def refresh_client_page(self, page: CdpPage) -> None:
        # This is the official fixed Refresh button, verified in installed App assets.
        clicked = page.evaluate("""(()=>{
          const el=document.querySelector('[title="刷新页面"]');
          if(!el) return false;
          el.click(); return true;
        })()""")
        if not clicked:
            raise BilibiliError("没有找到B站客户端刷新按钮，本次未刷新，请打开客户端检查")
        self.log("已点击B站客户端“刷新页面”按钮，等待列表重新加载")
        time.sleep(2)

    @staticmethod
    def _watch_account(page: CdpPage) -> str:
        account = page.evaluate("""(()=>{const states=document.querySelector('#app')?.__vue_app__?.config?.globalProperties?.$pinia?.state?.value||{};for(const state of Object.values(states)){const u=state?.userInfo;if(u&&typeof u.isLogin==='boolean')return u.isLogin?String(u.mid||u.uid||''):'guest';}return '';})()""")
        if not isinstance(account, str) or not account or account == 'guest':
            raise LoginRequired('无法确认当前客户端账号，本次未移除稍后再看')
        return account

    def _read_watch_api(self, page: CdpPage) -> list[dict[str, object]]:
        try:
            result = page.evaluate(EXTRACT_QUEUE_API_JS)
        except BilibiliError as exc:
            if 'WATCH_LOGIN_REQUIRED' in str(exc):
                raise LoginRequired('B站登录已失效，请重新登录') from exc
            raise
        if not isinstance(result, list):
            raise BilibiliError('稍后再看接口未返回有效列表，已保留原数据')
        return result

    @serialized_client_operation
    def read_watch_later(self, refresh: bool = False) -> list[dict[str, object]]:
        self.ensure_connected()
        page = self._resolve_main_page()
        self.log("正在通过客户端会话读取稍后再看列表…")
        try:
            items = self._read_watch_api(page)
            self.log(f"已读取 {len(items)} 条稍后再看视频")
            return items
        except LoginRequired:
            raise
        except BilibiliError as exc:
            if 'WATCH_ACCOUNT_CHANGED' in str(exc):
                raise
            self.log(f"队列接口暂不可用，尝试客户端页面读取：{exc}")
            page = self.open_watch_later()

        merged: dict[str, dict[str, object]] = {}
        stable_rounds = 0
        for _ in range(80):
            current = page.evaluate(EXTRACT_CARDS_JS) or []
            before = len(merged)
            for item in current:
                merged[str(item.get("stable_key", ""))] = item
            scroll = page.evaluate(SCROLL_JS) or {}
            if len(merged) == before and scroll.get("after") == scroll.get("before"):
                stable_rounds += 1
            else:
                stable_rounds = 0
            if stable_rounds >= 4 or (scroll.get("after", 0) >= scroll.get("max", 1) and len(merged) == before):
                break
            time.sleep(0.15)
        page.evaluate(
            "[document.querySelector('.app_mine--content'),document.scrollingElement,...document.querySelectorAll('main,section,div')]"
            ".filter(Boolean).filter(el=>el.scrollHeight>el.clientHeight+100)"
            ".sort((a,b)=>(b.scrollHeight-b.clientHeight)-(a.scrollHeight-a.clientHeight))[0].scrollTop=0"
        )
        items = list(merged.values())
        for item in items:
            item["duration_seconds"] = parse_duration(str(item.get("duration_text", "")))
        if not items:
            raise BilibiliError("页面已经打开，但没有识别到视频卡片；客户端界面可能已更新")
        self.log(f"已读取 {len(items)} 条稍后再看视频")
        return items

    def _target_ids(self) -> set[str]:
        return {str(info.get("id", "")) for info in self._target_infos()}

    def _close_blank_player_pages(self) -> None:
        closed = 0
        for info in self._target_infos():
            url = str(info.get("url", ""))
            if "player.html" not in url or "isWindowNewOpen=true" not in url or "bvid=" in url:
                continue
            try:
                page = CdpPage(info)
                page.close_window()
                closed += 1
            except Exception:
                continue
        if closed:
            self.log(f"已清理 {closed} 个未完成初始化的空白播放窗口")
            time.sleep(0.35)

    def _post_click_to_main_window(self, x: float, y: float) -> set[int]:
        """Send a native click without activating or exposing a minimized main window."""
        user32 = ctypes.windll.user32
        HWND_BOTTOM = 1
        SW_SHOWNOACTIVATE = 4
        SWP_NOSIZE = 0x0001
        SWP_NOACTIVATE = 0x0010
        SWP_NOOWNERZORDER = 0x0200
        SWP_NOSENDCHANGING = 0x0400
        WM_MOUSEMOVE = 0x0200
        WM_LBUTTONDOWN = 0x0201
        WM_LBUTTONUP = 0x0202
        MK_LBUTTON = 0x0001
        flags = SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING
        parked: set[int] = set()
        px = max(0, min(65_535, int(round(x))))
        py = max(0, min(65_535, int(round(y))))
        lparam = (py << 16) | px
        handles = _main_window_handles_for_pids(self._client_pids())
        if not handles:
            raise BilibiliError("找不到B站客户端主窗口，无法点击视频卡片")
        for hwnd in handles:
            if user32.IsIconic(hwnd) or not user32.IsWindowVisible(hwnd):
                user32.SetWindowPos(hwnd, HWND_BOTTOM, -32_000, -32_000, 0, 0, flags)
                user32.ShowWindow(hwnd, SW_SHOWNOACTIVATE)
                parked.add(hwnd)
            user32.PostMessageW(hwnd, WM_MOUSEMOVE, 0, lparam)
            user32.PostMessageW(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lparam)
            user32.PostMessageW(hwnd, WM_LBUTTONUP, 0, lparam)
        return parked

    @staticmethod
    def _dispatch_cdp_click(page: CdpPage, x: float, y: float) -> None:
        """Click a Chromium element in viewport coordinates without foregrounding the client."""
        point = {"x": float(x), "y": float(y)}
        page.command("Input.dispatchMouseEvent", {"type": "mouseMoved", **point})
        page.command(
            "Input.dispatchMouseEvent",
            {"type": "mousePressed", "button": "left", "clickCount": 1, **point},
        )
        page.command(
            "Input.dispatchMouseEvent",
            {"type": "mouseReleased", "button": "left", "clickCount": 1, **point},
        )

    def _find_video_page(
        self,
        before_targets: dict[str, tuple[str, str]],
        title: str,
        bvid: str,
        timeout: float = 12,
    ) -> CdpPage | None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.background_mode:
                self._minimize_automatic_windows()
            infos = self._target_infos()
            infos.sort(key=lambda info: str(info.get("id", "")) not in before_targets, reverse=True)
            for info in infos:
                target_id = str(info.get("id", ""))
                if target_id == (self.main_page.target_id if self.main_page else ""):
                    continue
                url = str(info.get("url", ""))
                current_title = str(info.get("title", ""))
                previous = before_targets.get(target_id)
                changed = previous is None or previous != (url, current_title)
                exact_video = bool(bvid and bvid.casefold() in url.casefold() and "player.html" in url)
                player_candidate = bool("player.html" in url and changed)
                if not exact_video and not player_candidate and not changed:
                    continue
                try:
                    page = CdpPage(info)
                    body = page.body_text()[:3_000]
                except Exception:
                    continue
                if exact_video or player_candidate or (changed and title[:10] in (page.title + body)):
                    if self.background_mode:
                        self._minimize_automatic_windows()
                    return page
                page.close()
            time.sleep(0.15)
        return None

    def _wait_video_details(self, page: CdpPage, item: dict[str, object], attempts: int = 4) -> None:
        title = str(item.get("title", ""))
        bvid = str(item.get("bvid", ""))
        for attempt in range(1, attempts + 1):
            deadline = time.time() + 7
            state: dict[str, object] = {}
            while time.time() < deadline:
                if self.manual_intervention.is_set():
                    raise ManualIntervention("播放页已交给人工处理")
                self._check_charge_permission(page, item)
                state = page.evaluate(
                    f"""
                    (()=>{{
                      const body=document.body?.innerText||'';
                      const button=document.querySelector('.download');
                      return {{
                        title:document.title||'', url:location.href,
                        titleLoaded:body.includes({json.dumps(title[:20], ensure_ascii=False)}),
                        bvidLoaded:location.href.includes({json.dumps(bvid, ensure_ascii=False)}),
                        buttonFound:!!button, buttonDisabled:!!button?.classList.contains('disabled'),
                        failed:/加载失败|网络错误|刷新页面|出错了/.test(body)
                      }};
                    }})()
                    """
                ) or {}
                if (
                    state.get("bvidLoaded")
                    and state.get("buttonFound")
                    and (not state.get("buttonDisabled") or state.get("titleLoaded"))
                ):
                    return
                time.sleep(0.25)
            if attempt < attempts:
                self.log(f"视频详情加载未完成，正在刷新重试（{attempt}/{attempts - 1}）：{title}")
                page.command("Page.reload", {"ignoreCache": True})
                time.sleep(1.2 + attempt * 0.4)
        if state.get("buttonFound") and state.get("buttonDisabled") and state.get("titleLoaded"):
            raise BilibiliError("视频详情已加载，但B站客户端禁用了离线缓存按钮")
        raise BilibiliError(f"视频详情页多次刷新后仍未加载成功：{title}")

    @staticmethod
    def _check_charge_permission(page: CdpPage, item: dict[str, object]) -> None:
        wanted = {key: str(item.get(key) or '') for key in ('bvid', 'aid')}
        if page.evaluate(CHARGE_PERMISSION_JS + '(' + json.dumps(wanted) + ')') is True:
            raise ChargeRequired('充电专属视频：B站客户端确认当前账号无观看权限，请充电后重试')

    def _creator_client_export(self) -> tuple[str, str]:
        """Use the installed client's signed space request implementation."""
        archive = self._client_archive()
        with archive.open('rb') as stream:
            header = struct.unpack('<4I', stream.read(16))
            assets = json.loads(stream.read(header[3]))['files']['render']['files']['assets']['files']
            for name, entry in assets.items():
                if not name.startswith('space.') or not name.endswith('.js'): continue
                stream.seek(8 + header[1] + int(entry['offset']))
                source = stream.read(entry['size']).decode('utf-8')
                match = re.search(r'([\w$]+)=\w+=>\w+\.get\("//api.bilibili.com/x/space/arc/search"', source)
                exported = re.search(r'\b' + re.escape(match.group(1)) + r' as ([\w$]+)[,}]', source) if match else None
                if exported:
                    return 'https://bilipc.bilibili.com/assets/' + name, exported.group(1)
        raise BilibiliError('当前客户端版本未找到UP主投稿接口')

    def native_asset(self, marker: str, prefix: str = '') -> tuple[str, str]:
        """Locate the installed official implementation, including signed requests."""
        cached = getattr(self, '_native_asset_cache', {})
        archive = self._client_archive()
        key=(str(archive),prefix,marker)
        if key in cached:
            return cached[key]
        with archive.open('rb') as stream:
            header = struct.unpack('<4I', stream.read(16))
            assets = json.loads(stream.read(header[3]))['files']['render']['files']['assets']['files']
            for name, entry in assets.items():
                if not name.endswith('.js') or not name.startswith(prefix):
                    continue
                stream.seek(8 + header[1] + int(entry['offset']))
                source = stream.read(entry['size']).decode('utf-8')
                if marker in source:
                    result = ('https://bilipc.bilibili.com/assets/' + name, source)
                    cached[key] = result
                    self._native_asset_cache = cached
                    return result
        raise BilibiliError('当前客户端版本未找到对应页面接口，请更新适配')

    def _client_archive(self) -> Path:
        installed = Path(self.config.bilibili_exe).parent / 'resources' / 'app.asar'
        if not self.main_page:
            return installed
        scripts = self.main_page.evaluate("[...document.scripts].map(s=>s.src).filter(s=>/\\/assets\\/index\\.[\\w]+\\.js(?:\\?|$)/.test(s))")
        if not isinstance(scripts, list) or not scripts:
            return installed
        names = {Path(urllib.parse.urlsplit(str(url)).path).name for url in scripts}
        resources = Path(os.environ.get('APPDATA','')) / 'bilibili' / 'resource'
        for archive in [installed, *sorted(resources.glob('*.asar'), key=lambda p:p.stat().st_mtime, reverse=True)]:
            try:
                with archive.open('rb') as stream:
                    header=struct.unpack('<4I',stream.read(16))
                    assets=json.loads(stream.read(header[3]))['files']['render']['files']['assets']['files']
                if names.issubset(assets):
                    return archive
            except (OSError, ValueError, KeyError, struct.error):
                continue
        raise BilibiliError('没有找到与当前客户端页面匹配的资源包，请重启客户端后重试')

    def native_export(self, marker: str, prefix: str = '') -> tuple[str, str]:
        uri, source = self.native_asset(marker, prefix)
        position = source.index(marker)
        definitions = list(re.finditer(r'(?:function\s+([\w$]+)\s*\(|(?:const\s+|[,;])([\w$]+)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[\w$]+)\s*=>)', source[:position]))
        if definitions:
            symbol = definitions[-1].group(1) or definitions[-1].group(2)
            exports = ','.join(re.findall(r'\bexport\s*\{([^}]+)\}', source))
            exported = re.search(r'(?:^|,)\s*' + re.escape(symbol) + r' as ([\w$]+)(?:,|$)', exports)
            if exported:
                return uri, exported.group(1)
        raise BilibiliError('客户端页面接口导出结构已变化，请更新适配')

    def native_http(self) -> tuple[str, str]:
        uri, source = self.native_asset('/x/space/wbi/acc/info')
        target = re.search(r'return ([\w$]+)\.get\(',source)
        for imported, filename in re.findall(r'import\{([^}]+)\}from"([^"]+)"',source):
            for entry in imported.split(','):
                names=entry.strip().split(' as ')
                if target and names[-1]==target.group(1):
                    return uri.rsplit('/',1)[0]+'/'+filename.removeprefix('./'), names[0]
        raise BilibiliError('无法定位客户端请求模块')

    def resolve_uploader(self, bvid: str) -> dict:
        self.ensure_connected()
        page = self._resolve_main_page()
        result = page.evaluate(f'''(async()=>{{
          const response=await fetch('https://api.bilibili.com/x/web-interface/view?bvid='+encodeURIComponent({json.dumps(bvid)}),{{credentials:'include'}});
          const payload=await response.json();return {{code:payload.code,mid:payload.data?.owner?.mid,name:payload.data?.owner?.name}};
        }})()''') or {}
        if result.get('code') != 0 or not result.get('mid'):
            raise BilibiliError('无法确认UP主编号，请刷新后重试')
        return result

    def _feed_client_export(self, action: str) -> tuple[str, str]:
        """Resolve the installed client's own action export, not an external API."""
        archive = self._client_archive()
        with archive.open('rb') as stream:
            header = struct.unpack('<4I', stream.read(16))
            assets = json.loads(stream.read(header[3]))['files']['render']['files']['assets']['files']
            for name, entry in assets.items():
                if not name.endswith('.js') or not name.startswith('index.'):
                    continue
                stream.seek(8 + header[1] + int(entry['offset']))
                source = stream.read(entry['size']).decode('utf-8')
                if action == 'watch':
                    symbol = 'watchAdd' if '/x/v2/history/toview/add' in source else ''
                else:
                    match = re.search(r'([\w$]+)=\w+=>[\w$]+\("window/openPlayerWindow",\w+\)', source)
                    symbol = match.group(1) if match else ''
                exported = re.search(r'\b' + re.escape(symbol) + r' as ([\w$]+)[,}]', source) if symbol else None
                if exported:
                    # The shell document is served from https://bilipc..., so even
                    # a relative import resolves to the network URL.  Address the
                    # module inside the installed Electron asar explicitly instead.
                    # Electron accepts this file URI and keeps the action entirely
                    # inside the official desktop client.
                    asset_uri = Path(str(archive) + f'/render/assets/{name}').as_uri()
                    return asset_uri, exported.group(1)
        raise BilibiliError('当前客户端版本未找到对应操作，请更新管理器适配')

    @serialized_client_operation
    def add_feed_watch_later(self, item: dict[str, object]) -> None:
        self.ensure_connected()
        page = self._resolve_main_page()
        aid = int(item.get('aid') or 0)
        if aid <= 0:
            raise BilibiliError('该视频缺少编号，请刷新视频流后重试')
        # Importing the app's historical watch module a second time registers
        # custom elements again (for example bili-avatar) and fails before the
        # official request can run.  Use the same official endpoint with the
        # already logged-in renderer session instead; no credentials leave the
        # client and the CSRF token is never returned to Python.
        result = page.evaluate(f'''(async()=>{{
          const csrf=/(?:^|;\\s*)bili_jct=([^;]+)/.exec(document.cookie)?.[1]||'';
          if(!csrf) return {{code:-101,message:'客户端登录信息不可用'}};
          const response=await fetch('https://api.bilibili.com/x/v2/history/toview/add?csrf='+encodeURIComponent(csrf),{{
            method:'POST',credentials:'include',headers:{{'Content-Type':'application/x-www-form-urlencoded;charset=UTF-8'}},body:'aid={aid}'}});
          const payload=await response.json();
          return {{code:payload.code,message:payload.message}};
        }})()''')
        if not isinstance(result, dict) or result.get('code') != 0:
            raise BilibiliError(f'加入稍后再看失败：{(result or {}).get("message") or "客户端未确认成功"}')

    def open_feed_player(self, item: dict[str, object]) -> CdpPage:
        self.ensure_connected()
        page = self._resolve_main_page()
        bvid, title = str(item['bvid']), str(item['title'])
        before = {str(info.get('id', '')): (str(info.get('url', '')), str(info.get('title', ''))) for info in self._target_infos()}
        # The official cache control lives in a player window.  Keep that
        # short-lived implementation detail off-screen for direct cache actions.
        before_windows = _window_handles_for_pids(self._client_pids())
        # Use the already running renderer's bridge. The installed asar may be
        # older than the client's hot-updated renderer; importing its modules
        # again registers incompatible player handlers and leaves a blank page.
        params = json.dumps({'type': 'ugc', 'bvid': bvid, 'aid': item.get('aid') or 0,
                             'cid': item.get('cid') or 0, 'fromSpmid': 'main.home-page-recommend.0.0'}, ensure_ascii=False)
        page.evaluate(f'''(async()=>{{
          if(typeof window.biliBridgePc?.callNative!=='function')throw new Error('客户端原生接口尚未就绪');
          await window.biliBridgePc.callNative('window/openPlayerWindow',{params});return true;
        }})()''')
        player = self._find_video_page(before, title, bvid, timeout=10)
        if player is None:
            raise BilibiliError('客户端未打开对应视频播放页，请重试')
        new_windows = _window_handles_for_pids(self._client_pids()) - before_windows
        if new_windows:
            _suppress_windows_for_handles(new_windows)
        try:
            self._initialize_player(player, item)
            self._wait_video_details(player, item)
            return player
        except Exception:
            player.close_window()
            raise

    def _initialize_player(self, player: CdpPage, item: dict[str, object]) -> None:
        """Complete the official player bootstrap using video metadata only."""
        bvid = str(item.get('bvid') or '')
        current = str(player.evaluate('location.href') or '')
        query = urllib.parse.parse_qs(urllib.parse.urlsplit(current).query)
        if (query.get('bvid') == [bvid] and query.get('aid', ['0'])[0] != '0'
                and query.get('cid', ['0'])[0] != '0'):
            return
        aid, cid = int(item.get('aid') or 0), int(item.get('cid') or 0)
        if not aid or not cid:
            main = self._resolve_main_page()
            metadata = main.evaluate(f'''(async()=>{{
              const r=await fetch('https://api.bilibili.com/x/web-interface/view?bvid='+encodeURIComponent({json.dumps(bvid)}),
                {{credentials:'include',signal:AbortSignal.timeout(8000)}});
              const p=await r.json();return {{code:p.code,bvid:p.data?.bvid,aid:p.data?.aid,cid:p.data?.cid}};
            }})()''') or {}
            if metadata.get('code') != 0 or metadata.get('bvid') != bvid:
                raise BilibiliError('客户端未能确认视频编号，已停止打开缓存面板')
            aid, cid = int(metadata.get('aid') or 0), int(metadata.get('cid') or 0)
        if not aid or not cid:
            raise BilibiliError('视频缺少播放页初始化参数，请刷新后重试')
        url = 'https://bilipc.bilibili.com/player.html?' + urllib.parse.urlencode({
            'type': 'ugc', 'bvid': bvid, 'aid': aid, 'cid': cid, 'page': 1,
            'itemId': cid, 'cached': 'false', 'fromSpmid': 'main.mine-view-later.0.0.pv',
        })
        self.log(f'正在补全客户端播放页参数：{bvid}')
        player.command('Page.navigate', {'url': url})

    def open_video(self, item: dict[str, object]) -> CdpPage:
        if item.get('_feed_direct') or re.fullmatch(r'BV[0-9A-Za-z]{10}', str(item.get('bvid') or '')):
            return self.open_feed_player(item)
        page = self.open_watch_later()
        title = str(item.get("title", ""))
        bvid = str(item.get("bvid", ""))
        self._close_blank_player_pages()
        before_targets = {
            str(info.get("id", "")): (str(info.get("url", "")), str(info.get("title", "")))
            for info in self._target_infos()
        }
        search = json.dumps({"title": title, "bvid": bvid}, ensure_ascii=False)
        click_point: dict[str, object] | None = None
        for _ in range(100):
            click_point = page.evaluate(
                f"""
                    (query => {{
                      const clean=v=>(v||'').replace(/\\s+/g,' ').trim();
                      const allCards=()=>[...document.querySelectorAll('.wl-item, [class*="watch-later"] [class*="item"]')];
                      let bvidNorm=query.bvid||'';
                      let card = bvidNorm ? allCards().find(n=>{{
                        const html=(n.outerHTML||'').toLowerCase();
                        return html.includes(bvidNorm.toLowerCase());
                      }}) : null;
                      if (!card) card=allCards().find(n=>clean(n.textContent).toLowerCase().includes(query.title.toLowerCase().slice(0,48)));
                      if (!card) return null;
                      card.scrollIntoView({{block:'center'}});
                      const el=card.querySelector('.cover') || card.querySelector('.info-title') || card;
                      const rect=el.getBoundingClientRect();
                      return {{x:rect.left+rect.width/2,y:rect.top+rect.height/2}};
                    }})({search})
                    """
            )
            if click_point:
                break
            scroll = page.evaluate(SCROLL_JS) or {}
            if scroll.get("after") == scroll.get("before"):
                break
            time.sleep(0.1)
        if not click_point:
            raise BilibiliError(f"队列中找不到视频：{title}")
        parked_main: set[int] = set()
        video_page: CdpPage | None = None
        try:
            # CDP viewport coordinates match getBoundingClientRect exactly and do not
            # depend on Windows DPI or on where the hidden client window is parked.
            self._dispatch_cdp_click(page, float(click_point["x"]), float(click_point["y"]))
            video_page = self._find_video_page(before_targets, title, bvid, timeout=5.0)
            if video_page is None:
                self.log(f"首次点击未打开独立播放页，正在通过客户端页面重试：{title}")
                self._dispatch_cdp_click(page, float(click_point["x"]), float(click_point["y"]))
                video_page = self._find_video_page(before_targets, title, bvid, timeout=4.0)
            if video_page is None:
                self.log(f"页面点击仍未打开播放页，正在使用原生点击回退：{title}")
                parked_main.update(
                    self._post_click_to_main_window(float(click_point["x"]), float(click_point["y"]))
                )
                video_page = self._find_video_page(before_targets, title, bvid, timeout=6.0)
        finally:
            _minimize_windows_for_handles(parked_main)
        if video_page is None:
            raise BilibiliError(f"点击视频卡片后没有打开对应的独立播放页：{title}")
        try:
            aid = int(item.get("aid", 0) or 0)
            cid = int(item.get("cid", 0) or 0)
            if aid and cid and bvid and "cid=" not in video_page.url:
                biz_id = str(item.get("biz_id", "") or "")
                media_list = json.dumps(
                    {"bizType": 2, "bizId": biz_id},
                    ensure_ascii=False,
                    separators=(",", ":"),
                )
                player_url = "https://bilipc.bilibili.com/player.html?" + urllib.parse.urlencode(
                    {
                        "type": "ugc",
                        "fromSpmid": "main.mine-view-later.0.0.pv",
                        "aid": aid,
                        "cid": cid,
                        "bvid": bvid,
                        "page": 1,
                        "itemId": cid,
                        "bizType": 2,
                        "bizId": biz_id,
                        "fSpmid": "main.mine-view-later.0.0.pv",
                        "cached": "false",
                        "cmFromTrackId": "",
                        "uuid": int(item.get("queue_index", 1) or 1),
                        "mediaList": media_list,
                    }
                )
                video_page.command("Page.navigate", {"url": player_url})
                time.sleep(0.6)
            self._wait_video_details(video_page, item)
            return video_page
        except Exception:
            video_page.close_window()
            raise

    @staticmethod
    def _quality_candidates(target: str) -> list[str]:
        ladder = ["8K", "4K", "1080P高码率", "1080P", "720P", "480P", "360P"]
        if target == "最高可用":
            return ladder
        start = next((index for index, value in enumerate(ladder) if value == target.replace(" ", "")), 0)
        return ladder[start:]

    @serialized_client_operation
    def cache_video(self, item: dict[str, object], quality: str, auto_fallback: bool) -> CacheResult:
        page = self.open_video(item)
        try:
            if self.manual_intervention.is_set():
                raise ManualIntervention("播放页已交给人工处理")
            deadline = time.time() + 10
            cache_state: dict[str, object] = {}
            while time.time() < deadline:
                self._check_charge_permission(page, item)
                cache_state = page.evaluate(
                    "(()=>{const el=document.querySelector('.download');return {found:!!el,disabled:!!el?.classList.contains('disabled')}})()"
                ) or {}
                if cache_state.get("found") and not cache_state.get("disabled"):
                    break
                time.sleep(0.25)
            if not cache_state.get("found"):
                raise BilibiliError("视频页面没有找到官方缓存按钮")
            if cache_state.get("disabled"):
                raise BilibiliError("该视频当前不允许离线缓存，B站客户端已禁用缓存按钮")
            page.evaluate("document.querySelector('.download').click(); true")
            deadline = time.time() + 5
            while time.time() < deadline and not page.evaluate("!!document.querySelector('.app-download')"):
                time.sleep(0.15)
            if not page.evaluate("!!document.querySelector('.app-download')"):
                self._check_charge_permission(page, item)
                raise BilibiliError("点击缓存后没有打开官方离线缓存面板")

            candidates = self._quality_candidates(quality)
            if not auto_fallback and quality != "最高可用":
                candidates = candidates[:1]
            quality_result = page.evaluate(
                f"""
                (async (candidates) => {{
                  const panel=document.querySelector('.app-download');
                  const button=panel?.querySelector('.app-download--qn-btn');
                  const canonical=text=>{{
                    const value=(text||'').replace(/\\s+/g,'');
                    if(value.includes('1080P')&&value.includes('高码率')) return '1080P高码率';
                    const match=value.match(/8K|4K|1080P|720P|480P|360P/);
                    return match ? match[0] : '';
                  }};
                  const items=[...panel.querySelectorAll('.app-download--qn-item')];
                  const available=items.map(el=>canonical(el.textContent)).filter(Boolean);
                  for(const candidate of candidates){{
                    const item=items.find(el=>canonical(el.textContent)===candidate);
                    if(!item) continue;
                    button?.click();
                    item.click();
                    await new Promise(resolve=>setTimeout(resolve,180));
                    if(canonical(button?.textContent)===candidate) return {{selected:candidate,available}};
                  }}
                  return {{selected:'',available}};
                }})({json.dumps(candidates, ensure_ascii=False)})
                """
            ) or {}
            actual_quality = str(quality_result.get("selected", ""))
            if not actual_quality:
                raise BilibiliError(f"缓存面板不支持所选画质：{quality}")
            if self.manual_intervention.is_set():
                raise ManualIntervention("播放页已交给人工处理")
            started = bool(page.evaluate("(()=>{const el=document.querySelector('.app-download .app-download--btn-num');if(!el)return false;el.click();return true})()"))
            if not started:
                raise BilibiliError("已经打开缓存面板，但没有找到下载按钮")
            time.sleep(0.35)
            self._check_charge_permission(page, item)
            self._confirm_cache_submission(item)
            return CacheResult(actual_quality=actual_quality, message="已提交到B站客户端离线缓存")
        finally:
            # Direct cache uses a temporary player solely to access B站's own
            # cache panel.  It must not remain on screen after submission.
            if self.background_mode or item.get('_feed_direct'):
                page.close_window()
            else:
                page.close()

    def _confirm_cache_submission(self, item: dict[str, object]) -> None:
        main = self._resolve_main_page()
        result = main.evaluate(f'''(async()=>{{
          const wanted={json.dumps({k:str(item.get(k) or '') for k in ('bvid','aid','cid')})};
          const matches=t=>{{const keys=['bvid','aid','cid'].filter(k=>wanted[k]&&wanted[k]!=='0');return keys.length&&keys.every(k=>String(t[k]||'')===wanted[k])}};
          const flatten=rows=>rows.flatMap(t=>Array.isArray(t.itemList)?flatten(t.itemList):[t.video?{{...t,...t.video}}:t]);
          for(let n=0;n<16;n++){{
            for(const method of ['download/getTaskList','download/getCompletedGroups']){{
              try{{const r=await window.biliBridgePc.callNative(method);
                if(Array.isArray(r?.data)){{const found=flatten(r.data).find(matches);if(found)return {{confirmed:!['error','notAllowed','damaged'].includes(found.status),message:found.errorMsg||found.status}};}}
              }}catch(error){{}}
            }}
            await new Promise(resolve=>setTimeout(resolve,300));
          }}return {{confirmed:false}};
        }})()''') or {}
        if result.get('confirmed') is not True:
            raise BilibiliError('客户端尚未确认视频加入缓存，已保留稍后再看：'+str(result.get('message') or '任务列表中未找到视频'))

    def open_offline_cache(self) -> None:
        self.ensure_connected()
        page = self._resolve_main_page()
        text = page.body_text()
        if "离线缓存" not in text:
            self._click_text(page, "我的")
            self._wait_for_text(page, "离线缓存")
        self._click_text(page, "离线缓存")
        self._wait_for_text(page, "已缓存视频")

    def _find_offline_card(self, page: CdpPage, item: dict[str, object]) -> bool:
        """Locate an official cache card by stable identity before title fallback."""
        wanted = {key: str(item.get(key) or '') for key in ('bvid','aid','cid')}
        if not any(value and value != '0' for value in wanted.values()):
            return False
        return page.evaluate(f'''(wanted=>{{
          const keys=Object.keys(wanted).filter(k=>wanted[k]&&wanted[k]!=='0');
          const cards=[...document.querySelectorAll('.download-item')];
          cards.forEach(card=>delete card.dataset.cacheManagerTarget);
          const found=cards.filter(card=>{{
            const data=card.__vueParentComponent?.props?.item||card.__vnode?.props?.item||card.dataset;
            if(data.itemList||data.episodes)return false;
            const video=data.video||data;
            return keys.every(key=>String(video[key]||data[key]||'')===wanted[key]);
          }});
          if(found.length!==1)return false;
          found[0].dataset.cacheManagerTarget='1';found[0].scrollIntoView({{block:'center'}});return true;
        }})({json.dumps(wanted)})''') is True

    @staticmethod
    def _click_offline_card_menu(page: CdpPage) -> bool:
        return bool(
            page.evaluate(
                """
                (()=>{
                  const card=document.querySelector('.download-item[data-cache-manager-target="1"]');
                  if(!card) return false;
                  const visible=el=>{const r=el.getBoundingClientRect(),s=getComputedStyle(el);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'};
                  const preferred=card.querySelector('[class*="more"],[class*="More"],[class*="menu"],[class*="operate"]');
                  let target=preferred;
                  if(!target){
                    const cr=card.getBoundingClientRect();
                    const candidates=[...card.querySelectorAll('button,[role="button"],svg,i,span,div')]
                      .filter(visible)
                      .map(el=>({el,r:el.getBoundingClientRect(),name:(el.className?.baseVal||el.className||'').toString()}))
                      .filter(x=>x.r.left>cr.left+cr.width*.68 && x.r.top>cr.top+cr.height*.38 && x.r.width<=64 && x.r.height<=64)
                      .sort((a,b)=>{
                        const score=x=>(/more|menu|operate/i.test(x.name)?1000:0)+x.r.left+x.r.top;
                        return score(b)-score(a);
                      });
                    target=candidates[0]?.el;
                  }
                  if(!target) return false;
                  const clickable=target.closest('button,[role="button"],[class*="more"],[class*="menu"],[class*="operate"]')||target;
                  // Some Electron/Vue nodes shadow their own `click` member.
                  // Dispatch through EventTarget's native prototype instead.
                  const fire=type=>EventTarget.prototype.dispatchEvent.call(clickable,new MouseEvent(type,{bubbles:true,cancelable:true,view:window}));
                  ['pointerdown','mousedown','pointerup','mouseup','click'].forEach(fire);
                  return true;
                })()
                """
            )
        )

    @staticmethod
    def _click_offline_menu_action(page: CdpPage, action: str) -> bool:
        return bool(
            page.evaluate(
                f"""
                (action=>{{
                  const clean=value=>(value||'').replace(/\\s+/g,' ').trim();
                  const visible=el=>{{const r=el.getBoundingClientRect(),s=getComputedStyle(el);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'}};
                  const matches=value=>action==='delete' ? /^(?:删除(?:缓存|任务|视频|此视频)?|取消(?:缓存|下载|任务)?)$/.test(value) : value==='更新弹幕';
                  const nodes=[...document.querySelectorAll('body *')]
                    .filter(el=>visible(el)&&matches(clean(el.textContent)))
                    .sort((a,b)=>a.children.length-b.children.length);
                  if(!nodes.length) return false;
                  const target=nodes[0];
                  const fire=type=>EventTarget.prototype.dispatchEvent.call(target,new MouseEvent(type,{{bubbles:true,cancelable:true,view:window}}));
                  ['pointerdown','mousedown','pointerup','mouseup','click'].forEach(fire);
                  return true;
                }})({json.dumps(action)})
                """
            )
        )

    @serialized_client_operation
    def manage_offline_item(self, item: dict[str, object], action: str) -> None:
        """Use the official cache-card overflow menu for delete/danmaku actions."""
        if action not in {"delete", "danmaku"}:
            raise ValueError(action)
        wanted = {key: str(item.get(key) or '') for key in ('bvid','aid','cid','item_id')}
        if not any(value and value != '0' for value in wanted.values()):
            raise BilibiliError('缺少缓存条目的BV/aid/cid/itemId，本次未操作')
        self.ensure_connected()
        page = self._resolve_main_page()
        script = (Path(__file__).parent / 'web' / 'cache_actions.js').read_text(encoding='utf-8-sig')
        result = page.evaluate(f'({script})({json.dumps(wanted)}, {json.dumps(action)})') or {}
        if result.get('confirmed') is not True:
            raise BilibiliError('客户端未确认缓存操作成功，本次不按标题回退')
        if action == 'delete' and type(result.get('remainingCount')) is int:
            self.expected_cache_count = result['remainingCount']

    @serialized_client_operation
    def delete_watch_later_item(self, item: dict[str, object]) -> None:
        """Remove one item through the official watch-later card overflow menu."""
        aid = int(item.get('aid') or 0)
        if not aid and re.fullmatch(r'BV[0-9A-Za-z]{10}', str(item.get('bvid') or '')):
            self.ensure_connected()
            metadata = self._resolve_main_page().evaluate(f'''(async()=>{{
              const p=await fetch('https://api.bilibili.com/x/web-interface/view?bvid='+encodeURIComponent({json.dumps(item['bvid'])}),
                {{credentials:'include',signal:AbortSignal.timeout(8000)}}).then(r=>r.json());
              return {{code:p.code,bvid:p.data?.bvid,aid:p.data?.aid}};
            }})()''') or {}
            if metadata.get('code') != 0 or metadata.get('bvid') != item['bvid']:
                raise BilibiliError('无法核对稍后再看视频编号，本次未移除')
            aid = int(metadata.get('aid') or 0)
        if aid:
            self.ensure_connected()
            page = self._resolve_main_page()
            account = self._watch_account(page)
            posted = False
            last_error = ''
            for attempt in range(3):
                try:
                    # Always reconcile before sending/re-sending the idempotent
                    # aid deletion. An uncertain POST never authorizes blind retry.
                    items = self._read_watch_api(page)
                    if self._watch_account(page) != account:
                        raise BilibiliError('WATCH_ACCOUNT_CHANGED：账号已变化，本次停止移除')
                    if not any(str(row.get('aid')) == str(aid) for row in items):
                        return
                    posted = True
                    result = page.evaluate(f'''(async()=>{{
                      const readAccount=()=>{{const states=document.querySelector('#app')?.__vue_app__?.config?.globalProperties?.$pinia?.state?.value||{{}};for(const state of Object.values(states)){{const u=state?.userInfo;if(u&&typeof u.isLogin==='boolean')return u.isLogin?String(u.mid||u.uid||''):'guest';}}return '';}};
                      if(readAccount()!=={json.dumps(account)})throw Error('WATCH_ACCOUNT_CHANGED');
                      const csrf=/(?:^|;\\s*)bili_jct=([^;]+)/.exec(document.cookie)?.[1]||'';
                      if(!csrf)return {{code:-101,message:'客户端登录已失效'}};
                      const controller=new AbortController();const timer=setTimeout(()=>controller.abort(),6000);
                      try{{
                        const r=await fetch('https://api.bilibili.com/x/v2/history/toview/del',{{
                          method:'POST',credentials:'include',signal:controller.signal,
                          headers:{{'Content-Type':'application/x-www-form-urlencoded;charset=UTF-8'}},
                          body:new URLSearchParams({{aid:{json.dumps(str(aid))},csrf}}).toString()}});
                        const p=await r.json();return {{code:p.code,message:p.message}};
                      }}finally{{clearTimeout(timer)}}
                    }})()''') or {}
                    if result.get('code') == -101:
                        raise LoginRequired('客户端登录已失效，请重新登录')
                    if result.get('code') != 0:
                        raise BilibiliError('移除请求被客户端拒绝：'+str(result.get('message') or result))
                    # A separate bounded read keeps the total time of each CDP
                    # evaluation below its transport timeout.
                    verified = self._read_watch_api(page)
                    if self._watch_account(page) != account:
                        raise BilibiliError('WATCH_ACCOUNT_CHANGED：账号已变化，移除结果待核对')
                    if not any(str(row.get('aid')) == str(aid) for row in verified):
                        return
                    last_error = '客户端列表仍包含此视频'
                except LoginRequired:
                    raise
                except BilibiliError as exc:
                    if 'WATCH_ACCOUNT_CHANGED' in str(exc):
                        raise
                    if '移除请求被客户端拒绝' in str(exc):
                        raise
                    last_error = str(exc)
                if attempt < 2:
                    self.log(f'稍后再看移除结果待核对，正在重新读取（{attempt+1}/2）')
                    time.sleep(0.3 * (attempt + 1))
            qualifier = '移除结果待确认' if posted else '未提交移除'
            raise BilibiliError(f'{qualifier}，已保留本地状态，请同步稍后再看后重试：{last_error}')
        raise BilibiliError('缺少可核对的视频编号，本次未移出稍后再看')

    def _collect_offline_tab(self, page: CdpPage, tab: str, mode: str) -> list[dict[str, object]]:
        self._click_text(page, tab)
        css_mode = "downloaded" if mode == "completed" else "downloading"
        empty_text = "暂无缓存的视频" if mode == "completed" else "暂无缓存中的视频"
        ready = False
        for _ in range(40):
            ready = page.evaluate(f"""(()=>{{
              const el=document.querySelector('.mine-download .tab-content.{css_mode}');
              return !!el && (!!el.querySelector('.download-item') || el.innerText.includes({json.dumps(empty_text)}));
            }})()""")
            if ready:
                break
            time.sleep(0.25)
        if not ready:
            raise BilibiliError(f"{tab}未完成加载，本次保留原缓存状态")
        time.sleep(1)
        page.evaluate("(()=>{const el=document.querySelector('.app_mine--content');if(el)el.scrollTop=0})()")
        merged: dict[str, dict[str, object]] = {}
        stable = 0
        expression = DOWNLOAD_ITEMS_JS.replace("MODE", json.dumps(mode))
        for _ in range(100):
            current = page.evaluate(expression) or []
            before = len(merged)
            for item in current:
                key = str(item.get("item_id") or "|".join(str(item.get(field, "")) for field in ("bvid", "cid", "title", "uploader")))
                merged[key] = item
            scroll = page.evaluate(SCROLL_JS) or {}
            if len(merged) == before and scroll.get("after") == scroll.get("before"):
                stable += 1
            else:
                stable = 0
            if stable >= 3:
                break
            time.sleep(0.12)
        return list(merged.values())

    @serialized_client_operation
    def read_offline_cache(self, refresh: bool = False, allow_empty: bool = False) -> list[dict[str, object]]:
        self.open_offline_cache()
        page = self._resolve_main_page()
        if refresh:
            self.refresh_client_page(page)
        self.log("正在同步B站客户端真实缓存状态…")
        for attempt in range(3):
            completed = self._collect_offline_tab(page, "已缓存视频", "completed")
            downloading = self._collect_offline_tab(page, "正在缓存", "downloading")
            states = completed + downloading
            if states:
                break
            if attempt < 2:
                self.log(f"两个缓存列表同时为空，等待界面数据后重新读取（{attempt+1}/2）")
                time.sleep(0.25)
        if not states:
            # An older manager may already have destroyed a player webContents.
            # Detect that specific native failure rather than suggesting refresh,
            # which cannot repair Electron's stale window registry.
            health = page.evaluate("""(async()=>{
              if(typeof window.biliBridgePc?.callNative!=='function')return null;
              try{await window.biliBridgePc.callNative('download/getCompletedGroups');return null}
              catch(error){return String(error?.message||error)}
            })()""")
            if isinstance(health, str) and 'Object has been destroyed' in health:
                self._cache_window_invalid = True
                raise ClientRestartRequired("B站客户端缓存窗口引用已失效，刷新无法恢复。请安全重启客户端后重新同步；缓存文件和任务会保留")
        if not states and self.expected_cache_count and not allow_empty:
            raise BilibiliError("客户端已缓存和正在缓存同时为空，多次读取未恢复；已保留上次记录。请检查客户端缓存目录或手动打开缓存页面确认")
        self.expected_cache_count = len(states)
        self.log(
            f"已同步 {len(states)} 条客户端缓存记录："
            f"已缓存 {len(completed)} 条，正在缓存 {len(downloading)} 条"
        )
        return states

    @serialized_client_operation
    def set_offline_cache_paused(self, paused: bool) -> int:
        """Use the official client controls to pause or resume all cache tasks."""
        self.open_offline_cache()
        page = self._resolve_main_page()
        self._click_text(page, "正在缓存")
        time.sleep(0.45)
        wanted = ["全部暂停", "暂停全部", "暂停所有任务"] if paused else ["全部继续", "继续全部", "全部开始", "开始全部"]
        # Prefer the official bulk menu shown in the top-right of the cache page.
        page.evaluate("""(()=>{
          const node=[...document.querySelectorAll('.mine-download button,.mine-download span')]
            .find(el=>el.textContent.trim()==='更多操作');
          if(!node)return false;
          let el=node;
          for(let n=0;el&&n<4;n++,el=el.parentElement){
            el.dispatchEvent(new MouseEvent('mouseenter',{bubbles:false}));
            el.dispatchEvent(new MouseEvent('mouseover',{bubbles:true}));
          }
          return true;
        })()""")
        time.sleep(0.25)
        clicked = page.evaluate(
            f"""
            (()=>{{
              const clean=v=>(v||'').replace(/\\s+/g,' ').trim();
              const visible=el=>{{const r=el.getBoundingClientRect(),s=getComputedStyle(el);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'}};
              const wanted={json.dumps(wanted, ensure_ascii=False)};
              const node=[...document.querySelectorAll('button,[role="button"],li,div,span')]
                .filter(el=>visible(el)&&wanted.includes(clean(el.textContent)))
                .sort((a,b)=>a.children.length-b.children.length)[0];
              if(!node) return 0;
              node.click();
              return -1;
            }})()
            """
        )
        if clicked == -1:
            time.sleep(0.45)
            return -1
        # Client versions without a bulk action expose one control per card.
        action_pattern = r"暂停" if paused else r"继续|开始"
        clicked = int(
            page.evaluate(
                f"""
                (()=>{{
                  const visible=el=>{{const r=el.getBoundingClientRect(),s=getComputedStyle(el);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'}};
                  const pattern=/{action_pattern}/;
                  const controls=[...document.querySelectorAll('.download-item .control-button')]
                    .filter(el=>visible(el)&&pattern.test((el.textContent||'')+' '+(el.getAttribute('aria-label')||'')+' '+(el.getAttribute('title')||'')));
                  controls.forEach(el=>el.click());
                  return controls.length;
                }})()
                """
            )
            or 0
        )
        if clicked <= 0:
            raise BilibiliError("B站客户端正在缓存页面没有找到官方暂停/继续控件")
        time.sleep(0.45)
        return clicked

    @serialized_client_operation
    def set_offline_items_paused(self, items: list[dict], paused: bool) -> int:
        self.ensure_connected()
        page = self._resolve_main_page()
        result = page.evaluate(f'''(async()=>{{
          const wanted={json.dumps([{k:str(row.get(k) or '') for k in ('bvid','aid','cid')} for row in items])};
          const bridge=window.biliBridgePc;
          const tasks=(await bridge.callNative('download/getTaskList'))?.data;
          if(!Array.isArray(tasks))throw Error('客户端未返回任务列表');
          const matches=(task,row)=>{{
            const keys=['bvid','aid','cid'].filter(k=>row[k]&&row[k]!=='0');
            return keys.length>0&&keys.every(k=>String(task[k]||'')===row[k]);
          }};
          const selected=tasks.filter(task=>wanted.some(row=>matches(task,row)));
          if(wanted.some(row=>!selected.some(task=>matches(task,row))))throw Error('部分勾选视频已不在缓存任务中，请同步状态后重试');
          const ids=[...new Set(selected.map(task=>task.itemId))];
          if(!ids.length)throw Error('没有可控制的缓存任务');
          await bridge.callNative({json.dumps('download/pauseTasks' if paused else 'download/restartTasks')},{{taskIds:ids}});
          for(let n=0;n<12;n++){{
            const current=(await bridge.callNative('download/getTaskList'))?.data;
            if(Array.isArray(current)&&ids.every(id=>{{const t=current.find(t=>t.itemId===id);return {"t?.status==='paused'" if paused else "!t||['downloading','connecting','pending','completed'].includes(t.status)"}}}))return {{count:ids.length}};
            await new Promise(resolve=>setTimeout(resolve,250));
          }}
          throw Error('指令已发送，但客户端尚未确认所选任务状态');
        }})()''') or {}
        return int(result.get('count') or 0)

    def show_client(self) -> None:
        self.background_mode = False
        self._background_applied = True
        _show_windows_for_pids(self._client_pids())

    def show_player(self) -> int:
        pids = self._client_pids()
        players = _window_handles_for_pids(pids) - _main_window_handles_for_pids(pids)
        if not players:
            raise BilibiliError("当前没有独立播放窗口，请先打开一个视频后重试")
        # Stop suppression first, including the WinEvent callback, before restoring.
        self.background_mode = False
        self.manual_intervention.set()
        self._background_guard_stop.set()
        thread = self._background_guard_thread
        if thread and thread is not threading.current_thread():
            thread.join(timeout=1)
        for hwnd in players:
            _center_window(hwnd)
        self.player_centered = True
        self.log(f"已将 {len(players)} 个播放窗口恢复到屏幕中央，已停止自动隐藏")
        return len(players)

    def hide_player(self) -> int:
        pids = self._client_pids()
        players = _window_handles_for_pids(pids) - _main_window_handles_for_pids(pids)
        _suppress_windows_for_handles(players)
        self.background_mode = True
        self._background_applied = True
        self.player_centered = False
        # The interrupted submission remains stopped; starting a new cache task
        # clears manual_intervention. Hiding must not revive the previous task.
        self.log(f"已将 {len(players)} 个播放窗口移出屏幕，恢复后台显示模式")
        return len(players)

    def hide_client(self) -> None:
        self.background_mode = True
        self._background_applied = True
        _minimize_windows_for_pids(self._client_pids())

    def _verified_client_pids(self) -> list[int]:
        kernel32=ctypes.windll.kernel32
        kernel32.QueryFullProcessImageNameW.argtypes=[wintypes.HANDLE,wintypes.DWORD,wintypes.LPWSTR,ctypes.POINTER(wintypes.DWORD)]
        kernel32.QueryFullProcessImageNameW.restype=wintypes.BOOL
        expected=os.path.normcase(str(Path(self.config.bilibili_exe).resolve()))
        result=[]
        for pid in self._client_pids():
            handle=kernel32.OpenProcess(0x1000,False,pid)
            if not handle:
                if pid in self._client_pids():
                    raise BilibiliError(f'无法核实B站客户端进程 PID={pid}，请手动正常退出客户端后重试')
                continue
            try:
                code=wintypes.DWORD()
                if kernel32.GetExitCodeProcess(handle,ctypes.byref(code)) and code.value!=259:
                    continue
                buffer=ctypes.create_unicode_buffer(32768);size=wintypes.DWORD(len(buffer))
                if not kernel32.QueryFullProcessImageNameW(handle,0,buffer,ctypes.byref(size)):
                    if kernel32.GetExitCodeProcess(handle,ctypes.byref(code)) and code.value!=259:
                        continue
                    raise BilibiliError(f'无法核实B站客户端进程路径 PID={pid}')
                if os.path.normcase(str(Path(buffer.value).resolve()))==expected:result.append(pid)
            finally:
                kernel32.CloseHandle(handle)
        return result

    def _stop_for_restart(self) -> None:
        pids=self._verified_client_pids()
        if not pids:
            self.close()
            return
        self.log(f'正在安全关闭B站客户端后台进程：PID={pids}')
        # First let Electron flush its state through a normal browser shutdown.
        closed=False
        if self._debug_endpoint_ready():
            try:
                self._resolve_main_page().command('Browser.close')
                closed=True
            except Exception as exc:
                self.log(f'客户端正常退出请求已发送或连接已关闭：{type(exc).__name__}')
        if not closed:
            for hwnd in _main_window_handles_for_pids(pids):
                ctypes.windll.user32.PostMessageW(wintypes.HWND(hwnd),0x0010,0,0)
        self.close()
        deadline=time.monotonic()+3
        while time.monotonic()<deadline and self._verified_client_pids():time.sleep(0.1)
        remaining=self._verified_client_pids()
        if remaining:
            self.log(f'客户端尚有后台进程，仅结束已核实路径的残留进程：PID={remaining}')
            _terminate_processes(remaining)
        deadline=time.monotonic()+8
        while time.monotonic()<deadline and self._verified_client_pids():time.sleep(0.1)
        if self._verified_client_pids():
            raise BilibiliError('B站客户端仍在退出中，本次未重新启动，请正常退出后重试')
        self._background_applied=False
        self.log('已确认B站客户端旧进程全部退出')

    @serialized_client_operation
    def restart_client(self) -> None:
        # The restart prompt can outlive the original connection check.  If a
        # previous restart (or a compatible manual launch) has made CDP ready
        # in the meantime, attach to that process instead of killing it again.
        if self._debug_endpoint_ready() and not getattr(self, '_cache_window_invalid', False):
            self.log("B站客户端连接已就绪，直接连接现有客户端，无需再次重启")
            self._apply_initial_background()
            self._resolve_main_page()
            return
        self._stop_for_restart()
        self._launch_client()
        self._cache_window_invalid = False

    def close(self) -> None:
        if self.main_page:
            self.main_page.close()
        self.main_page = None
