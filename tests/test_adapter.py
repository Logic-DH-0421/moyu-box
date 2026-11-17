import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.error
import urllib.request

ROOT=Path(__file__).resolve().parents[1]

class BilibiliAdapterTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (ROOT/'artifacts').mkdir(exist_ok=True)
        cls.temp=tempfile.TemporaryDirectory(prefix='moyubox-adapter-',dir=ROOT/'artifacts')
        cls.data=Path(cls.temp.name)
        cls.token='integration-only-token'
        env=os.environ.copy()
        env.update(MOYU_SERVICE_TOKEN=cls.token,PYTHONDONTWRITEBYTECODE='1')
        cls.ready=cls.data/'ready.json'
        cls.process=subprocess.Popen([sys.executable,str(ROOT/'adapters/bilibili/service.py'),'--data',str(cls.data),'--web',str(ROOT/'vendor/bilibili/bilibili_cache_manager/web'),'--ready',str(cls.ready)],env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW)
        for _ in range(150):
            if cls.ready.exists():
                cls.url=json.loads(cls.ready.read_text())['url']
                return
            if cls.process.poll() is not None:
                raise RuntimeError(cls.process.communicate()[1].decode(errors='replace'))
            time.sleep(.1)
        cls.process.kill()
        raise RuntimeError('Adapter did not start')

    @classmethod
    def tearDownClass(cls):
        try:
            urllib.request.urlopen(urllib.request.Request(cls.url+'shutdown',data=b'{}',headers={'X-Moyu-Token':cls.token}),timeout=3).close()
            cls.process.wait(timeout=5)
        finally:
            if cls.process.poll() is None: cls.process.kill()
            cls.process.communicate(timeout=3)
            cls.temp.cleanup()

    def request(self,payload,token=None,origin=None):
        headers={'Content-Type':'application/json','X-Moyu-Token':self.token if token is None else token}
        if origin: headers['Origin']=origin
        return urllib.request.urlopen(urllib.request.Request(self.url+'api',data=json.dumps(payload).encode(),headers=headers),timeout=5)

    def test_real_api_and_isolated_data(self):
        with self.request({'method':'get_state','args':[{}]}) as response:
            data=json.load(response)['value']
        self.assertFalse(data['config']['launch_client_on_startup'])
        self.assertTrue((self.data/'videos.db').is_file())

    def test_wrong_token_and_foreign_origin_are_rejected(self):
        for kwargs in [{'token':'wrong'},{'origin':'https://untrusted.example'}]:
            with self.assertRaises(urllib.error.HTTPError) as error:self.request({'method':'get_state','args':[]},**kwargs)
            self.assertEqual(error.exception.code,403)
            error.exception.close()

    def test_private_or_unknown_operations_are_rejected(self):
        for method in ['__getattribute__','close','setup_logging','arbitrary_command']:
            with self.assertRaises(urllib.error.HTTPError) as error:self.request({'method':method,'args':[]})
            self.assertEqual(error.exception.code,403)
            error.exception.close()

    def test_static_path_cannot_escape_package(self):
        with self.assertRaises(urllib.error.HTTPError) as error:urllib.request.urlopen(self.url+'%2e%2e/config_store.py',timeout=5)
        self.assertEqual(error.exception.code,403)
        error.exception.close()

    def test_unknown_structure_fails_without_crashing_service(self):
        with self.assertRaises(urllib.error.HTTPError) as error:self.request({'method':'get_state','args':'bad'})
        self.assertEqual(error.exception.code,400)
        error.exception.close()
        with urllib.request.urlopen(self.url+'health',timeout=5) as response:self.assertTrue(json.load(response)['ok'])

if __name__=='__main__':unittest.main()
