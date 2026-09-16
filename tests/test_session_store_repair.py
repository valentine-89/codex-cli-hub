from contextlib import contextmanager
import importlib.util
import json
import pathlib
import sqlite3
import subprocess
import sys
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "scripts/repair-session-store.py"


@contextmanager
def database(path):
    connection = sqlite3.connect(path)
    try:
        with connection:
            yield connection
    finally:
        connection.close()


class RepairTests(unittest.TestCase):
    def test_dry_run_import_history_idempotency_and_desktop_preservation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            home = root / "home"
            profile = root / "profiles" / "account"
            for directory in (home, profile):
                directory.mkdir(parents=True)
                with database(directory / "state_5.sqlite") as db:
                    db.execute("CREATE TABLE threads (id TEXT PRIMARY KEY, source TEXT, cwd TEXT, rollout_path TEXT, title TEXT)")
                    db.execute("CREATE TABLE thread_dynamic_tools (thread_id TEXT, name TEXT, PRIMARY KEY(thread_id,name))")
                with database(directory / "thread_history_1.sqlite") as db:
                    for table in ("thread_turns", "thread_items", "thread_history_projection_state", "thread_realtime_items"):
                        db.execute(f"CREATE TABLE {table} (thread_id TEXT PRIMARY KEY, data TEXT)")
            rollout = home / "rollout.jsonl"
            rollout.write_text("conversation-sentinel", encoding="utf-8")
            with database(home / "state_5.sqlite") as db:
                db.execute("INSERT INTO threads VALUES ('desktop','vscode',?,?,?)", (str(home), str(rollout), "newest-desktop"))
            with database(profile / "state_5.sqlite") as db:
                db.executemany("INSERT INTO threads VALUES (?,?,?,?,?)", [
                    ("desktop", "cli", str(home), str(rollout), "stale-private"),
                    ("cli", "cli", str(home), str(rollout), "private-cli"),
                    ("deleted-desktop", "vscode", str(home), str(rollout), "must-not-resurrect")])
                db.execute("INSERT INTO thread_dynamic_tools VALUES ('cli','test-tool')")
            with database(profile / "thread_history_1.sqlite") as db:
                db.execute("INSERT INTO thread_items VALUES ('cli','history-sentinel')")
            command = [sys.executable, str(SCRIPT), "--home", str(home), "--profiles", str(profile.parent)]
            subprocess.run(command, check=True, capture_output=True)
            with database(home / "state_5.sqlite") as db:
                self.assertEqual(db.execute("SELECT count(*) FROM threads").fetchone()[0], 1)
            for _ in range(2):
                subprocess.run(command + ["--apply"], check=True, capture_output=True)
            with database(home / "state_5.sqlite") as db:
                self.assertEqual(db.execute("SELECT id,title FROM threads ORDER BY id").fetchall(),
                                 [("cli", "private-cli"), ("desktop", "newest-desktop")])
                self.assertEqual(db.execute("SELECT name FROM thread_dynamic_tools").fetchone()[0], "test-tool")
            with database(home / "thread_history_1.sqlite") as db:
                self.assertEqual(db.execute("SELECT data FROM thread_items").fetchone()[0], "history-sentinel")
            self.assertEqual(rollout.read_text(encoding="utf-8"), "conversation-sentinel")
            with database(profile / "state_5.sqlite") as db:
                self.assertEqual(db.execute("SELECT count(*) FROM threads").fetchone()[0], 3)

    def test_repair_only_verified_missing_mnt_mapping(self):
        spec = importlib.util.spec_from_file_location("repair", SCRIPT)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp)
            wrong = "C:\\mnt\\" + path.drive[0].lower() + str(path)[2:]
            self.assertEqual(module.repaired_cwd(wrong), str(path))
            self.assertIsNone(module.repaired_cwd(wrong + "\\missing"))
            self.assertIsNone(module.repaired_cwd(str(path)))
            rollout = path / "rollout.jsonl"
            data = [dict(type="session_meta", payload=dict(cwd=wrong, instructions="preserve")),
                    dict(type="turn_context", payload=dict(cwd=wrong)),
                    dict(type="event_msg", payload=dict(message=wrong))]
            before = b''.join((json.dumps(item) + '\n').encode() for item in data)
            rollout.write_bytes(before)
            offsets = [len(line) for line in before.splitlines(keepends=True)]
            self.assertEqual(module.repair_rollout_cwd(rollout, str(path)), 2)
            self.assertEqual(rollout.read_bytes(), before)
            self.assertEqual(module.repair_rollout_cwd(rollout, str(path), apply=True), 2)
            after = rollout.read_bytes()
            self.assertEqual([len(line) for line in after.splitlines(keepends=True)], offsets)
            items = [json.loads(line) for line in after.splitlines()]
            self.assertEqual(items[-1], data[-1])
            self.assertEqual(items[0]['payload']['cwd'], str(path))
            self.assertEqual(module.repair_rollout_cwd(rollout, str(path), apply=True), 0)


if __name__ == "__main__":
    unittest.main()
