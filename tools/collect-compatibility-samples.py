"""Download traceable public samples. Does not execute downloaded software."""
from pathlib import Path
import concurrent.futures
import hashlib
import json
import sys
import time
import urllib.request
import zipfile

ROOT = Path(sys.argv[1]).resolve()
ROOT.mkdir(parents=True, exist_ok=True)
HEADERS = {'User-Agent': 'MoyuBox-Compatibility-Test/0.6.0'}

def fetch(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=HEADERS), timeout=30) as r:
        began=time.monotonic(); chunks=[]; total=0
        while True:
            chunk=r.read(65536)
            if not chunk:break
            chunks.append(chunk);total+=len(chunk)
            if time.monotonic()-began>65:raise TimeoutError('Sample download total deadline exceeded')
            if total>70*1024*1024:raise ValueError('Download exceeds test budget')
        data=b''.join(chunks)
        expected=r.headers.get('Content-Length')
        if expected and int(expected)!=len(data):
            raise IOError('Incomplete download: expected %s bytes, received %s' % (expected,len(data)))
        if len(data) > 70 * 1024 * 1024:
            raise ValueError('Download exceeds test budget')
        return data, r.geturl(), r.headers.get('Content-Type', '')

def latest_asset(repo, suffix):
    data, _, _ = fetch('https://api.github.com/repos/' + repo + '/releases/latest')
    release = json.loads(data)
    asset = next(x for x in release['assets'] if x['name'].endswith(suffix))
    return asset['browser_download_url'], release['tag_name']

samples = []
def add(id, source, format, name, url, **extra):
    samples.append(dict(id=id, source=source, format=format, name=name, url=url, **extra))

try:
    u, v = latest_asset('zufuliu/notepad4', 'Notepad4_en_x64_v26.10r6392.zip')
except Exception:
    u, v = 'https://github.com/zufuliu/notepad4/releases/download/v26.10r6392/Notepad4_en_x64_v26.10r6392.zip', 'v26.10r6392'
add('gh-exe-1', 'GitHub', 'EXE', 'Notepad4', u, version=v, archive=True, executable='Notepad4.exe')
add('gh-exe-2', 'GitHub', 'EXE', 'FindInFiles .NET 4.8', 'https://github.com/zufuliu/notepad4/releases/download/v26.10r6392/FindInFiles-net4.8.zip', version='v26.10r6392', archive=True, executable='FindInFiles.exe')
add('web-exe-1', 'Public', 'EXE', 'HashMyFiles', 'https://www.nirsoft.net/utils/hashmyfiles-x64.zip', archive=True, executable='HashMyFiles.exe', homepage='https://www.nirsoft.net/utils/hash_my_files.html')
add('web-exe-2', 'Public', 'EXE', 'AlternateStreamView', 'https://www.nirsoft.net/utils/alternatestreamview-x64.zip', archive=True, executable='AlternateStreamView.exe', homepage='https://www.nirsoft.net/utils/alternate_data_streams.html')
add('gh-zip-1', 'GitHub', 'ZIP', '2048 source', 'https://codeload.github.com/gabrielecirulli/2048/zip/refs/heads/master', archive=True, entry='2048-master/index.html', probe='2048')
add('gh-zip-2', 'GitHub', 'ZIP', 'Hextris source', 'https://codeload.github.com/Hextris/hextris/zip/refs/heads/gh-pages', archive=True, entry='hextris-gh-pages/index.html', probe='hextris')
add('web-zip-1', 'Public', 'ZIP', 'HTML5UP Dimension', 'https://html5up.net/dimension/download', archive=True, entry='index.html', probe='dimension', homepage='https://html5up.net/dimension')
add('web-zip-2', 'Public', 'ZIP', 'HTML5UP Massively', 'https://html5up.net/massively/download', archive=True, entry='index.html', probe='massively', homepage='https://html5up.net/massively')
add('gh-web-1', 'GitHub', 'WEB', '2048 deployed', 'https://play2048.co/', repo='https://github.com/gabrielecirulli/2048', probe='2048')
add('gh-web-2', 'GitHub', 'WEB', 'Hextris deployed', 'https://hextris.io/', repo='https://github.com/Hextris/hextris', probe='hextris')
add('web-web-1', 'Public', 'WEB', 'MDN HTML', 'https://developer.mozilla.org/en-US/docs/Web/HTML', probe='HTML')
add('web-web-2', 'Public', 'WEB', 'W3C CSS', 'https://www.w3.org/Style/CSS/', probe='CSS')
add('gh-txt-1', 'GitHub', 'TXT', 'Alice corpus', 'https://raw.githubusercontent.com/dakrone/corpus/master/data/alice-in-wonderland.txt')
add('gh-txt-2', 'GitHub', 'TXT', 'Tiny Shakespeare', 'https://raw.githubusercontent.com/karpathy/char-rnn/master/data/tinyshakespeare/input.txt')
add('web-txt-1', 'Public', 'TXT', 'Alice Gutenberg', 'https://www.gutenberg.org/cache/epub/11/pg11.txt')
add('web-txt-2', 'Public', 'TXT', '三字經 Gutenberg', 'https://www.gutenberg.org/cache/epub/25160/pg25160.txt')
add('gh-epub-1', 'GitHub', 'EPUB', 'IDPF Moby Dick', 'https://github.com/IDPF/epub3-samples/releases/download/20230704/moby-dick.epub')
add('gh-epub-2', 'GitHub', 'EPUB', 'IDPF accessible EPUB 3', 'https://github.com/IDPF/epub3-samples/releases/download/20230704/accessible_epub_3.epub')
add('web-epub-1', 'Public', 'EPUB', 'Alice Gutenberg EPUB3', 'https://www.gutenberg.org/ebooks/11.epub3.images')
add('web-epub-2', 'Public', 'EPUB', 'Pride and Prejudice EPUB3', 'https://www.gutenberg.org/ebooks/1342.epub3.images')
add('gh-html-1', 'GitHub', 'HTML', 'MDN getting started', 'https://raw.githubusercontent.com/mdn/learning-area/main/html/introduction-to-html/getting-started/index.html')
add('gh-html-2', 'GitHub', 'HTML', 'HTML5 Boilerplate', 'https://raw.githubusercontent.com/h5bp/html5-boilerplate/main/src/index.html')
add('web-html-1', 'Public', 'HTML', 'CERN first website', 'https://info.cern.ch/hypertext/WWW/TheProject.html')
add('web-html-2', 'Public', 'HTML', 'W3C URI style', 'https://www.w3.org/Provider/Style/URI.html')
add('gh-pdf-1', 'GitHub', 'PDF', 'PDF.js issue sample', 'https://raw.githubusercontent.com/mozilla/pdf.js/master/test/pdfs/annotation-button-widget.pdf')
add('gh-pdf-2', 'GitHub', 'PDF', 'PDF.js basic API', 'https://raw.githubusercontent.com/mozilla/pdf.js/master/test/pdfs/basicapi.pdf')
add('web-pdf-1', 'Public', 'PDF', 'W3C dummy PDF', 'https://www.w3.org/WAI/ER/tests/xhtml/testfiles/resources/pdf/dummy.pdf')
add('web-pdf-2', 'Public', 'PDF', 'Orimi PDF test', 'https://www.orimi.com/pdf-test.pdf')

