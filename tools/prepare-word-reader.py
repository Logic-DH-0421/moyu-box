"""Prepare the MIT upstream renderer with one local-only adapter hook. No network/build scripts."""
from pathlib import Path
import hashlib
import json
import shutil
import sys

root = Path(__file__).resolve().parents[1]
upstream = root / 'vendor' / 'novel-disguise'
target = Path(sys.argv[1])
target.mkdir(parents=True, exist_ok=True)
record = json.loads((upstream / 'UPSTREAM.json').read_text(encoding='utf-8'))
for name, expected in record['sha256'].items():
    if hashlib.sha256((upstream / name).read_bytes()).hexdigest() != expected:
        raise ValueError('Upstream fingerprint changed: ' + name)

source = (upstream / 'novel-disguise.js').read_text(encoding='utf-8')
marker = '    // 切换原版界面'
assert source.count(marker) == 1
hook = '''    // MoyuBox local adapter (MIT modification): use the original Word renderer;
    // skip remote-site scraping, GM menus and the upstream E-key reload handler.
    if (typeof window.MoyuWordAdapter === 'function') {
        config.mode = DICT.MODE.WORD;
        config.theme = DICT.THEME.OFFICE;
        config.resourceResolution = DICT.RESOURCE_RESOLUTION.FORCE_1K;
        common();
        window.MoyuWordAdapter({
            setContent: function (element) { clearWordContent(); setWordContent($(element)); },
            title: setDisguisedTitle,
            detail: setWordDetail,
            upstream: 'NiaoBlush/novel-disguise@2.15.0'
        });
        return;
    }

'''
(target / 'engine.js').write_text('/* Copyright (c) 2024 NiaoBlush. MIT License; see LICENSE-novel-disguise.txt. */\n' + source.replace(marker, hook + marker), encoding='utf-8')
for name in ['novel-disguise-resource.js', 'novel-disguise-jquery.js']:
    shutil.copy2(upstream / name, target / name)
shutil.copy2(upstream / 'LICENSE', target / 'LICENSE-novel-disguise.txt')
shutil.copy2(upstream / 'UPSTREAM.json', target / 'UPSTREAM.json')
for path in (root / 'src' / 'WordReader').iterdir():
    if path.is_file(): shutil.copy2(path, target / path.name)
print('Word reader prepared from pinned MIT source: ' + record['commit'])
