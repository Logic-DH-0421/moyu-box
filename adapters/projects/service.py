"""Controlled local transports; project content/logs stay inside each project's data root."""
from __future__ import annotations
import argparse
from collections import deque
from dataclasses import asdict
import json
import mimetypes
import os
from pathlib import Path
import queue
import secrets
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlsplit

def injection(token):
    return ("<script>window.__moyuAdapter=true;(()=>{const original=window.fetch.bind(window),token=" + json.dumps(token) + ";window.fetch=(input,options={})=>{const url=new URL(typeof input==='string'?input:input.url,location.href);if(url.origin===location.origin&&url.pathname.startsWith('/api/')){const headers=new Headers(options.headers||(input instanceof Request?input.headers:undefined));headers.set('X-Moyu-Token',token);options={...options,headers};}return original(input,options);};})();</script>")

def handler_type(parent, token, web, kind, api=None):
    class Handler(parent):
        def log_message(self, *_): pass
        def respond(self, value, code=200):
            payload=json.dumps(value, ensure_ascii=False).encode('utf-8'); self.send_response(code)
            self.send_header('Content-Type','application/json; charset=utf-8'); self.send_header('Content-Length',str(len(payload))); self.send_header('Cache-Control','no-store'); self.end_headers(); self.wfile.write(payload)
        def trusted(self):
            origin=f'http://127.0.0.1:{self.server.server_port}'
            return self.headers.get('Host')==origin[7:] and self.headers.get('Origin',origin)==origin
        def authorized(self): return secrets.compare_digest(self.headers.get('X-Moyu-Token',''),token)
        def do_GET(self):
            if not self.trusted(): return self.respond({'error':'Invalid origin/host'},403)
            path=urlsplit(self.path).path
            if path=='/health': return self.respond({'ok':True,'adapter':kind})
            if path.startswith('/api/'):
                if not self.authorized(): return self.respond({'error':'Unauthorized'},403)
                return super().do_GET()
            target=(web/('index.html' if path=='/' else unquote(path).lstrip('/'))).resolve()
            if not target.is_relative_to(web): return self.respond({'error':'Invalid path'},403)
            if not target.is_file(): return self.respond({'error':'Not found'},404)
            data=target.read_bytes()
            if target.suffix=='.html': data=data.decode('utf-8-sig').replace('<head>','<head>'+injection(token),1).encode('utf-8')
            self.send_response(200); self.send_header('Content-Type',mimetypes.guess_type(str(target))[0] or 'application/octet-stream'); self.send_header('Content-Length',str(len(data))); self.send_header('Cache-Control','no-store'); self.send_header('Content-Security-Policy',"frame-ancestors 'none'; base-uri 'self'"); self.end_headers(); self.wfile.write(data)
        def do_POST(self):
            if not self.trusted() or not self.authorized(): return self.respond({'error':'Unauthorized'},403)
            path=urlsplit(self.path).path
            if path=='/shutdown':
                self.respond({'ok':True}); threading.Thread(target=self.server.shutdown,daemon=True).start(); return
            if path=='/api/minimize': return self.respond({'error':'窗口由摸鱼盒子管理'},400)
            return super().do_POST()
    return Handler

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--kind',choices=('multiai',),required=True); parser.add_argument('--data',required=True); parser.add_argument('--web',required=True); parser.add_argument('--ready',required=True); args=parser.parse_args()
    data=Path(args.data).resolve(); data.mkdir(parents=True,exist_ok=True); web=Path(args.web).resolve(); ready=Path(args.ready)
    os.environ['MOYU_PROJECT_DATA']=str(data); os.environ['MULTIAI_DATA_DIR']=str(data)
    if not getattr(sys,'frozen',False):
        root=Path(__file__).resolve().parents[2]; sys.path[:0]=[str(root/'vendor/multiai')]
    import multiai_app
    multiai_app.resource_path=lambda name: web.parent/name
    api=multiai_app.APP; parent=multiai_app.RequestHandler
    token=os.environ.get('MOYU_SERVICE_TOKEN') or secrets.token_urlsafe(32)
    server=ThreadingHTTPServer(('127.0.0.1',0),handler_type(parent,token,web,args.kind,api)); server.daemon_threads=True
    ready.write_text(json.dumps({'url':f'http://127.0.0.1:{server.server_port}/'}),encoding='utf-8')
    try: server.serve_forever(poll_interval=.2)
    finally:
        server.server_close()
        api.stop()
        ready.unlink(missing_ok=True)

if __name__=='__main__': main()
