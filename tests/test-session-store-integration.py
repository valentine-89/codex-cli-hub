"""Exercise installed Codex binaries against schema-only, credential-free fixtures.

No model turn is sent. All writes and profile deletions stay under a named temp tree.
"""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import queue
import sqlite3
import stat
import subprocess
import tempfile
import threading
import time
import uuid


class Rpc:
    def __init__(self, binary, home, shared):
        env = {k: v for k, v in os.environ.items()
               if not k.startswith(("CODEX_", "OPENAI_", "CHATGPT_", "AZURE_OPENAI_"))}
        env.update(CODEX_HOME=str(home), CODEX_SQLITE_HOME=str(shared))
        self.process = subprocess.Popen(
            [str(binary), "-c", "sqlite_home=" + json.dumps(str(shared)), "-c",
             'cli_auth_credentials_store="file"', "app-server", "--listen", "stdio://"],
            cwd=home, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW)
        self.messages = queue.Queue()
        self.counter = 0

        def read():
            for line in self.process.stdout:
                try:
                    self.messages.put(json.loads(line))
                except json.JSONDecodeError:
                    pass
            self.messages.put({"eof": True})

        def drain():
            for _ in self.process.stderr:
                pass

        threading.Thread(target=read, daemon=True).start()
        threading.Thread(target=drain, daemon=True).start()
        self.ok("initialize", {"clientInfo": {"name": "session_store_acceptance", "version": "1.12.2"},
                               "capabilities": {"experimentalApi": True}})
        self.process.stdin.write(json.dumps({"method": "initialized", "params": {}}) + "\n")
        self.process.stdin.flush()

    def call(self, method, params):
        self.counter += 1
        self.process.stdin.write(json.dumps({"id": self.counter, "method": method, "params": params}) + "\n")
        self.process.stdin.flush()
        deadline = time.monotonic() + 25
        while True:
            response = self.messages.get(timeout=max(.01, deadline - time.monotonic()))
            if response.get("eof"):
                raise RuntimeError("Fixture app-server closed")
            if response.get("id") == self.counter:
                return response

    def ok(self, method, params):
        response = self.call(method, params)
        assert "error" not in response, (method, response.get("error"))
        return response["result"]

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
        # Short-lived startup helpers may release their cwd just after the server.
        time.sleep(.75)


def schema_only(source, target):
    with closing(sqlite3.connect(source.as_uri() + "?mode=ro", uri=True)) as src, closing(sqlite3.connect(target)) as dst:
        for sql, in src.execute("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' "
                                "ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 ELSE 2 END"):
            dst.execute(sql)
        rows = src.execute("SELECT * FROM _sqlx_migrations").fetchall()
        dst.executemany("INSERT INTO _sqlx_migrations VALUES (" + ",".join("?" * len(rows[0])) + ")", rows)
        dst.commit()


def run(executable, *args):
    return subprocess.run([str(executable), *map(str, args)], capture_output=True, text=True,
                          encoding="utf-8", timeout=40, creationflags=subprocess.CREATE_NO_WINDOW)


def compress_rollout(path):
    from compression import zstd
    if path.exists():
        Path(str(path) + ".zst").write_bytes(zstd.compress(path.read_bytes()))
        path.unlink()


def clean(root):
    # Inventory once. Remove named files, detach junctions, then empty directories.
    files, dirs, links = [], [], []
    for base, children, names in os.walk(root, followlinks=False):
        base = Path(base)
        dirs.append(base)
        for name in list(children):
            path = base / name
            if path.is_junction() or path.is_symlink():
                links.append(path)
                children.remove(name)
        files.extend(base / name for name in names)
    for path in files:
        assert path.resolve().is_relative_to(root.resolve()), path
        for attempt in range(20):
            try:
                if path.stat().st_file_attributes & stat.FILE_ATTRIBUTE_READONLY:
                    path.chmod(stat.S_IWRITE)
                path.unlink()
                break
            except PermissionError:
                time.sleep(.2)
        else:
            raise RuntimeError("Fixture file still locked: " + str(path))
    for path in links:
        assert path.absolute().is_relative_to(root.absolute()), path
        path.rmdir()
    for path in sorted(dirs, key=lambda p: len(p.parts), reverse=True):
        for attempt in range(20):
            try:
                path.rmdir()
                break
            except PermissionError:
                time.sleep(.2)
        else:
            raise RuntimeError("Fixture directory still locked: " + str(path))


