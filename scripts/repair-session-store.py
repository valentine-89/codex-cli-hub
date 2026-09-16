"""One-time, explicit maintenance for the Codex 0.154 state schema.

Dry-run by default. Preserve source databases and conversation content. Import CLI-only
threads (including paginated history) without overwriting Desktop threads.
Repair only nonexistent C:\\mnt\\<drive> paths whose real drive directory exists.
Run with Python 3: repair-session-store.py --home PATH --profiles PATH [--apply]
"""
from contextlib import contextmanager
import argparse
import json
import os
from pathlib import Path
import re
import sqlite3
import uuid


@contextmanager
def database(path, read_only=False):
    connection = sqlite3.connect(path.resolve().as_uri() + ("?mode=ro" if read_only else "?mode=rw"), uri=True, timeout=15)
    try:
        with connection:
            yield connection
    finally:
        connection.close()


def connect_read(path):
    return database(path, read_only=True)


def columns(db, table, schema="main"):
    return [row[1] for row in db.execute(f'PRAGMA {schema}.table_info("{table}")')]


def repaired_cwd(cwd):
    match = re.fullmatch(r"C:\\mnt\\([a-zA-Z])\\(.+)", cwd, re.IGNORECASE)
    if match and not Path(cwd).exists():
        target = Path(match[1].upper() + ":\\" + match[2])
        if target.is_dir():
            return str(target)
    return None


def repair_rollout_cwd(path, current, apply=False):
    """Repair metadata only, padding JSON whitespace to retain history byte offsets."""
    path = Path(path)
    if not Path(current).is_dir():
        raise RuntimeError("Repair destination does not exist")
    legacy = "/mnt/" + current[0].lower() + current[2:].replace("\\", "/")
    broken = "C:\\mnt\\" + current[0].lower() + current[2:]
    before = path.read_bytes()
    stat = path.stat()
    lines = []
    count = 0
    for line in before.splitlines(keepends=True):
        item = json.loads(line)
        payload = item.get("payload", {})
        if item.get("type") in ("session_meta", "turn_context") and payload.get("cwd") in (legacy, broken):
            previous = payload["cwd"]
            token = json.dumps(current, ensure_ascii=False).encode("utf-8")
            for match in re.finditer(rb'"cwd"\s*:\s*("(?:[^"\\]|\\.)*")', line):
                if json.loads(match[1]) != previous:
                    continue
                if len(token) > len(match[1]):
                    raise RuntimeError("Path repair would change history offsets")
                updated = line[:match.start(1)] + token + b" " * (len(match[1]) - len(token)) + line[match.end(1):]
                payload["cwd"] = current
                if json.loads(updated) != item:
                    raise RuntimeError("Unexpected non-metadata change")
                line = updated
                count += 1
                break
            else:
                raise RuntimeError("Cannot locate cwd token")
        lines.append(line)
    after = b"".join(lines)
    if len(after) != len(before):
        raise RuntimeError("History byte offsets changed")
    if apply and count:
        temporary = path.with_name(".cwd-repair-" + uuid.uuid4().hex + ".tmp")
        try:
            temporary.write_bytes(after)
            if path.read_bytes() != before:
                raise RuntimeError("Rollout changed concurrently; repair aborted")
            os.replace(temporary, path)
            os.utime(path, ns=(stat.st_atime_ns, stat.st_mtime_ns))
        finally:
            temporary.unlink(missing_ok=True)
    return count


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--home", type=Path, required=True)
    parser.add_argument("--profiles", type=Path, required=True)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    state_path = args.home / "state_5.sqlite"
    history_path = args.home / "thread_history_1.sqlite"
    if not state_path.is_file() or not history_path.is_file():
        raise RuntimeError("Expected current Codex state and history databases; nothing changed")
    plans = []
    with connect_read(state_path) as target:
        target_ids = {r[0] for r in target.execute("SELECT id FROM threads")}
        for profile in sorted(args.profiles.iterdir()):
            source_path = profile / "state_5.sqlite"
            if not source_path.is_file():
                continue
            with connect_read(source_path) as source:
                source.row_factory = sqlite3.Row
                rows = [dict(r) for r in source.execute("SELECT * FROM threads WHERE source='cli'")
                        if r["id"] not in target_ids]
                for row in rows:
                    if not Path(row["rollout_path"]).is_file():
                        raise RuntimeError("Missing rollout for " + row["id"])
                    if row.get("project_id") or row.get("thread_section_id"):
                        raise RuntimeError("Unmapped project/section for " + row["id"])
                    if columns(source, "threads") != columns(target, "threads"):
                        raise RuntimeError("State schema differs; explicit migration required")
                    plans.append((profile, row))
                    target_ids.add(row["id"])
        repairs = [(r[0], r[1], repaired_cwd(r[1]), r[2]) for r in target.execute("SELECT id,cwd,rollout_path FROM threads")
                   if repaired_cwd(r[1])]
    print(json.dumps({"importCliThreads": [r["id"] for _, r in plans], "repairCwd": repairs,
                      "apply": args.apply}, indent=2))
    # Preflight every table before any write. Never copy migration records or
    # resurrect old Desktop entries that may intentionally be archived/deleted.
    history_tables = ("thread_turns", "thread_items", "thread_history_projection_state", "thread_realtime_items")
    prepared = []
    with connect_read(history_path) as target_history:
        for profile, row in plans:
            tables = {}
            with connect_read(profile / "thread_history_1.sqlite") as source:
                for table in history_tables:
                    cols = columns(source, table)
                    if cols != columns(target_history, table):
                        raise RuntimeError("History schema differs; explicit migration required")
                    tables[table] = (cols, source.execute(f'SELECT * FROM "{table}" WHERE thread_id=?', (row["id"],)).fetchall())
            with connect_read(profile / "state_5.sqlite") as source:
                cols = columns(source, "thread_dynamic_tools")
                with connect_read(state_path) as target:
                    if cols != columns(target, "thread_dynamic_tools"):
                        raise RuntimeError("Dynamic tool schema differs; explicit migration required")
                tools = (cols, source.execute('SELECT * FROM thread_dynamic_tools WHERE thread_id=?', (row["id"],)).fetchall())
            prepared.append((row, tables, tools))
    if not args.apply:
        return
    for _, _, current, rollout in repairs:
        repair_rollout_cwd(rollout, current, apply=True)
    # History is copied first; an interrupted run is safe to repeat. Existing
    # thread IDs always win and source databases remain untouched.
    with database(history_path) as history:
        for row, tables, _ in prepared:
            for table, (cols, rows) in tables.items():
                names = ','.join('"' + c + '"' for c in cols)
                marks = ','.join('?' for _ in cols)
                history.executemany(f'INSERT OR IGNORE INTO "{table}" ({names}) VALUES ({marks})', rows)
    with database(state_path) as target:
        target.execute("PRAGMA foreign_keys=ON")
        for row, _, (cols, rows) in prepared:
            names = ','.join('"' + c + '"' for c in row)
            marks = ','.join('?' for _ in row)
            target.execute(f'INSERT OR IGNORE INTO threads ({names}) VALUES ({marks})', tuple(row.values()))
            names = ','.join('"' + c + '"' for c in cols)
            marks = ','.join('?' for _ in cols)
            target.executemany(f'INSERT OR IGNORE INTO thread_dynamic_tools ({names}) VALUES ({marks})', rows)
        for thread_id, previous, current, _ in repairs:
            target.execute("UPDATE threads SET cwd=? WHERE id=? AND cwd=?", (current, thread_id, previous))
    print("Applied. Original profile databases and conversation content preserved.")


if __name__ == "__main__":
    main()
