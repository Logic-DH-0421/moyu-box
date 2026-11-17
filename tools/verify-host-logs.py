"""Check recorded host operation chains, outcomes and redaction, not file existence."""
from datetime import datetime
import collections
import json
from pathlib import Path
import sys
import zipfile

root = Path(sys.argv[1])
rows = [json.loads(line) for file in (root / "logs").glob("moyu-*.jsonl")
        for line in file.read_text(encoding="utf-8-sig").splitlines()]
assert rows, "empty diagnostics"
for row in rows:
    assert datetime.fromisoformat(row["timestamp"]).tzinfo is not None
    assert all(key in row for key in ("timestamp", "level", "module", "event", "session_id", "message"))
groups = collections.defaultdict(list)
for row in rows:
    if "operation_id" in row:
        groups[row["operation_id"]].append(row)
for operation, events in groups.items():
    starts = [r for r in events if r.get("phase") == "started"]
    terminals = [r for r in events if r.get("phase") == "terminal"]
    assert len(starts) == len(terminals) == 1, (operation, [r["event"] for r in events])
    assert terminals[0]["elapsed_ms"] >= 0
    assert starts[0]["correlation_id"] == terminals[0]["correlation_id"]
    parent = starts[0].get("parent_operation_id")
    if parent in groups:
        assert starts[0]["correlation_id"] == groups[parent][0]["correlation_id"]
requests = [r for r in rows if r.get("phase") == "received"]
assert len({r["correlation_id"] for r in requests}) == len(requests), "requests share a root correlation"
assert any(r["event"] == "action.open.finished" and r.get("result") == "timeout" for r in rows)
assert any(r["event"] == "navigation.response_failed" and r.get("status") == 503 for r in rows)
assert any(r["event"] == "script.unhandled" for r in rows)
assert any(r["event"] == "settings.save.finished" and r.get("result") == "failed" for r in rows)
for marker in (root / "logs").glob("session-*.state.json"):
    state = json.loads(marker.read_text(encoding="utf-8-sig"))
    assert state["clean"] and state["active_count"] == 0, "pending operation at normal exit"
raw = "\n".join(json.dumps(r, ensure_ascii=False) for r in rows)
for secret in ("FRAME_NOTE_CANARY", "SHELL_PRIVATE_CANARY", "invalid zip private content", "private-fixture.epub", "标签页测试鱼", "X-Moyu-Token", "service-ready.json"):
    assert secret not in raw, f"sensitive field: {secret}"
with zipfile.ZipFile(root / "host-diagnostics.zip") as archive:
    assert set(archive.namelist()) == {"manifest.json", "host.jsonl"}
    manifest = json.loads(archive.read("manifest.json"))
    assert manifest["scope"] == "MoyuBox-host-only" and manifest["complete"]
    assert "SHELL_PRIVATE_CANARY" not in archive.read("host.jsonl").decode("utf-8")
report = {"ok": True, "records": len(rows), "operations": len(groups), "independent_requests": len(requests), "pending_at_exit": 0}
(root / "log-chain-validation.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(report))
