"""Actual packaged project transports with isolated data, without sending content externally."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
from urllib.error import HTTPError
from urllib.request import Request, build_opener, ProxyHandler

ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'release/MoyuBox-0.8.0'
HTTP=build_opener(ProxyHandler({}))

class ProjectServices(unittest.TestCase):
    def run_service(self,kind,exercise):
        with tempfile.TemporaryDirectory(prefix='MoyuProject-') as temp:
            data=Path(temp); ready=data/'ready.json'; env=dict(os.environ,MOYU_SERVICE_TOKEN='integration-secret')
            process=subprocess.Popen([str(OUT/'adapters/MoyuProjectsService.exe'),'--kind',kind,'--data',str(data),'--web',str(OUT/'plugins'/kind/('web' if kind=='multiai' else '')),'--ready',str(ready)],env=env)
            try:
                for _ in range(200):
                    if ready.exists(): break
                    if process.poll() is not None: self.fail(f'{kind} exited {process.returncode}')
                    time.sleep(.1)
                self.assertTrue(ready.exists(),kind+' readiness timeout')
                url=json.loads(ready.read_text(encoding='utf-8-sig'))['url']
                def request(path,body=None,headers=None,token=True):
                    h={'X-Moyu-Token':'integration-secret'} if token else {}
                    if headers: h.update(headers)
                    return HTTP.open(Request(url+path,data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json',**h}),timeout=10)
                with request('') as response: self.assertIn(b'__moyuAdapter',response.read())
                for path,headers,token in [('api/state',{},False),('api/state',{'Origin':'https://foreign.example'},True),('api/state',{'Host':'evil.local'},True),('%2e%2e/hidden',{},True)]:
                    with self.assertRaises(HTTPError) as raised: request(path,headers=headers,token=token)
                    self.assertEqual(raised.exception.code,403); raised.exception.close()
                exercise(request,data)
                with request('shutdown',{}) as response: self.assertTrue(json.load(response)['ok'])
                process.wait(timeout=20); self.assertEqual(process.returncode,0); self.assertFalse(ready.exists())
            finally:
                if process.poll() is None: process.kill(); process.wait()
    def test_multiai_state_attachment_and_settings(self):
        def exercise(request,data):
            with self.assertRaises(HTTPError) as raised: request('api/minimize',{})
            self.assertEqual(raised.exception.code,400); raised.exception.close()
            with request('api/state') as response: state=json.load(response)
            self.assertEqual(state['version'],'0.14.1'); self.assertEqual(len(state['browsers']),4); self.assertGreaterEqual(len(state['platforms']),9)
            with request('api/attachment',{'name':'test.txt','data':'aGVsbG8='}) as response: attachment=json.load(response)
            self.assertIn('token',attachment['attachment']); self.assertTrue((data/'uploads').is_dir())
            with request('api/custom-platform',{'name':'isolated test','url':'https://example.com/chat'}) as response: self.assertTrue(json.load(response)['ok'])
            self.assertTrue((data/'custom-platforms.json').is_file())
        self.run_service('multiai',exercise)
if __name__=='__main__': unittest.main()
