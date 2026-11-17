from __future__ import annotations

import json
import ntpath
import os
import sys
import tempfile
import threading
from dataclasses import asdict, dataclass, fields, field
from pathlib import Path


APP_DIR_NAME = "BilibiliCacheManager"
CONFIG_LOCK = threading.RLock()


def app_data_dir() -> Path:
    override = os.environ.get("MOYU_PROJECT_DATA")
    if override:
        target = Path(override).resolve()
        target.mkdir(parents=True, exist_ok=True)
        return target
    if getattr(sys, "frozen", False):
        base = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local"))
        target = base / APP_DIR_NAME
    else:
        target = Path(__file__).resolve().parents[1] / "data"
    target.mkdir(parents=True, exist_ok=True)
    return target


@dataclass(slots=True)
class AppConfig:
    bilibili_exe: str = r"C:\Program Files\bilibili\哔哩哔哩.exe"
    cache_directory: str = ""
    quality: str = "1080P"
    cache_all: bool = False
    auto_fallback: bool = True
    keep_watch_later: bool = True
    hidden_mode: bool = False
    launch_client_on_startup: bool = False
    cdp_port: int = 19223
    action_delay_ms: int = 500
    layout_modes: dict = field(default_factory=dict)


CONFIG_PATH = app_data_dir() / "config.json"


def normalize_cache_directory(value: str) -> str:
    if not isinstance(value, str):
        raise ValueError('缓存路径必须是文件夹路径')
    value = value.strip()
    if not value:
        return ''
    drive, tail = ntpath.splitdrive(value)
    if not drive or not tail.startswith(('\\', '/')) or any(c in value for c in '<>"|?*\r\n\x00') or ':' in tail:
        raise ValueError('请输入完整的缓存文件夹路径，例如 D:\\BilibiliCache')
    return ntpath.normpath(value)


def load_config() -> AppConfig:
    config = AppConfig()
    if not CONFIG_PATH.exists():
        return config
    try:
        raw = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    except (OSError, ValueError, TypeError):
        return config
    if not isinstance(raw, dict):
        return config
    values = {}
    for field in fields(AppConfig):
        value = raw.get(field.name, getattr(config, field.name))
        default = getattr(config, field.name)
        if type(value) is not type(default):
            continue
        if field.name == 'cache_directory':
            try: value = normalize_cache_directory(value)
            except ValueError: continue
        if field.name == 'layout_modes':
            value = {key: mode for key, mode in value.items() if isinstance(key,str) and mode in ('list','grid')}
        if field.name == 'quality' and value not in ('最高可用', '4K', '1080P高码率', '1080P', '720P', '480P'):
            continue
        if field.name == 'cdp_port' and not 1 <= value <= 65535:
            continue
        if field.name == 'action_delay_ms' and not 0 <= value <= 60000:
            continue
        if field.name == 'bilibili_exe' and not value.strip():
            continue
        values[field.name] = value
    try:
        return AppConfig(**values)
    except (TypeError, ValueError):
        return config


def save_config(config: AppConfig) -> None:
    # Shared by all callers; each attempt owns its temporary file.
    with CONFIG_LOCK:
        payload = json.dumps(asdict(config), ensure_ascii=False, indent=2)
        temp = None
        try:
            with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8",
                    dir=CONFIG_PATH.parent, prefix=CONFIG_PATH.name + ".", suffix=".tmp",
                    delete=False) as handle:
                temp = Path(handle.name)
                handle.write(payload)
                handle.flush()
                os.fsync(handle.fileno())
            temp.replace(CONFIG_PATH)
        finally:
            if temp is not None:
                temp.unlink(missing_ok=True)
