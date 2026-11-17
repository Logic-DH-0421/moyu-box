"""Account-scoped feed snapshots, separate from the authoritative cache queue."""
import json
import math
import threading
import uuid
from datetime import datetime
from pathlib import Path

SOURCES = {'recommend': '推荐', 'popular': '热门', 'dynamic': '动态', 'search': '搜索', 'creator': 'UP主投稿', 'history':'历史记录'}


def _snapshot(value):
    """Validate untrusted persisted data without discarding usable rows/metadata."""
    if not isinstance(value, dict):
        value = {}
    result = dict(value)
    rows = value.get('items', [])
    clean = []
    for row in rows if isinstance(rows, list) else []:
        if not isinstance(row, dict) or not isinstance(row.get('bvid'), str) or not row['bvid'].strip():
            continue
        item = dict(row)
        for field in ('title', 'uploader', 'cover', 'duration_text', 'uploader_mid', 'repost_by'):
            if field in item and not isinstance(item[field], str):
                item[field] = ''
        duration = item.get('duration_seconds', 0)
        if type(duration) not in (int, float) or (type(duration) is float and not math.isfinite(duration)) or duration < 0:
            item['duration_seconds'] = 0
        clean.append(item)
    result['items'] = clean
    result['updated'] = value.get('updated') if isinstance(value.get('updated'), str) else ''
    result['ended'] = value.get('ended') if type(value.get('ended')) is bool else False
    for field in ('query', 'request_id'):
        if field in result and not isinstance(result[field], str):
            result[field] = ''
    if 'page' in result and (type(result['page']) is not int or result['page'] < 1):
        result['page'] = 1
    if 'options' in result:
        options = result['options'] if isinstance(result['options'], dict) else {}
        result['options'] = {
            **{key:val for key,val in options.items() if key in ('type','tab','collection_id','collection_type','keyword') and isinstance(val,str)},
            'order': options.get('order') if isinstance(options.get('order'), str) else 'totalrank',
            'duration': options.get('duration') if type(options.get('duration')) is int and options['duration'] >= 0 else 0,
            'tids': options.get('tids') if type(options.get('tids')) is int and options['tids'] >= 0 else 0,
        }
    return result


def _saved_feeds(value):
    if not isinstance(value, dict):
        return {}
    return {account: {source: _snapshot(snapshot) for source, snapshot in sources.items() if source in SOURCES}
            for account, sources in value.items() if isinstance(account, str) and account and isinstance(sources, dict)}


class FeedStore:
    def __init__(self, path: Path):
        self.path = path
        self.lock = threading.RLock()
        self.account = ''
        self.snapshots = {}
        self.watch_later = set()
        # Account must be verified in the live client before saved personal feeds are shown.
        self._invalid_original = None
        try:
            original = path.read_bytes()
        except OSError:
            self.saved = {}
        else:
            try:
                raw = json.loads(original.decode('utf-8-sig'))
                self.saved = _saved_feeds(raw)
                if self.saved != raw:
                    self._invalid_original = original
            except (ValueError, TypeError):
                self.saved = {}
                self._invalid_original = original

    def set_account(self, account):
        if not isinstance(account, str) or not account.strip():
            raise ValueError('无法确认客户端账号，请重新连接后读取')
        with self.lock:
            if self.account != account:
                snapshots = self.saved.get(account, {})
                self.snapshots = snapshots
                self.account = account
                self.watch_later.clear()

    def set_watch_later(self, bvids):
        with self.lock:
            self.watch_later = set(bvids)

    def mark_watch_later(self, bvid):
        with self.lock:
            self.watch_later.add(bvid)

    def unmark_watch_later(self, bvid):
        with self.lock:
            self.watch_later.discard(bvid)

    def get(self, source):
        if source not in SOURCES:
            raise ValueError('未知视频来源')
        with self.lock:
            return json.loads(json.dumps(self.snapshots.get(source, {'items': [], 'updated': '', 'ended': False})))

    def update(self, source, items, *, more=False, ended=False, meta=None):
        if source not in SOURCES or not self.account:
            raise ValueError('未确认视频来源或账号')
        with self.lock:
            previous_snapshot = self.get(source)
            previous = previous_snapshot['items']
            if source in ('search', 'creator', 'history') and ((meta or {}).get('query') != previous_snapshot.get('query') or (meta or {}).get('options') != previous_snapshot.get('options')):
                previous = []
            items = _snapshot({'items': items})['items']
            order = previous + items if more else items + (previous if source == 'dynamic' else [])
            unique = {}
            for item in order:
                key = item['bvid']
                if key not in unique:
                    unique[key] = item
            snapshot = _snapshot({**(meta or {}), 'items': list(unique.values()), 'updated': datetime.now().isoformat(timespec='seconds'), 'ended': ended})
            snapshots = {**self.snapshots, source: snapshot}
            saved = {**self.saved, self.account: snapshots}
            self.path.parent.mkdir(parents=True, exist_ok=True)
            if self._invalid_original is not None:
                backup = self.path.with_name(self.path.name + '.invalid-' + uuid.uuid4().hex + '.bak')
                with backup.open('xb') as handle:
                    handle.write(self._invalid_original)
                self._invalid_original = None
            temp = self.path.with_suffix('.tmp')
            temp.write_text(json.dumps(saved, ensure_ascii=False), encoding='utf-8')
            temp.replace(self.path)
            self.snapshots = snapshots
            self.saved = saved
            return self.get(source)
