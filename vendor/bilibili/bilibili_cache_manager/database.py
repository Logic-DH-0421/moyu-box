from __future__ import annotations

import sqlite3
import re
from datetime import datetime, timedelta
from difflib import SequenceMatcher
from pathlib import Path
from typing import Any, Iterable

from .config_store import app_data_dir


DB_PATH = app_data_dir() / "videos.db"
SUBMISSION_SYNC_TIMEOUT_SECONDS = 45
AUTHORITATIVE_CACHE_STATUSES = ("已完成", "正在缓存", "已暂停", "已提交")


def parse_display_count(value: Any) -> int:
    """Convert B站 display counters (for example ``7万``) into database integers."""
    if isinstance(value, bool):
        return int(value)
    if isinstance(value, (int, float)):
        return max(0, int(value))
    text = str(value or "").strip().replace(",", "").replace("，", "")
    if not text:
        return 0
    match = re.fullmatch(r"([0-9]+(?:\.[0-9]+)?)\s*([万亿wWkKmMbB]?)", text)
    if not match:
        return 0
    number = float(match.group(1))
    multiplier = {"万": 10_000, "亿": 100_000_000, "w": 10_000, "W": 10_000,
                  "k": 1_000, "K": 1_000, "m": 1_000_000, "M": 1_000_000,
                  "b": 1_000_000_000, "B": 1_000_000_000}.get(match.group(2), 1)
    return max(0, int(number * multiplier))


class ClosingConnection(sqlite3.Connection):
    def __exit__(self, exc_type: object, exc_value: object, traceback: object) -> bool:
        try:
            return bool(super().__exit__(exc_type, exc_value, traceback))
        finally:
            self.close()


def _connect() -> sqlite3.Connection:
    conn = sqlite3.connect(DB_PATH, timeout=20, factory=ClosingConnection)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA busy_timeout=20000")
    return conn


