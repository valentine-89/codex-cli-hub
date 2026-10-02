import importlib.util
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("repair", Path(__file__).parents[1] / "scripts/repair-projectless-chat.py")
repair = importlib.util.module_from_spec(spec)
spec.loader.exec_module(repair)


class ProjectlessRepairTests(unittest.TestCase):
    def test_dry_run_apply_and_repeat_preserve_existing_state_and_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)
            cwd = home / "manager-chats" / "old-chat"
            cwd.mkdir(parents=True)
            sentinel = cwd / "work.py"
            sentinel.write_text("keep")
            thread_id = "01a0df23-399a-75b2-98a3-a6ba5628c31f"
            state = {"unrelated": {"keep": True}, "projectless-thread-ids": ["existing"]}
            path = home / ".codex-global-state.json"
            path.write_text(json.dumps(state))
            original = path.read_bytes()
            with closing(sqlite3.connect(home / "state_5.sqlite")) as db:
                db.execute("CREATE TABLE threads(id TEXT, cwd TEXT, project_id TEXT, archived INTEGER)")
                db.execute("INSERT INTO threads VALUES(?,?,NULL,0)", (thread_id, str(cwd)))
                db.commit()
            self.assertEqual(repair.repair(home)["threadIds"], [thread_id])
            self.assertEqual(path.read_bytes(), original)
            with patch.object(repair, "require_desktop_closed", side_effect=RuntimeError("running")):
                with self.assertRaises(RuntimeError):
                    repair.repair(home, True)
            self.assertEqual(path.read_bytes(), original)
            with patch.object(repair, "require_desktop_closed"):
                repair.repair(home, True)
                self.assertEqual(repair.repair(home, True)["threadIds"], [])
            updated = json.loads(path.read_text())
            self.assertEqual(updated["unrelated"], state["unrelated"])
            self.assertEqual(updated["projectless-thread-ids"], ["existing", thread_id])
            self.assertEqual(updated["thread-projectless-output-directories"][thread_id], str(cwd))
            self.assertEqual(sentinel.read_text(), "keep")
            self.assertEqual(next(home.glob("manager-projectless-repair-*.bak")).read_bytes(), original)
            with closing(sqlite3.connect(home / "state_5.sqlite")) as db:
                self.assertEqual(db.execute("SELECT cwd FROM threads").fetchone()[0], str(cwd))

    def test_never_reclassifies_assigned_or_unrelated_sessions(self):
        ids = [f"00000000-0000-0000-0000-00000000000{i}" for i in range(3)]
        state = {"thread-project-assignments": {ids[0]: {"projectId": "p"}}}
        rows = [(ids[0], r"C:\codex\manager-chats\a", None),
                (ids[1], r"C:\codex\manager-chats\b", "server-project"),
                (ids[2], r"C:\codex\manager-chats-other\c", None)]
        self.assertEqual(repair.prepare(state, rows, r"C:\codex\manager-chats")[1], [])
        with self.assertRaises(ValueError):
            repair.prepare({"projectless-thread-ids": {}}, rows, r"C:\codex\manager-chats")


if __name__ == "__main__":
    unittest.main()
