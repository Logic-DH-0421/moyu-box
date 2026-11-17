from __future__ import annotations

import ctypes
import logging
import os
import queue
import re
import urllib.error
import urllib.request
from dataclasses import asdict, replace
from pathlib import Path
from typing import Any

import webview

from .bilibili_client import (
    BilibiliError,
    LoginRequired,
    PreviewLookupError,
    fetch_preview,
)
from .config_store import CONFIG_LOCK, app_data_dir, load_config, save_config, normalize_cache_directory
from .database import (
    counts,
    init_db,
    invert_selected,
    query_videos,
    reset_failed_selected,
    set_all_selected,
    set_selected,
    update_quality_for_pending,
    upsert_previewed_video,
    feed_video_states,
)
from .worker import AutomationWorker


QUALITY_OPTIONS = ["最高可用", "4K", "1080P高码率", "1080P", "720P", "480P"]
WINDOW_TITLE = "B站稍后再看管理器"
_LOG = logging.getLogger("bilibili_cache_manager.preview")
_PREVIEW_CACHE: dict[str, dict[str, Any]] = {}


def _log(msg: str) -> None:
    """Forward ``fetch_preview`` debug breadcrumbs into the main logger."""
    _LOG.info(msg)


def _covers_dir() -> Path:
    """Local directory where downloaded cover thumbnails are stored."""
    from .config_store import app_data_dir

    folder = app_data_dir() / "covers"
    folder.mkdir(parents=True, exist_ok=True)
    return folder


def _safe_bvid_for_filename(bvid: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9_-]+", "", bvid or "")
    return cleaned[:24] or "unknown"


def _fetch_preview_cached(identifier: str) -> dict[str, Any]:
    """Resolve ``identifier`` and remember the result for the lifetime of
    the process. Caching by normalised BV id so the same video pasted twice
    does not hit B 站 again.
    """
    item, _cached = _fetch_preview_cached_with_flag(identifier)
    return item


def _fetch_preview_cached_with_flag(identifier: str) -> tuple[dict[str, Any], bool]:
    """Like :func:`_fetch_preview_cached` but also returns whether the result
    was served from the in-memory cache (``True``) or freshly fetched
    from B 站 (``False``). The flag is forwarded to the UI so the toast can
    surface a "hit cache, Xms" line.
    """
    from .bilibili_client import _extract_bvid

    token = _extract_bvid(identifier) or identifier.strip()
    if token and token in _PREVIEW_CACHE:
        _LOG.info("封面预览命中本地缓存：%s", token)
        return _PREVIEW_CACHE[token], True
    item = fetch_preview(identifier, log=_log)
    if token:
        _PREVIEW_CACHE[token] = item
    return item, False


class _RECT(ctypes.Structure):
    _fields_ = [
        ("left", ctypes.c_long),
        ("top", ctypes.c_long),
        ("right", ctypes.c_long),
        ("bottom", ctypes.c_long),
    ]


class _MONITORINFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", ctypes.c_ulong),
        ("rcMonitor", _RECT),
        ("rcWork", _RECT),
        ("dwFlags", ctypes.c_ulong),
    ]


def find_manager_window() -> int | None:
    """Return the native HWND that belongs to this manager process."""
    user32 = ctypes.windll.user32
    current_pid = os.getpid()
    found: list[int] = []
    enum_proc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    @enum_proc
    def visit(hwnd: int, _lparam: int) -> bool:
        pid = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == current_pid and user32.IsWindowVisible(hwnd):
            length = user32.GetWindowTextLengthW(hwnd)
            buffer = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(hwnd, buffer, length + 1)
            if buffer.value == WINDOW_TITLE:
                found.append(int(hwnd))
        return True

    user32.EnumWindows(visit, 0)
    return found[0] if found else None


def configure_fixed_window_style() -> bool:
    """Keep native move/minimize support while disabling edge resizing."""
    user32 = ctypes.windll.user32
    hwnd = find_manager_window()
    if hwnd is None:
        logging.warning("未找到管理器原生窗口，无法应用固定窗口样式")
        return False
    GWL_STYLE = -16
    WS_THICKFRAME = 0x00040000
    WS_MINIMIZEBOX = 0x00020000
    WS_MAXIMIZEBOX = 0x00010000
    WS_SYSMENU = 0x00080000
    SWP_NOSIZE = 0x0001
    SWP_NOMOVE = 0x0002
    SWP_NOZORDER = 0x0004
    SWP_NOACTIVATE = 0x0010
    SWP_FRAMECHANGED = 0x0020
    get_style = user32.GetWindowLongPtrW
    set_style = user32.SetWindowLongPtrW
    get_style.restype = ctypes.c_ssize_t
    set_style.restype = ctypes.c_ssize_t
    style = int(get_style(hwnd, GWL_STYLE))
    style &= ~WS_THICKFRAME
    style &= ~WS_MAXIMIZEBOX
    style |= WS_MINIMIZEBOX | WS_SYSMENU
    set_style(hwnd, GWL_STYLE, style)
    user32.SetWindowPos(
        hwnd,
        0,
        0,
        0,
        0,
        0,
        SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED,
    )
    logging.info("已禁用窗口边框缩放，保留标题栏拖动和工作区最大化")
    return True