def init_db() -> None:
    with _connect() as conn:
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS videos (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                stable_key TEXT NOT NULL UNIQUE,
                bvid TEXT NOT NULL DEFAULT '',
                aid INTEGER NOT NULL DEFAULT 0,
                cid INTEGER NOT NULL DEFAULT 0,
                biz_id TEXT NOT NULL DEFAULT '',
                title TEXT NOT NULL DEFAULT '',
                uploader TEXT NOT NULL DEFAULT '',
                duration_seconds INTEGER NOT NULL DEFAULT 0,
                duration_text TEXT NOT NULL DEFAULT '',
                part_count INTEGER NOT NULL DEFAULT 1,
                queue_index INTEGER NOT NULL DEFAULT 0,
                in_queue INTEGER NOT NULL DEFAULT 1,
                selected INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT '未缓存',
                target_quality TEXT NOT NULL DEFAULT '1080P',
                actual_quality TEXT NOT NULL DEFAULT '',
                progress INTEGER NOT NULL DEFAULT 0,
                error TEXT NOT NULL DEFAULT '',
                source_hint TEXT NOT NULL DEFAULT '',
                view_count INTEGER NOT NULL DEFAULT 0,
                danmaku_count INTEGER NOT NULL DEFAULT 0,
                cache_size_bytes INTEGER NOT NULL DEFAULT 0,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
            """
        )
        existing_columns = {str(row[1]) for row in conn.execute("PRAGMA table_info(videos)")}
        for column, declaration in (
            ("aid", "INTEGER NOT NULL DEFAULT 0"),
            ("cid", "INTEGER NOT NULL DEFAULT 0"),
            ("biz_id", "TEXT NOT NULL DEFAULT ''"),
            ("view_count", "INTEGER NOT NULL DEFAULT 0"),
            ("danmaku_count", "INTEGER NOT NULL DEFAULT 0"),
            ("cache_size_bytes", "INTEGER NOT NULL DEFAULT 0"),
        ):
            if column not in existing_columns:
                conn.execute(f"ALTER TABLE videos ADD COLUMN {column} {declaration}")
        conn.execute("CREATE INDEX IF NOT EXISTS idx_videos_queue ON videos(in_queue, queue_index)")
        conn.execute("CREATE INDEX IF NOT EXISTS idx_videos_status ON videos(status)")
        conn.execute("CREATE INDEX IF NOT EXISTS idx_videos_bvid ON videos(bvid)")
        conn.execute(
            "UPDATE videos SET status='已暂停', error='上次运行意外中断' "
            "WHERE status IN ('正在打开', '等待客户端')"
        )
        conn.execute(
            "UPDATE videos SET status='已提交', progress=5, error='等待同步B站客户端真实状态' "
            "WHERE status='已完成' AND error='已提交到官方离线缓存'"
        )


def sync_queue(items: Iterable[dict[str, Any]], *, cache_all: bool, quality: str) -> int:
    now = datetime.now().isoformat(timespec="seconds")
    rows = list(items)
    seen: set[str] = set()
    with _connect() as conn:
        conn.execute("UPDATE videos SET in_queue=0")
        for position, item in enumerate(rows, start=1):
            stable_key = str(item.get("stable_key") or item.get("bvid") or f"queue:{position}:{item.get('title', '')}")
            seen.add(stable_key)
            conn.execute(
                """
                INSERT INTO videos (
                    stable_key, bvid, aid, cid, biz_id, title, uploader, duration_seconds, duration_text,
                    part_count, queue_index, in_queue, selected, status,
                    target_quality, source_hint, view_count, danmaku_count, first_seen, last_seen, updated_at
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, '未缓存', ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(stable_key) DO UPDATE SET
                    bvid=excluded.bvid,
                    aid=CASE WHEN excluded.aid!=0 THEN excluded.aid ELSE videos.aid END,
                    cid=CASE WHEN excluded.cid!=0 THEN excluded.cid ELSE videos.cid END,
                    biz_id=CASE WHEN excluded.biz_id!='' THEN excluded.biz_id ELSE videos.biz_id END,
                    title=excluded.title,
                    uploader=excluded.uploader,
                    duration_seconds=excluded.duration_seconds,
                    duration_text=excluded.duration_text,
                    part_count=excluded.part_count,
                    queue_index=excluded.queue_index,
                    in_queue=1,
                    selected=CASE WHEN ? THEN 1 ELSE videos.selected END,
                    target_quality=CASE WHEN videos.status='已完成' THEN videos.target_quality ELSE excluded.target_quality END,
                    source_hint=excluded.source_hint,
                    view_count=excluded.view_count,
                    danmaku_count=excluded.danmaku_count,
                    last_seen=excluded.last_seen,
                    updated_at=videos.updated_at
                """,
                (
                    stable_key,
                    str(item.get("bvid", "")),
                    int(item.get("aid", 0) or 0),
                    int(item.get("cid", 0) or 0),
                    str(item.get("biz_id", "")),
                    str(item.get("title", "")).strip(),
                    str(item.get("uploader", "")).strip(),
                    int(item.get("duration_seconds", 0) or 0),
                    str(item.get("duration_text", "")),
                    int(item.get("part_count", 1) or 1),
                    position,
                    1 if cache_all else 0,
                    quality,
                    str(item.get("source_hint", "")),
                    parse_display_count(item.get("view_count", item.get("views", 0))),
                    parse_display_count(item.get("danmaku_count", item.get("danmaku", 0))),
                    now,
                    now,
                    now,
                    1 if cache_all else 0,
                ),
            )
    return len(seen)


def query_videos(filters: dict[str, Any] | None = None) -> list[sqlite3.Row]:
    filters = filters or {}
    view = str(filters.get("view", "queue"))
    if view == "downloading":
        # The official client's "正在缓存" tab also contains tasks that have
        # entered the queue but are still waiting for an available download
        # slot.  Those are represented locally as "已提交" and must remain
        # visible alongside actively downloading and paused tasks.
        clauses = ["status IN ('正在缓存','已暂停','已提交')"]
    elif view == "completed":
        clauses = ["status='已完成'"]
    else:
        clauses = ["in_queue=1"]
    args: list[Any] = []
    keyword = str(filters.get("keyword", "")).strip()
    if keyword:
        clauses.append("(title LIKE ? OR uploader LIKE ? OR bvid LIKE ?)")
        term = f"%{keyword}%"
        args.extend([term, term, term])
    uploader = str(filters.get("uploader", "")).strip()
    if uploader:
        clauses.append("uploader LIKE ?")
        args.append(f"%{uploader}%")
    status = str(filters.get("status", "")).strip()
    if status and status != "全部":
        clauses.append("status=?")
        args.append(status)
    quality = str(filters.get("quality", "")).strip()
    if quality:
        clauses.append("(actual_quality=? OR (actual_quality='' AND target_quality=?))")
        args.extend([quality, quality])
    min_duration = int(filters.get("min_duration", 0) or 0)
    max_duration = int(filters.get("max_duration", 0) or 0)
    if min_duration:
        clauses.append("duration_seconds>=?")
        args.append(min_duration)
    if max_duration:
        clauses.append("duration_seconds<=?")
        args.append(max_duration)
    sql = "SELECT * FROM videos WHERE " + " AND ".join(clauses) + " ORDER BY queue_index, id"
    with _connect() as conn:
        return list(conn.execute(sql, args))


def videos_by_ids(ids: Iterable[int]) -> list[sqlite3.Row]:
    values = [int(value) for value in ids]
    if not values:
        return []
    placeholders = ",".join("?" for _ in values)
    with _connect() as conn:
        rows = list(conn.execute(f"SELECT * FROM videos WHERE id IN ({placeholders})", values))
    order = {value: index for index, value in enumerate(values)}
    return sorted(rows, key=lambda row: order.get(int(row["id"]), len(order)))


def set_selected(ids: Iterable[int], selected: bool) -> None:
    values = [int(value) for value in ids]
    if not values:
        return
    placeholders = ",".join("?" for _ in values)
    with _connect() as conn:
        conn.execute(f"UPDATE videos SET selected=?, updated_at=? WHERE id IN ({placeholders})", [1 if selected else 0, datetime.now().isoformat(timespec="seconds"), *values])


def set_all_selected(selected: bool, *, only_ids: Iterable[int] | None = None) -> None:
    now = datetime.now().isoformat(timespec="seconds")
    with _connect() as conn:
        if only_ids is None:
            conn.execute("UPDATE videos SET selected=?, updated_at=? WHERE in_queue=1", (1 if selected else 0, now))
            return
        ids = [int(value) for value in only_ids]
        if not ids:
            return
        placeholders = ",".join("?" for _ in ids)
        conn.execute(f"UPDATE videos SET selected=?, updated_at=? WHERE id IN ({placeholders})", [1 if selected else 0, now, *ids])


def invert_selected(ids: Iterable[int]) -> None:
    values = [int(value) for value in ids]
    if not values:
        return
    placeholders = ",".join("?" for _ in values)
    with _connect() as conn:
        conn.execute(f"UPDATE videos SET selected=CASE selected WHEN 1 THEN 0 ELSE 1 END, updated_at=? WHERE id IN ({placeholders})", [datetime.now().isoformat(timespec="seconds"), *values])


def selected_videos(*, include_failed: bool = True) -> list[sqlite3.Row]:
    clauses = ["in_queue=1", "selected=1", "status NOT IN ('已完成','正在缓存')"]
    if not include_failed:
        clauses.append("status NOT IN ('失败', '不支持缓存', '需充电')")
    with _connect() as conn:
        return list(conn.execute("SELECT * FROM videos WHERE " + " AND ".join(clauses) + " ORDER BY queue_index, id"))


def update_status(video_id: int, status: str, progress: int, error: str = "", *, actual_quality: str | None = None) -> None:
    updates = ["status=?", "progress=?", "error=?", "updated_at=?"]
    args: list[Any] = [status, max(0, min(100, int(progress))), error, datetime.now().isoformat(timespec="seconds")]
    if actual_quality is not None:
        updates.append("actual_quality=?")
        args.append(actual_quality)
    if status == "已提交":
        updates.append("selected=0")
    args.append(int(video_id))
    with _connect() as conn:
        conn.execute(f"UPDATE videos SET {', '.join(updates)} WHERE id=?", args)


def update_quality_for_pending(quality: str) -> None:
    with _connect() as conn:
        conn.execute(
            "UPDATE videos SET target_quality=?, updated_at=? WHERE in_queue=1 AND status!='已完成'",
            (quality, datetime.now().isoformat(timespec="seconds")),
        )


def feed_video_states(bvids: Iterable[str] | None = None) -> dict[str, str]:
    if bvids is not None:
        keys = list(dict.fromkeys(bvids))
        if not keys:
            return {}
        result = {}
        with _connect() as conn:
            for offset in range(0, len(keys), 900):
                batch = keys[offset:offset + 900]
                placeholders = ','.join('?' for _ in batch)
                result.update((row['bvid'], row['status']) for row in conn.execute(
                    f"SELECT bvid,status FROM videos WHERE bvid IN ({placeholders}) ORDER BY id", batch))
        return result
    with _connect() as conn:
        return {row['bvid']: row['status'] for row in conn.execute("SELECT bvid,status FROM videos WHERE bvid!='' ORDER BY id")}


def feed_video_membership() -> dict[str, bool]:
    with _connect() as conn:
        return {row['bvid']: bool(row['in_queue']) for row in conn.execute("SELECT bvid,in_queue FROM videos WHERE bvid!='' ORDER BY id")}


def save_feed_video(item: dict[str, Any], *, in_queue: bool, quality: str) -> dict[str, Any]:
    """Persist one feed action without resetting other queue rows or cache states."""
    now = datetime.now().isoformat(timespec='seconds')
    with _connect() as conn:
        existing = conn.execute('SELECT * FROM videos WHERE bvid=? ORDER BY id LIMIT 1', (item['bvid'],)).fetchone()
        if existing:
            conn.execute(
                'UPDATE videos SET in_queue=CASE WHEN ? THEN 1 ELSE in_queue END, view_count=?, danmaku_count=?, updated_at=? WHERE id=?',
                (int(in_queue), parse_display_count(item.get('views', item.get('view_count', 0))),
                 parse_display_count(item.get('danmaku', item.get('danmaku_count', 0))), now, existing['id']),
            )
            return dict(conn.execute('SELECT * FROM videos WHERE id=?', (existing['id'],)).fetchone())
        cur = conn.execute('''INSERT INTO videos
            (stable_key,bvid,aid,title,uploader,duration_seconds,duration_text,source_hint,view_count,danmaku_count,in_queue,target_quality,first_seen,last_seen,updated_at)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)''',
            (item['bvid'],item['bvid'],int(item.get('aid') or 0),item['title'],item.get('uploader',''),
             item.get('duration_seconds',0),item.get('duration_text',''),item.get('cover',''),
             parse_display_count(item.get('views', item.get('view_count', 0))),parse_display_count(item.get('danmaku', item.get('danmaku_count', 0))),
             int(in_queue),quality,now,now,now))
        return dict(conn.execute('SELECT * FROM videos WHERE id=?', (cur.lastrowid,)).fetchone())


def upsert_previewed_video(item: dict[str, Any]) -> dict[str, Any]:
    """Add a single video fetched from a one-off preview into the watch-later queue.

    Uses ``INSERT ... ON CONFLICT(stable_key) DO UPDATE`` so repeated pastes of
    the same link don't create duplicate rows. The returned dict contains the
    final ``id`` and whether the row was newly inserted; the caller (WebApi
    layer) is expected to flip ``selected=1`` afterward.
    """
    from .bilibili_client import BilibiliClient  # local import to avoid cycles

    normalized = BilibiliClient.normalize_preview_item(item)
    stable_key = normalized["stable_key"]
    now = datetime.now().isoformat(timespec="seconds")
    with _connect() as conn:
        conn.execute("UPDATE videos SET in_queue=0")
        existing = conn.execute("SELECT id FROM videos WHERE stable_key=?", (stable_key,)).fetchone()
        if existing is not None:
            row_id = int(existing[0])
            conn.execute(
                """
                UPDATE videos SET
                    bvid=?, aid=?, cid=?, biz_id=?, title=?, uploader=?,
                    duration_seconds=?, duration_text=?, part_count=?,
                    source_hint=?, in_queue=1, selected=1, status='未缓存',
                    last_seen=?, updated_at=?
                WHERE id=?
                """,
                (
                    normalized["bvid"],
                    normalized["aid"],
                    normalized["cid"],
                    normalized["biz_id"],
                    normalized["title"],
                    normalized["uploader"],
                    normalized["duration_seconds"],
                    normalized["duration_text"],
                    normalized["part_count"],
                    normalized["source_hint"],
                    now,
                    now,
                    row_id,
                ),
            )
            return {"id": row_id, "created": False, "stable_key": stable_key}
        cur = conn.execute(
            """
            INSERT INTO videos (
                stable_key, bvid, aid, cid, biz_id, title, uploader, duration_seconds, duration_text,
                part_count, queue_index, in_queue, selected, status,
                target_quality, source_hint, first_seen, last_seen, updated_at
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, 1, 1, '未缓存', ?, ?, ?, ?, ?)
            """,
            (
                stable_key,
                normalized["bvid"],
                normalized["aid"],
                normalized["cid"],
                normalized["biz_id"],
                normalized["title"],
                normalized["uploader"],
                normalized["duration_seconds"],
                normalized["duration_text"],
                normalized["part_count"],
                "1080P",
                normalized["source_hint"],
                now,
                now,
                now,
            ),
        )
        return {"id": int(cur.lastrowid), "created": True, "stable_key": stable_key}


def normalize_video_title(value: str) -> str:
    """Normalize decorative title differences while preserving Chinese and alphanumerics."""
    return "".join(re.findall(r"[\w\u3400-\u9fff]+", value.casefold(), flags=re.UNICODE))


def title_match_score(queue_title: str, cache_title: str) -> float:
    queue_key = normalize_video_title(queue_title)
    cache_key = normalize_video_title(cache_title)
    if not queue_key or not cache_key:
        return 0.0
    if queue_key == cache_key:
        return 100.0
    shorter, longer = sorted((queue_key, cache_key), key=len)
    if len(shorter) >= 7 and shorter in longer:
        return 85.0 + min(10.0, len(shorter) / max(1, len(longer)) * 10.0)
    ratio = SequenceMatcher(None, queue_key, cache_key).ratio()
    if min(len(queue_key), len(cache_key)) >= 8 and ratio >= 0.72:
        return ratio * 80.0
    return 0.0


def _best_cache_row(
    rows: list[sqlite3.Row],
    state: dict[str, Any],
    used_ids: set[int],
) -> sqlite3.Row | None:
    cache_title = str(state.get("title", "")).strip()
    cache_uploader = normalize_video_title(str(state.get("uploader", "")))
    best: sqlite3.Row | None = None
    best_score = 0.0
    for row in rows:
        row_id = int(row["id"])
        if row_id in used_ids:
            continue
        identity_match = False
        incompatible = False
        for field in ("bvid", "aid", "cid"):
            left, right = str(row[field] or ""), str(state.get(field) or "")
            if left and right and left != "0" and right != "0":
                if left != right:
                    incompatible = True
                else:
                    identity_match = True
        if incompatible:
            continue
        if identity_match:
            return row
        # Identified client records must not fall back to a similar title on a
        # queue row without IDs: that could mark an uncached collection episode.
        if any(str(state.get(field) or '').strip('0') for field in ('bvid', 'aid', 'cid')):
            continue
        score = title_match_score(str(row["title"]), cache_title)
        if score <= 0:
            continue
        row_uploader = normalize_video_title(str(row["uploader"]))
        if cache_uploader and row_uploader and cache_uploader == row_uploader:
            score += 5.0
        if score > best_score:
            best = row
            best_score = score
    return best


def sync_cache_states(states: Iterable[dict[str, Any]]) -> int:
    """Make the official completed/downloading pages authoritative for queue status."""
    now = datetime.now().isoformat(timespec="seconds")
    official_states = [dict(state) for state in states if str(state.get("title", "")).strip()]
    changed_ids: set[int] = set()
    with _connect() as conn:
        # Every item outside the current watch-later queue is rebuilt from the
        # official downloading/completed tabs. This also removes items that once
        # had a BV queue key and would otherwise survive forever as stale rows.
        submission_cutoff = (datetime.now() - timedelta(seconds=SUBMISSION_SYNC_TIMEOUT_SECONDS)).isoformat(timespec='seconds')
        conn.execute("DELETE FROM videos WHERE in_queue=0 AND NOT(status='已提交' AND updated_at>=?)", (submission_cutoff,))
        conn.execute(
            "UPDATE videos SET status='已提交', progress=5, error='等待同步B站客户端真实状态', updated_at=? "
            "WHERE status='已完成' AND error='已提交到官方离线缓存'",
            (now,),
        )
        rows = list(
            conn.execute(
                "SELECT id,title,uploader,bvid,aid,cid,status,updated_at FROM videos WHERE in_queue=1 OR status='已提交' ORDER BY queue_index,id"
            )
        )

        authoritative_placeholders = ",".join("?" for _ in AUTHORITATIVE_CACHE_STATUSES)
        stale_rows = list(
            conn.execute(
                f"SELECT id FROM videos WHERE in_queue=1 AND status IN ({authoritative_placeholders})",
                AUTHORITATIVE_CACHE_STATUSES,
            )
        )
        if stale_rows:
            stale_ids = [int(row["id"]) for row in stale_rows]
            placeholders = ",".join("?" for _ in stale_ids)
            conn.execute(
                f"UPDATE videos SET status='未缓存', progress=0, actual_quality='', "
                f"error='B站客户端当前缓存列表中不存在', updated_at=? WHERE id IN ({placeholders})",
                [now, *stale_ids],
            )
            changed_ids.update(stale_ids)

        cutoff = (datetime.now() - timedelta(seconds=SUBMISSION_SYNC_TIMEOUT_SECONDS)).isoformat(timespec="seconds")
        expired_submissions = list(
            conn.execute(
                "SELECT id FROM videos WHERE in_queue=1 AND status='已提交' AND updated_at<=?",
                (cutoff,),
            )
        )
        if expired_submissions:
            expired_ids = [int(row["id"]) for row in expired_submissions]
            placeholders = ",".join("?" for _ in expired_ids)
            conn.execute(
                f"UPDATE videos SET status='未缓存', progress=0, actual_quality='', selected=0, "
                f"error='未出现在B站客户端当前缓存列表，可重新选择缓存', updated_at=? "
                f"WHERE id IN ({placeholders})",
                [now, *expired_ids],
            )
            changed_ids.update(expired_ids)

        used_ids: set[int] = set()
        for state in official_states:
            row = _best_cache_row(rows, state, used_ids)
            if row is None:
                title = str(state.get("title", "")).strip()
                uploader = str(state.get("uploader", "")).strip()
                stable_key = f"offline:{normalize_video_title(title)}|{normalize_video_title(uploader)}"
                identity = state.get("item_id") or ":".join(str(state.get(field) or "") for field in ("bvid", "aid", "cid")).strip(":")
                if identity:
                    stable_key = f"offline:id:{identity}"
                conn.execute(
                    """
                    INSERT INTO videos (
                        stable_key, title, uploader, queue_index, in_queue, selected, status,
                        progress, cache_size_bytes, error, first_seen, last_seen, updated_at
                    ) VALUES (?, ?, ?, 0, 0, 0, ?, ?, ?, ?, ?, ?, ?)
                    ON CONFLICT(stable_key) DO UPDATE SET
                        title=excluded.title, uploader=excluded.uploader, in_queue=0,
                        selected=0, status=excluded.status, progress=excluded.progress,
                        cache_size_bytes=excluded.cache_size_bytes, error=excluded.error,
                        last_seen=excluded.last_seen, updated_at=excluded.updated_at
                    """,
                    (
                        stable_key,
                        title,
                        uploader,
                        str(state.get("status", "未缓存")),
                        max(0, min(100, int(state.get("progress", 0) or 0))),
                        max(0, int(state.get("cache_size_bytes", 0) or 0)),
                        str(state.get("message", "")),
                        now,
                        now,
                        now,
                    ),
                )
                inserted = conn.execute("SELECT id FROM videos WHERE stable_key=?", (stable_key,)).fetchone()
                if inserted is not None:
                    seconds = max(0, int(state.get('duration_seconds') or 0))
                    duration = f'{seconds // 60}:{seconds % 60:02d}' if seconds else ''
                    conn.execute(
                        "UPDATE videos SET bvid=?,aid=?,cid=?,source_hint=?,duration_seconds=?,duration_text=?,cache_size_bytes=? WHERE id=?",
                        (str(state.get('bvid') or ''), str(state.get('aid') or ''), str(state.get('cid') or ''),
                         str(state.get('source_hint') or ''), seconds, duration, max(0, int(state.get('cache_size_bytes') or 0)), int(inserted[0])),
                    )
                    changed_ids.add(int(inserted[0]))
                continue
            row_id = int(row["id"])
            used_ids.add(row_id)
            conn.execute(
                "UPDATE videos SET status=?, progress=?, cache_size_bytes=?, error=?, selected=0, updated_at=? WHERE id=?",
                (
                    str(state.get("status", "未缓存")),
                    max(0, min(100, int(state.get("progress", 0) or 0))),
                    max(0, int(state.get("cache_size_bytes", 0) or 0)),
                    str(state.get("message", "")),
                    now,
                    row_id,
                ),
            )
            changed_ids.add(row_id)
    return len(changed_ids)


def counts() -> dict[str, int]:
    with _connect() as conn:
        total = int(conn.execute("SELECT COUNT(*) FROM videos WHERE in_queue=1").fetchone()[0])
        selected = int(conn.execute("SELECT COUNT(*) FROM videos WHERE in_queue=1 AND selected=1").fetchone()[0])
        completed = int(conn.execute("SELECT COUNT(*) FROM videos WHERE status='已完成'").fetchone()[0])
        active = int(
            conn.execute(
                "SELECT COUNT(*) FROM videos WHERE status IN ('正在缓存','已暂停','已提交')"
            ).fetchone()[0]
        )
        paused = int(conn.execute("SELECT COUNT(*) FROM videos WHERE status='已暂停'").fetchone()[0])
        failed = int(conn.execute("SELECT COUNT(*) FROM videos WHERE in_queue=1 AND status IN ('失败','不支持缓存','需充电')").fetchone()[0])
    return {"total": total, "selected": selected, "active": active, "paused": paused, "completed": completed, "failed": failed}


def reset_failed_selected() -> int:
    with _connect() as conn:
        cur = conn.execute(
            "UPDATE videos SET status='未缓存', progress=0, error='', selected=1, updated_at=? "
            "WHERE in_queue=1 AND status IN ('失败','不支持缓存','需充电','已暂停')",
            (datetime.now().isoformat(timespec="seconds"),),
        )
        return cur.rowcount


def database_path() -> Path:
    return Path(DB_PATH)