def download(s):
    if s['format'] == 'WEB':
        s['download_status'] = 'live_url'; return s
    folder = ROOT / 'samples' / s['id']; folder.mkdir(parents=True, exist_ok=True)
    filename = 'download.zip' if s.get('archive') else 'sample.' + s['format'].lower()
    path = folder / filename
    for attempt in range(1, 3):
        try:
            if attempt==1 and path.exists() and s['id'] not in ('gh-exe-2','gh-pdf-1'):
                data=path.read_bytes();resolved=s['url'];content_type='cached original download'
            else:data, resolved, content_type = fetch(s['url'])
            path.write_bytes(data)
            s.update(path=str(path), resolved_url=resolved, content_type=content_type,
                     bytes=len(data), sha256=hashlib.sha256(data).hexdigest(), attempt=attempt)
            if s.get('archive'):
                target = folder / 'extracted'; target.mkdir(exist_ok=True)
                with zipfile.ZipFile(path) as z:
                    if sum(i.file_size for i in z.infolist()) > 150*1024*1024 or len(z.infolist()) > 7000:
                        raise ValueError('Archive exceeds test budget')
                    for i in z.infolist():
                        p = (target / i.filename).resolve()
                        if not p.is_relative_to(target.resolve()): raise ValueError('Unsafe archive path')
                    z.extractall(target)
                if s.get('executable'):
                    exe = next(target.rglob(s['executable']))
                    if exe.read_bytes()[:2] != b'MZ': raise ValueError('Not a Windows executable')
                    s['entry_path'] = str(exe); s['exe_sha256'] = hashlib.sha256(exe.read_bytes()).hexdigest()
                else:
                    entry = target / s['entry']
                    if not entry.is_file(): raise ValueError('Expected HTML entry missing')
                    s['entry_path'] = str(entry)
            elif s['format'] in ('EPUB', 'PDF'):
                if not data.startswith(b'PK' if s['format']=='EPUB' else b'%PDF'): raise ValueError('Unexpected file signature')
                if s['format']=='EPUB':
                    with zipfile.ZipFile(path) as z:
                        bad=z.testzip()
                        if bad:raise ValueError('EPUB CRC failed: '+bad)
            s['download_status'] = 'ok'; s.pop('download_error', None)
            print(s['id'], 'OK', len(data), flush=True); return s
        except Exception as e:
            s['download_error'] = type(e).__name__ + ': ' + str(e)
    s['download_status'] = 'failed'; print(s['id'], s['download_error'], flush=True); return s

with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
    results = list(pool.map(download, samples))
(ROOT/'samples.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print('SAVED', ROOT/'samples.json', flush=True)