def setup_logging() -> Path:
    path = app_data_dir() / "diagnostic.log"
    logging.basicConfig(
        filename=path,
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
        encoding="utf-8",
    )
    from . import __version__
    logging.info("程序启动：版本=%s，PID=%s", __version__, os.getpid())
    return path


class WebApi:
    def __init__(self) -> None:
        init_db()
        self.log_path = setup_logging()
        self.config = load_config()
        self.worker = AutomationWorker(self.config)
        self.running_operation = ""
        self.preview_mode = os.environ.get("BILIBILI_MANAGER_UI_PREVIEW") == "1"
        self._window_maximized = False
        self._restore_rect: tuple[int, int, int, int] | None = None

    @staticmethod
    def _safe_int(value: Any) -> int:
        try:
            return int(value or 0)
        except (TypeError, ValueError):
            return 0

    def get_state(self, filters: dict[str, Any] | None = None) -> dict[str, Any]:
        filters = filters or {}
        normalized = {
            "view": str(filters.get("view", "queue")),
            "keyword": str(filters.get("keyword", "")),
            "uploader": str(filters.get("uploader", "")),
            "status": str(filters.get("status", "全部")),
            "quality": str(filters.get("quality", "")),
            "min_duration": self._safe_int(filters.get("min_duration")),
            "max_duration": self._safe_int(filters.get("max_duration")),
        }
        rows = [dict(row) for row in query_videos(normalized)]
        events: list[dict[str, Any]] = []
        while True:
            try:
                event = self.worker.events.get_nowait()
            except queue.Empty:
                break
            kind = str(event.get("kind", ""))
            if kind == "log":
                logging.info("%s", event.get("text", ""))
            elif kind == "error":
                logging.error("%s", event.get("text", ""))
            elif kind == "item_error":
                logging.warning("%s: %s", event.get("title", ""), event.get("text", ""))
            elif kind == "operation":
                self.running_operation = str(event.get("operation", "")) if event.get("running") else ""
            events.append(event)
        running, operation = self.worker.task_state()
        return {
            "rows": rows,
            "stats": counts(),
            "events": events,
            "config": asdict(self.config),
            "cache_directory_current": getattr(self.worker.client, 'cache_directory_current', ''),
            "quality_options": QUALITY_OPTIONS,
            "running": running,
            "operation": operation,
            "log_path": str(self.log_path),
            "preview_mode": self.preview_mode,
            "player_centered": bool(self.worker.client and self.worker.client.player_centered),
        }

    def save_settings(self, values: dict[str, Any]) -> bool:
        with CONFIG_LOCK:
            candidate = replace(self.config)
            candidate.cache_directory = normalize_cache_directory(values.get("cache_directory", candidate.cache_directory))
            quality = str(values.get("quality", candidate.quality))
            if quality in QUALITY_OPTIONS:
                candidate.quality = quality
            for field in ("cache_all", "auto_fallback", "keep_watch_later", "hidden_mode", "launch_client_on_startup"):
                setattr(candidate, field, bool(values.get(field, getattr(candidate, field))))
            save_config(candidate)
            self.config = candidate
            self.worker.config = candidate
            update_quality_for_pending(candidate.quality)
            if candidate.cache_all:
                set_all_selected(True)
            return True

    def set_selected(self, ids: list[int], selected: bool) -> bool:
        set_selected(ids, selected)
        return True

    def select_visible(self, ids: list[int]) -> bool:
        set_all_selected(True, only_ids=ids)
        return True

    def clear_visible(self, ids: list[int]) -> bool:
        with CONFIG_LOCK:
            candidate = replace(self.config, cache_all=False)
            save_config(candidate)
            self.config = candidate
            self.worker.config = candidate
            set_all_selected(False, only_ids=ids)
            return True

    def invert_visible(self, ids: list[int]) -> bool:
        invert_selected(ids)
        return True

    def refresh_queue(self) -> bool:
        return self.worker.start_refresh()

    def start_client(self) -> bool:
        return self.worker.start_connect()

    def open_log(self) -> bool:
        """Open the local diagnostic log from the sidebar entry."""
        try:
            self.log_path.parent.mkdir(parents=True, exist_ok=True)
            self.log_path.touch(exist_ok=True)
            os.startfile(str(self.log_path))
            return True
        except OSError as exc:
            logging.error("无法打开日志文件：%s", exc)
            return False

    def read_log(self) -> dict:
        from . import __version__
        try:
            with self.log_path.open('rb') as handle:
                handle.seek(max(0, self.log_path.stat().st_size - 128 * 1024))
                lines=handle.read().decode('utf-8',errors='replace').splitlines()[-150:]
            return {'ok':True,'text':'\n'.join(line[:1500] for line in lines),'path':str(self.log_path),'version':__version__}
        except OSError as exc:
            return {'ok':False,'text':str(exc),'version':__version__}

    def open_author_website(self) -> bool:
        import webbrowser
        return bool(webbrowser.open('https://www.openlogicspace.cn/'))

    def open_log_folder(self) -> bool:
        try:
            os.startfile(str(self.log_path.parent))
            return True
        except OSError:
            return False

    def history_action(self, action: str, key: str = '') -> bool:
        return self.worker.start_history_action(action, key)

    def open_page_item(self, source: str, key: str) -> bool:
        import webbrowser
        from urllib.parse import urlparse
        if source not in ('search','creator','history'):return False
        item=next((r for r in self.worker.feeds.get(source)['items'] if r['bvid']==key),None)
        if not item:return False
        if re.fullmatch(r'BV[0-9A-Za-z]{10}',key):
            progress=max(0,int(item.get('watch_progress') or 0))
            url=f'https://www.bilibili.com/video/{key}?t={progress}'
        else:
            url=str(item.get('uri') or '')
            if url.startswith('//'):url='https:'+url
        parsed=urlparse(url)
        if parsed.scheme not in ('https','http') or not (parsed.hostname=='bilibili.com' or (parsed.hostname or '').endswith('.bilibili.com')):return False
        return webbrowser.open(url)

    def read_feed(self, source: str, more: bool = False, keyword: str = '', options: dict | None = None, request_id: str = '') -> bool:
        return self.worker.start_feed(source, bool(more), str(keyword or ''), options, str(request_id or ''))

    def resolve_uploader(self, bvid: str) -> dict:
        try:
            client = self.worker._get_client()
            with client.operation_lock:
                return {'ok': True, **client.resolve_uploader(bvid)}
        except BilibiliError as exc:
            return {'ok': False, 'error': str(exc)}

    def feed_video_action(self, source: str, bvid: str, action: str) -> bool:
        return self.worker.start_feed_action(source, bvid, action)

    def get_feed(self, source: str) -> dict[str, Any]:
        with self.worker.feeds.lock:
            result = self.worker.feeds.get(source)
            account = self.worker.feeds.account
            memberships = set(self.worker.feeds.watch_later)
        statuses = feed_video_states(item['bvid'] for item in result['items'])
        for item in result['items']:
            item['status'] = statuses.get(item['bvid'], '未缓存')
            item['watch_later'] = item['bvid'] in memberships
        return {**result, 'account': account}

    def stop_feed(self) -> bool:
        return self.worker.stop_feed()

    def open_feed_video(self, bvid: str) -> bool:
        import webbrowser
        if not re.fullmatch(r'BV[0-9A-Za-z]{10}', bvid):
            return False
        return webbrowser.open(f'https://www.bilibili.com/video/{bvid}')

    def refresh_current(self, view: str) -> bool:
        return self.worker.start_refresh()

    def sync_status(self) -> bool:
        return self.worker.start_status_sync()

    def start_cache(self, ids: list[int]) -> bool:
        if ids:
            set_selected(ids, True)
        if self.config.cache_all:
            set_all_selected(True)
        return self.worker.start_cache()

    def delete_items(self, ids: list[int], view: str) -> bool:
        if view == "queue":
            return self.worker.start_delete_watch_later(ids)
        return self.worker.start_delete_cache(ids)

    def update_danmaku(self, ids: list[int]) -> bool:
        return self.worker.start_update_danmaku(ids)

    def preview_video(self, identifier: str) -> dict[str, Any]:
        """Resolve a BV/av id / share link and return cover + title only.

        The result is **not** written to the database — the user still has to
        press "加入缓存" before the row shows up in the watch-later queue.
        Returns ``{ok, item}`` on success or ``{ok=False, error, code}`` so the
        UI can decide between a regular toast and a login-required prompt.
        """
        try:
            item, cached = _fetch_preview_cached_with_flag(identifier)
        except LoginRequired as exc:
            logging.warning("封面预览需登录：%s", exc)
            return {"ok": False, "code": "login", "error": str(exc)}
        except PreviewLookupError as exc:
            logging.warning("封面预览失败：%s", exc)
            return {"ok": False, "code": "lookup", "error": str(exc)}
        except BilibiliError as exc:
            logging.error("封面预览异常：%s", exc)
            return {"ok": False, "code": "unknown", "error": str(exc)}
        return {"ok": True, "item": item, "cached": cached}

    def add_preview_to_queue(self, identifier: str) -> dict[str, Any]:
        """Resolve the identifier and persist it as a selected queue row."""
        try:
            item = _fetch_preview_cached(identifier)
        except LoginRequired as exc:
            return {"ok": False, "code": "login", "error": str(exc)}
        except PreviewLookupError as exc:
            return {"ok": False, "code": "lookup", "error": str(exc)}
        row = upsert_previewed_video(item)
        logging.info(
            "封面预览入队列：bvid=%s id=%s created=%s",
            item.get("bvid"),
            row["id"],
            row["created"],
        )
        return {"ok": True, "id": row["id"], "created": row["created"], "item": item}

    def download_cover_image(self, bvid: str) -> dict[str, Any]:
        """Persist the most recently previewed cover thumbnail to ``covers/``.

        The thumbnail URL comes from the in-memory preview cache populated by
        ``preview_video``; if the user has not previewed the BV in this
        process we fall back to a metadata lookup. The result is the absolute
        path on disk so the UI can offer "打开所在文件夹".
        """
        from .bilibili_client import _extract_bvid

        token = _extract_bvid(bvid) or (bvid or "").strip()
        if not token:
            return {"ok": False, "error": "缺少有效的 BV 号"}
        cached = _PREVIEW_CACHE.get(token)
        if cached is None:
            try:
                cached = fetch_preview(token, log=_log)
            except BilibiliError as exc:
                return {"ok": False, "error": str(exc)}
            _PREVIEW_CACHE[token] = cached
        pic = str(cached.get("cover") or cached.get("source_hint") or "").strip()
        if not pic:
            return {"ok": False, "error": "该视频未返回封面地址"}
        if pic.startswith("//"):
            pic = "https:" + pic
        elif pic.startswith("http://"):
            pic = "https://" + pic[len("http://"):]
        target = _covers_dir() / f"{_safe_bvid_for_filename(token)}.jpg"
        try:
            req = urllib.request.Request(
                pic,
                headers={
                    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
                    "Referer": "https://www.bilibili.com/",
                },
            )
            with urllib.request.urlopen(req, timeout=20) as resp:
                data = resp.read()
            target.write_bytes(data)
        except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError) as exc:
            return {"ok": False, "error": f"下载封面图失败：{exc}"}
        size = target.stat().st_size
        logging.info("封面图已下载：%s (%d bytes)", target, size)
        return {"ok": True, "path": str(target), "bytes": size}

    def retry_failed(self) -> int:
        return reset_failed_selected()

    def pause(self) -> bool:
        self.worker.pause()
        return True

    def control_selected_cache(self, ids: list[int], paused: bool) -> bool:
        return self.worker.start_selected_cache_control([int(value) for value in ids], bool(paused))

    def set_video_layout(self, source: str, mode: str) -> bool:
        if source not in ('queue','downloading','completed','recommend','popular','dynamic','search','creator','history') or mode not in ('list','grid'):
            return False
        with CONFIG_LOCK:
            candidate = replace(self.config, layout_modes={**self.config.layout_modes, source: mode})
            save_config(candidate)
            self.config = candidate
            self.worker.config = candidate
        return True

    def resume(self) -> bool:
        self.worker.resume()
        return True

    def stop(self) -> bool:
        self.worker.stop()
        return True

    def skip(self) -> bool:
        self.worker.skip()
        return True

    def show_client(self) -> bool:
        self.worker.show_client()
        return True

    def show_player(self) -> bool:
        self.worker.show_player()
        return True

    def open_offline_cache(self) -> bool:
        return self.worker.open_offline_cache()

    def resolve_attention(self, action: str, approved: bool) -> bool:
        if not approved:
            return True
        if action == "restart":
            return self.worker.start_restart_refresh()
        self.worker.show_client()
        return True

    def close(self, *_args: Any) -> None:
        self.worker.close()

    def minimize_window(self) -> bool:
        if webview.windows:
            webview.windows[0].minimize()
        return True

    def begin_window_drag(self) -> bool:
        hwnd = find_manager_window()
        if hwnd is None:
            return False
        user32 = ctypes.windll.user32
        WM_NCLBUTTONDOWN = 0x00A1
        HTCAPTION = 2
        user32.ReleaseCapture()
        user32.SendMessageW(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0)
        return True

    def is_maximized_to_work_area(self) -> bool:
        hwnd = find_manager_window()
        if hwnd is None:
            return False
        user32 = ctypes.windll.user32
        current = _RECT()
        if not user32.GetWindowRect(hwnd, ctypes.byref(current)):
            return False
        monitor = user32.MonitorFromWindow(hwnd, 2)
        info = _MONITORINFO()
        info.cbSize = ctypes.sizeof(_MONITORINFO)
        if not monitor or not user32.GetMonitorInfoW(monitor, ctypes.byref(info)):
            return False
        work = info.rcWork
        tolerance = 2
        return all(
            abs(a - b) <= tolerance
            for a, b in (
                (current.left, work.left),
                (current.top, work.top),
                (current.right, work.right),
                (current.bottom, work.bottom),
            )
        )

    def maximize_to_work_area(self) -> bool:
        """Fill the current monitor's work area without covering its taskbar."""
        hwnd = find_manager_window()
        if hwnd is None:
            logging.warning("最大化失败：未找到管理器原生窗口")
            return False
        user32 = ctypes.windll.user32
        current = _RECT()
        if not self._window_maximized and user32.GetWindowRect(hwnd, ctypes.byref(current)):
            self._restore_rect = (
                int(current.left),
                int(current.top),
                int(current.right - current.left),
                int(current.bottom - current.top),
            )
        MONITOR_DEFAULTTONEAREST = 2
        monitor = user32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
        info = _MONITORINFO()
        info.cbSize = ctypes.sizeof(_MONITORINFO)
        if not monitor or not user32.GetMonitorInfoW(monitor, ctypes.byref(info)):
            logging.warning("最大化失败：无法读取当前显示器工作区")
            return False
        work = info.rcWork
        SWP_NOZORDER = 0x0004
        SWP_NOACTIVATE = 0x0010
        ok = bool(
            user32.SetWindowPos(
                hwnd,
                0,
                int(work.left),
                int(work.top),
                int(work.right - work.left),
                int(work.bottom - work.top),
                SWP_NOZORDER | SWP_NOACTIVATE,
            )
        )
        if ok:
            self._window_maximized = True
            logging.info(
                "窗口已按当前显示器工作区最大化：%s,%s %sx%s",
                work.left,
                work.top,
                work.right - work.left,
                work.bottom - work.top,
            )
        return ok

    def restore_from_work_area(self) -> bool:
        hwnd = find_manager_window()
        if hwnd is None or self._restore_rect is None:
            return False
        x, y, width, height = self._restore_rect
        SWP_NOZORDER = 0x0004
        SWP_NOACTIVATE = 0x0010
        ok = bool(
            ctypes.windll.user32.SetWindowPos(
                hwnd, 0, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE
            )
        )
        if ok:
            self._window_maximized = False
            logging.info("窗口已还原：%s,%s %sx%s", x, y, width, height)
        return ok

    def toggle_maximize_window(self) -> bool:
        self._window_maximized = self.is_maximized_to_work_area()
        if self._window_maximized:
            return self.restore_from_work_area()
        return self.maximize_to_work_area()

    def close_window(self) -> bool:
        if webview.windows:
            webview.windows[0].destroy()
        return True

    def report_ui_metrics(self, values: dict[str, Any]) -> bool:
        logging.info("UI_METRICS %s", values)
        return True


def run() -> None:
    api = WebApi()
    html = Path(__file__).resolve().parent / "web" / "index.html"
    window = webview.create_window(
        WINDOW_TITLE,
        url=html.as_uri(),
        js_api=api,
        # A narrower default window; the title-bar maximize control stays available.
        width=1120,
        height=768,
        min_size=(900, 576),
        resizable=False,
        maximized=False,
        background_color="#fff8fb",
        hidden=os.environ.get("BILIBILI_MANAGER_UI_HEADLESS") == "1",
        frameless=True,
        easy_drag=False,
    )
    def prepare_window() -> None:
        configure_fixed_window_style()

    window.events.shown += prepare_window
    window.events.closed += api.close
    webview.start(gui="edgechromium", debug=False, private_mode=False)
