"""Register chats created in manager-chats by versions 1.10.0/1.10.1 with Desktop.

Dry-run by default. Close Codex Desktop before --apply: it caches global state in memory.
Only adds Desktop projectless membership/path hints; preserves cwd, history and working files.
"""
import argparse
from contextlib import closing
from datetime import datetime, timezone
import json
import ntpath
import os
from pathlib import Path
import sqlite3
import subprocess
import uuid


def normalized(path):
    if path.startswith("\\\\?\\"):
        path = path[4:]
    return ntpath.normcase(ntpath.normpath(path))


def prepare(state, threads, legacy_root):
    result = json.loads(json.dumps(state))
    for key, kind in (("projectless-thread-ids", list), ("thread-workspace-root-hints", dict),
                      ("thread-projectless-output-directories", dict), ("thread-project-assignments", dict)):
        if key in result and not isinstance(result[key], kind):
            raise ValueError("Unexpected Desktop metadata: " + key)
    ids = result.setdefault("projectless-thread-ids", [])
    roots = result.setdefault("thread-workspace-root-hints", {})
    outputs = result.setdefault("thread-projectless-output-directories", {})
    assignments = result.get("thread-project-assignments", {})
    changed = []
    for row in threads:
        thread_id, cwd, project_id = row
        uuid.UUID(thread_id)
        if not normalized(cwd).startswith(normalized(legacy_root) + "\\") or project_id or assignments.get(thread_id):
            continue
        path = cwd[4:] if cwd.startswith("\\\\?\\") else cwd
        if thread_id in ids and thread_id in roots and thread_id in outputs:
            continue
        if thread_id not in ids:
            ids.append(thread_id)
        # Existing files stay in their original location; do not invent a new outputs folder.
        roots.setdefault(thread_id, path)
        outputs.setdefault(thread_id, path)
        changed.append(thread_id)
    return result, changed


def require_desktop_closed():
    check = subprocess.run([
        "powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
        "$ErrorActionPreference = 'Stop'; $p = Get-Process | "
        "Where-Object { $_.ProcessName -eq 'ChatGPT' -or "
        "($_.ProcessName -eq 'Codex' -and $_.Path -like '*WindowsApps*') }; "
        "if ($p) { exit 9 }; exit 0"
    ], creationflags=subprocess.CREATE_NO_WINDOW, capture_output=True)
    if check.returncode != 0:
        raise RuntimeError("Close Codex Desktop before applying. Its cached state can overwrite this repair.")


def repair(home, apply=False):
    state_path = home / ".codex-global-state.json"
    if state_path.is_symlink() or state_path.is_junction():
        raise RuntimeError("Desktop state must be a regular local file")
    before = state_path.read_bytes()
    state = json.loads(before)
    if not isinstance(state, dict):
        raise ValueError("Desktop state must be an object")
    with closing(sqlite3.connect((home / "state_5.sqlite").resolve().as_uri() + "?mode=ro", uri=True)) as db:
        rows = db.execute("SELECT id,cwd,project_id FROM threads WHERE archived=0 AND cwd LIKE '%manager-chats%'").fetchall()
    after, changed = prepare(state, rows, str(home / "manager-chats"))
    if apply and changed:
        require_desktop_closed()
        if state_path.read_bytes() != before:
            raise RuntimeError("Desktop state changed; retry after closing Desktop")
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S")
        backup = home / ("manager-projectless-repair-" + stamp + "-" + uuid.uuid4().hex + ".json.bak")
        with backup.open("xb") as stream:
            stream.write(before)
        temporary = home / (".manager-projectless-" + uuid.uuid4().hex + ".tmp")
        try:
            with temporary.open("xb") as stream:
                stream.write(json.dumps(after, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
                stream.flush()
                os.fsync(stream.fileno())
            require_desktop_closed()
            if state_path.read_bytes() != before:
                raise RuntimeError("Desktop state changed; nothing replaced")
            os.replace(temporary, state_path)
        finally:
            temporary.unlink(missing_ok=True)
    return {"apply": apply, "threadIds": changed, "preserved": "cwd, working files, SQLite and conversation history"}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--home", type=Path, required=True)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    print(json.dumps(repair(args.home, args.apply), indent=2))
