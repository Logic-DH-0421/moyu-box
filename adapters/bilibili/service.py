"""Loopback adapter for the existing Bilibili client cache manager.

No pywebview window is created. The native host owns the view and project data.
"""
from __future__ import annotations

import argparse
import json
import logging
import os
from pathlib import Path
import secrets
import sys
import threading
import types
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlsplit

if not getattr(sys, "frozen", False):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "vendor" / "bilibili"))
sys.modules["webview"] = types.SimpleNamespace(windows=[])

METHODS = frozenset("get_state read_log open_log open_log_folder history_action open_page_item save_settings set_selected select_visible clear_visible invert_visible refresh_queue start_client read_feed resolve_uploader feed_video_action get_feed stop_feed open_feed_video refresh_current sync_status start_cache delete_items update_danmaku preview_video add_preview_to_queue download_cover_image retry_failed pause resume stop skip show_client show_player open_offline_cache resolve_attention report_ui_metrics control_selected_cache set_video_layout open_author_website".split())
WINDOW_METHODS = frozenset("minimize_window begin_window_drag toggle_maximize_window close_window maximize_to_work_area restore_from_work_area is_maximized_to_work_area".split())


def bridge_script(token: str) -> str:
    # A literal JSON string is used instead of interpolating untrusted source code.
    return """<style>.window-bar{display:none!important}</style><script>
document.addEventListener('DOMContentLoaded',()=>{
 const token=TOKEN;
 window.pywebview={api:new Proxy({}, {get:(_target,method)=>async(...args)=>{
   const response=await fetch('/api',{method:'POST',headers:{'Content-Type':'application/json','X-Moyu-Token':token},body:JSON.stringify({method,args})});
   const result=await response.json();if(!response.ok)throw new Error(result.error||'服务请求失败');return result.value;
 }})};
 window.dispatchEvent(new Event('pywebviewready'));
});</script>""".replace("TOKEN", json.dumps(token))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", required=True)
    parser.add_argument("--web", required=True)
    parser.add_argument("--ready", required=True)
    args = parser.parse_args()
    data = Path(args.data).resolve()
    data.mkdir(parents=True, exist_ok=True)
    os.environ["MOYU_PROJECT_DATA"] = str(data)
    os.environ["BILIBILI_MANAGER_UI_PREVIEW"] = "0"
    token = os.environ.get("MOYU_SERVICE_TOKEN") or secrets.token_urlsafe(32)
    from bilibili_cache_manager.web_app import WebApi

    api = WebApi()
    # First integration never connects the official client automatically.
    api.config.launch_client_on_startup = False
    web = Path(args.web).resolve()
    api_lock = threading.RLock()

    class Handler(SimpleHTTPRequestHandler):
        def __init__(self, *values, **kwargs):
            super().__init__(*values, directory=str(web), **kwargs)

        def log_message(self, fmt, *values):
            # Paths, input values and tokens are not copied to request logs.
            pass

        def trusted_host(self) -> bool:
            return self.headers.get("Host") == f"127.0.0.1:{self.server.server_port}"

        def result(self, status: int, payload: object) -> None:
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            if not self.trusted_host():
                self.result(403, {"error": "Invalid host"})
                return
            path = urlsplit(self.path).path
            if path == "/health":
                self.result(200, {"ok": True, "adapter": "bilibili", "version": 1})
                return
            # Resolve before delegating static serving; no path may escape web.
            target = (web / unquote(path).lstrip("/")).resolve()
            if not target.is_relative_to(web):
                self.result(403, {"error": "Invalid path"})
                return
            if path in ("/", "/index.html"):
                html = (web / "index.html").read_text(encoding="utf-8")
                html = html.replace("</head>", bridge_script(token) + "</head>")
                body = html.encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(body)))
                self.send_header("Cache-Control", "no-store")
                self.send_header("Content-Security-Policy", "frame-ancestors 'none'; base-uri 'self'")
                self.end_headers()
                self.wfile.write(body)
                return
            if not target.is_file():
                self.result(404, {"error": "Not found"})
                return
            super().do_GET()

        def do_POST(self):
            origin = f"http://127.0.0.1:{self.server.server_port}"
            if not self.trusted_host() or self.headers.get("Origin", origin) != origin or not secrets.compare_digest(self.headers.get("X-Moyu-Token", ""), token):
                self.result(403, {"error": "请求来源或令牌无效"})
                return
            if self.path == "/shutdown":
                self.result(200, {"ok": True})
                threading.Thread(target=self.server.shutdown, daemon=True).start()
                return
            if self.path != "/api":
                self.result(404, {"error": "Not found"})
                return
            try:
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= 1024 * 1024:
                    self.result(413, {"error": "请求体大小无效"})
                    return
                payload = json.loads(self.rfile.read(length))
                if not isinstance(payload, dict) or not isinstance(payload.get("args"), list):
                    raise ValueError("请求结构无效")
                method = payload.get("method")
                if method in WINDOW_METHODS:
                    self.result(200, {"value": False})
                    return
                if method not in METHODS:
                    self.result(403, {"error": "接口未授权"})
                    return
                with api_lock:
                    result = getattr(api, method)(*payload["args"])
                self.result(200, {"value": result})
            except (ValueError, TypeError) as error:
                self.result(400, {"error": str(error)})
            except Exception as error:
                logging.exception("Bilibili adapter operation failed")
                self.result(500, {"error": str(error)})

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.daemon_threads = True
    ready = Path(args.ready)
    ready.write_text(json.dumps({"url": f"http://127.0.0.1:{server.server_port}/"}), encoding="utf-8")
    try:
        server.serve_forever(poll_interval=0.2)
    finally:
        api.close()
        server.server_close()
        ready.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