def check(binary, args):
    root = Path(tempfile.mkdtemp(prefix="CodexSessionStoreAcceptance-"))
    app, shared = root / "manager", root / "shared"
    app.mkdir()
    (app / "profiles").mkdir()
    shared.mkdir()
    (shared / "config.toml").write_text("[features]\nplugins = false\n", encoding="utf-8")
    for name in ("sessions", "archived_sessions"):
        (shared / name).mkdir()
    (app / "settings.json").write_text(json.dumps({"version": 1, "defaultCodexHome": str(shared), "workingDirectory": str(root)}))
    try:
        for name in ("state_5.sqlite", "thread_history_1.sqlite"):
            schema_only(args.schema_home / name, shared / name)
        profiles = [app / "profiles" / uuid.uuid4().hex for _ in range(2)]
        for profile in profiles:
            profile.mkdir()
            (profile / "config.toml").write_text("[features]\nplugins = false\n", encoding="utf-8")
            for name in ("sessions", "archived_sessions"):
                # Values are arguments, never interpolated shell code.
                result = run("pwsh", "-NoProfile", "-CommandWithArgs", "New-Item -ItemType Junction -Path $args[0] -Target $args[1] | Out-Null",
                             str(profile / name), str(shared / name))
                assert result.returncode == 0, result.stderr
        (app / "accounts.json").write_text(json.dumps({"version": 1, "accounts": [{
            "id": profiles[1].name, "displayName": "fixture", "profilePath": "profiles/" + profiles[1].name,
            "sharedSessions": True}]}))
        tid = str(uuid.uuid4())
        relative = Path("2026/10/13") / ("rollout-2026-10-13T00-00-00-" + tid + ".jsonl")
        canonical, alias = shared / "sessions" / relative, profiles[0] / "sessions" / relative
        canonical.parent.mkdir(parents=True)
        records = [{"timestamp": "2026-10-13T00:00:00.000Z", "ordinal": 0, "type": "session_meta", "payload": {
            "id": tid, "session_id": tid, "timestamp": "2026-10-13T00:00:00.000Z", "cwd": str(root),
            "source": "cli", "thread_source": "user", "history_mode": "paginated", "cli_version": "0.162.1",
            "originator": "codex-tui", "model_provider": "openai", "base_instructions": {"text": ""}}}]
        canonical.write_text("".join(json.dumps(r) + "\n" for r in records), encoding="utf-8")
        if args.compressed:
            compress_rollout(canonical)
        with closing(sqlite3.connect(shared / "state_5.sqlite")) as db:
            now = int(time.time())
            db.execute("INSERT INTO backfill_state(id,status,last_success_at,updated_at) VALUES(1,'complete',?,?)", (now, now))
            db.execute("INSERT INTO threads(id,rollout_path,created_at,updated_at,source,model_provider,cwd,title,sandbox_policy,approval_mode,preview,history_mode,cli_version,first_user_message) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                       (tid, str(alias), now, now, "cli", "openai", str(root), "Fixture title", '{"type":"read-only"}', "never", "Fixture preview", "paginated", "0.162.1", "Fixture message"))
            db.commit()
        params = {"limit": 100, "archived": False, "sortKey": "updated_at", "modelProviders": [],
                  "sourceKinds": ["cli", "vscode", "appServer"], "useStateDbOnly": True}
        rpc = Rpc(binary, profiles[1], shared)
        try:
            assert tid in [r["id"] for r in rpc.ok("thread/list", params)["data"]]
            rpc.ok("thread/read", {"threadId": tid, "includeTurns": False})
            # Production repair must reject even this synthetic database while a client is running.
            blocked = run(args.manager, "--root", app, "--session-store-repair")
            assert blocked.returncode != 0 and "Close Codex" in blocked.stderr, (blocked.stdout, blocked.stderr)
            blocked_delete = run(args.manager, "--root", app, "--delete-account", profiles[1].name, "--expected-account-name", "fixture")
            assert blocked_delete.returncode != 0 and "Close Codex" in blocked_delete.stderr, (blocked_delete.stdout, blocked_delete.stderr)
            wrong_name = run(args.manager, "--root", app, "--delete-account", profiles[1].name, "--expected-account-name", "wrong fixture")
            assert wrong_name.returncode != 0 and "name changed" in wrong_name.stderr
            assert profiles[1].exists() and len(json.loads((app / "accounts.json").read_text())["accounts"]) == 1
            rpc.ok("thread/resume", {"threadId": tid, "cwd": str(root), "approvalPolicy": "never", "sandbox": "read-only"})
        finally:
            rpc.close()
        # Resume may append lifecycle metadata; repair must preserve the resulting bytes.
        if args.compressed:
            compress_rollout(canonical)
        physical = Path(str(canonical) + ".zst") if args.compressed else canonical
        original = hashlib.sha256(physical.read_bytes()).hexdigest()
        # Current resume can normalize paths itself. Recreate the persisted legacy
        # alias explicitly so this also exercises chats that were never resumed.
        with closing(sqlite3.connect(shared / "state_5.sqlite")) as db:
            db.execute("UPDATE threads SET rollout_path=? WHERE id=?", (str(alias), tid))
            db.commit()
        # Reproduce the old deletion defect, leaving the shared transcript untouched.
        (profiles[0] / "sessions").rmdir()
        (profiles[0] / "archived_sessions").rmdir()
        (profiles[0] / "config.toml").unlink()
        profiles[0].rmdir()
        rpc = Rpc(binary, profiles[1], shared)
        try:
            assert tid not in [r["id"] for r in rpc.ok("thread/list", params)["data"]]
            assert rpc.call("thread/archive", {"threadId": tid})["error"]["code"] == -32600
        finally:
            rpc.close()
        audit = run(args.manager, "--root", app, "--session-store-audit")
        assert audit.returncode == 0, audit.stderr
        assert json.loads(audit.stdout)["audit"]["brokenPaths"] == 1
        repaired = run(args.test_host, "--session-store-fixture-repair", app, profiles[1].name)
        assert repaired.returncode == 0, (repaired.stdout, repaired.stderr)
        assert json.loads(repaired.stdout)["changed"] == 1
        assert not profiles[1].exists()
        assert json.loads((app / "accounts.json").read_text())["accounts"] == []
        listed = run(args.test_host, "--session-store-fixture-list", app, binary)
        assert listed.returncode == 0 and tid in json.loads(listed.stdout), (listed.stdout, listed.stderr)
        rpc = Rpc(binary, shared, shared)
        try:
            assert tid in [r["id"] for r in rpc.ok("thread/list", params)["data"]]
            rpc.ok("thread/archive", {"threadId": tid})
            rpc.ok("thread/unarchive", {"threadId": tid})
        finally:
            rpc.close()
        with closing(sqlite3.connect(shared / "state_5.sqlite")) as db:
            path, title = db.execute("SELECT rollout_path,title FROM threads WHERE id=?", (tid,)).fetchone()
        assert title == "Fixture title"
        restored = Path(path) if Path(path).exists() else Path(path + ".zst")
        assert hashlib.sha256(restored.read_bytes()).hexdigest() == original
        again = run(args.test_host, "--session-store-fixture-repair", app)
        assert again.returncode == 0 and json.loads(again.stdout)["changed"] == 0, again.stdout
        print(json.dumps({"binary": str(binary), "compressed": args.compressed, "result": "PASS", "checks": ["cross-profile read/resume without model turn", "live guard", "missing-list reproduction", "archive failure reproduction", "audit", "repair", "profile deletion", "archive/unarchive", "content hash", "idempotence"]}), flush=True)
    finally:
        clean(root)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--schema-home", type=Path, required=True)
    parser.add_argument("--manager", type=Path, required=True)
    parser.add_argument("--test-host", type=Path, required=True)
    parser.add_argument("--codex", type=Path, action="append", required=True)
    parser.add_argument("--compressed", action="store_true", help="Exercise .jsonl.zst storage; requires Python 3.14+")
    options = parser.parse_args()
    for executable in options.codex:
        check(executable, options)
