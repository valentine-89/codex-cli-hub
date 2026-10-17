using CodexAccountManager.Core;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using ZstdSharp;

internal static class SessionStoreTests
{
    internal static void CreateDatabase(string home)
    {
        using var db = new NativeSqlite(Path.Combine(home, "state_5.sqlite"), write: true, create: true);
        db.Query("CREATE TABLE _sqlx_migrations(version INTEGER PRIMARY KEY,success INTEGER NOT NULL)");
        db.Query("INSERT INTO _sqlx_migrations VALUES(59,1)");
        db.Query("CREATE TABLE threads(id TEXT PRIMARY KEY,rollout_path TEXT NOT NULL,archived INTEGER NOT NULL DEFAULT 0,title TEXT DEFAULT 'keep title',project_id TEXT DEFAULT 'keep project',updated_at INTEGER DEFAULT 42)");
    }

    internal static void Register(List<(string Name, Func<Task> Run)> tests, string testRoot)
    {
        void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
        Test("session repair preserves content and other columns, repairs deleted profile, and is idempotent", () =>
        {
            var f = new StoreFixture(testRoot); var a = f.Profile(); var b = f.Profile();
            var first = f.Thread(a); var second = f.Thread(b, archived: true);
            new JunctionService().Remove(Path.Combine(a, "sessions"), Path.Combine(f.Home, "sessions"));
            var before = f.UnrelatedColumns(); var content = File.ReadAllBytes(first.CanonicalPath);
            File.WriteAllText(Path.Combine(f.Home, "thread_history_1.sqlite"), "history sentinel");
            var audit = f.Service.Audit();
            Check(audit.Repairs.Count == 2 && audit.BrokenPaths == 1 && audit.Issues.Count == 0, "Incorrect repair inventory");
            Check(f.Service.Repair(audit) == 2, "Repair count");
            Check(f.Service.Repair(f.Service.Audit()) == 0, "Repair is not idempotent");
            Check(before == f.UnrelatedColumns(), "Non-path metadata changed");
            Check(content.SequenceEqual(File.ReadAllBytes(first.CanonicalPath)), "Transcript changed");
            Check(File.ReadAllText(Path.Combine(f.Home, "thread_history_1.sqlite")) == "history sentinel", "History changed");
            Check(f.Path(first.ThreadId) == first.CanonicalPath && f.Path(second.ThreadId) == second.CanonicalPath, "Not canonical");
        });
        Test("profile deletion canonicalizes its sessions before detaching links", () =>
        {
            var f = new StoreFixture(testRoot); var a = f.Profile(); var b = f.Profile(); var session = f.Thread(a);
            // Account B can access the same transcript through its own junction.
            Check(File.Exists(Path.Combine(b, "sessions", Path.GetRelativePath(Path.Combine(f.Home, "sessions"), session.CanonicalPath))), "B cannot resume A file");
            var account = new Account { Id = Path.GetFileName(a), ProfilePath = Path.Combine("profiles", Path.GetFileName(a)), DisplayName = "A" };
            new CodexProfileService(f.Root, new JunctionService(), () => { }).Delete(account, new() { DefaultCodexHome = f.Home });
            Check(!Directory.Exists(a) && File.Exists(session.CanonicalPath) && f.Path(session.ThreadId) == session.CanonicalPath, "Delete orphaned a session");
        });
        Test("running Codex blocks repair and deletion without partial changes", () =>
        {
            var f = new StoreFixture(testRoot); var profile = f.Profile(); var session = f.Thread(profile);
            var service = new SessionStoreMaintenance(f.Root, f.Home, () => throw new IOException("active client"));
            Reject(() => service.Repair(service.Audit()));
            var account = new Account { Id = Path.GetFileName(profile), ProfilePath = Path.Combine("profiles", Path.GetFileName(profile)), DisplayName = "A" };
            Reject(() => new CodexProfileService(f.Root, new JunctionService(), () => throw new IOException("active client")).Delete(account, new() { DefaultCodexHome = f.Home }));
            Check(Directory.Exists(Path.Combine(profile, "sessions")) && f.Path(session.ThreadId) == session.PreviousPath, "Active-client rejection mutated state");
        });
        Test("device-prefixed canonical paths stay unchanged while prefixed profile aliases are repaired", () =>
        {
            var f = new StoreFixture(testRoot); var profile = f.Profile();
            var canonical = f.Thread(profile); var alias = f.Thread(profile);
            using (var db = f.Db())
            {
                db.Query("UPDATE threads SET rollout_path=? WHERE id=?", @"\\?\" + canonical.CanonicalPath, canonical.ThreadId);
                db.Query("UPDATE threads SET rollout_path=? WHERE id=?", @"\\?\" + alias.PreviousPath, alias.ThreadId);
            }
            var audit = f.Service.Audit(Path.GetFileName(profile));
            Check(audit.Repairs.Count == 1 && audit.Issues.Count == 0, "Prefix changed scope");
            Check(f.Service.Repair(audit, Path.GetFileName(profile)) == 1, "Prefixed alias not repaired");
            Check(f.Path(canonical.ThreadId) == @"\\?\" + canonical.CanonicalPath, "Canonical spelling changed");
            Check(f.Service.Audit().Repairs.Count == 0, "Canonical prefix treated as broken");
        });
        Test("missing file and mismatched header block the entire repair batch", () =>
        {
            var f = new StoreFixture(testRoot); var a = f.Profile(); var good = f.Thread(a); var bad = f.Thread(a);
            File.WriteAllText(bad.CanonicalPath, JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = Guid.NewGuid().ToString() } }) + "\n");
            var audit = f.Service.Audit(); Check(audit.Issues.Count == 1, "ID mismatch not detected");
            Reject(() => f.Service.Repair(audit)); Check(f.Path(good.ThreadId) == good.PreviousPath, "Partial repair");
            File.Delete(bad.CanonicalPath); Check(f.Service.Audit().Issues.Single().Reason == "Session file is missing.", "Missing file not detected");
        });
        Test("paths outside the shared store and nested reparse targets are rejected", () =>
        {
            var f = new StoreFixture(testRoot); var a = f.Profile(); var session = f.Thread(a);
            using (var db = f.Db()) db.Query("UPDATE threads SET rollout_path=? WHERE id=?", Path.Combine(f.Root, "outside.jsonl"), session.ThreadId);
            Check(f.Service.Audit().Issues.Count == 1, "Outside path accepted");
            using (var db = f.Db()) db.Query("UPDATE threads SET rollout_path=? WHERE id=?", session.PreviousPath.Replace("sessions\\", "sessions\\..\\"), session.ThreadId);
            Check(f.Service.Audit().Issues.Count == 1, "Traversal accepted");
            var linked = Path.Combine(f.Home, "sessions", "linked"); new JunctionService().Create(linked, f.Root);
            using (var db = f.Db()) db.Query("UPDATE threads SET rollout_path=? WHERE id=?", Path.Combine(linked, "outside.jsonl"), session.ThreadId);
            Check(f.Service.Audit().Issues.Count == 1, "Nested junction accepted");
        });
        Test("changed audit and busy database fail without overwriting concurrent state", () =>
        {
            var f = new StoreFixture(testRoot); var session = f.Thread(f.Profile()); var audit = f.Service.Audit();
            using (var db = f.Db()) db.Query("UPDATE threads SET archived=1 WHERE id=?", session.ThreadId);
            Reject(() => f.Service.Repair(audit));
            using (var db = f.Db()) db.Query("UPDATE threads SET archived=0 WHERE id=?", session.ThreadId);
            using (var writer = f.Db())
            {
                writer.Query("BEGIN IMMEDIATE"); Reject(() => f.Service.Repair(f.Service.Audit())); writer.Query("ROLLBACK");
            }
            Check(f.Path(session.ThreadId) == session.PreviousPath, "Concurrent rejection changed path");
        });
        Test("new client detected before commit rolls back every path update", () =>
        {
            var f = new StoreFixture(testRoot); var first = f.Thread(f.Profile()); var second = f.Thread(f.Profile()); var checks = 0;
            var service = new SessionStoreMaintenance(f.Root, f.Home, () => { if (++checks == 3) throw new IOException("new client"); });
            Reject(() => service.Repair(service.Audit()));
            Check(f.Path(first.ThreadId) == first.PreviousPath && f.Path(second.ThreadId) == second.PreviousPath, "Transaction did not roll back");
        });
        Test("unknown schema and custom triggers block writes", () =>
        {
            var f = new StoreFixture(testRoot); var session = f.Thread(f.Profile());
            using (var db = f.Db()) db.Query("INSERT INTO _sqlx_migrations VALUES(62,1)");
            Reject(() => f.Service.Audit());
            using (var db = f.Db())
            {
                db.Query("DELETE FROM _sqlx_migrations WHERE version=62");
                db.Query("CREATE TRIGGER unexpected AFTER UPDATE ON threads BEGIN UPDATE threads SET title='changed' WHERE id=NEW.id; END");
            }
            Reject(() => f.Service.Audit()); Check(f.Path(session.ThreadId) == session.PreviousPath, "Unknown schema was modified");
            using (var db = f.Db())
            {
                db.Query("DROP TRIGGER unexpected");
                db.Query("CREATE TRIGGER threads_created_at_ms_after_update AFTER UPDATE ON threads BEGIN UPDATE threads SET title='changed' WHERE id=NEW.id; END");
            }
            Reject(() => f.Service.Audit());
        });
        Test("schema 60 and 61 repair paths without changing read state or metadata", () =>
        {
            foreach (var version in new[] { 60, 61 })
            {
                var f = new StoreFixture(testRoot); var session = f.Thread(f.Profile()); Compress(session.CanonicalPath);
                using (var db = f.Db())
                {
                    for (var v = 60; v <= version; v++) db.Query("INSERT INTO _sqlx_migrations VALUES(?,1)", v.ToString());
                    db.Query("CREATE TABLE guardian_review_feedback(id TEXT PRIMARY KEY NOT NULL,thread_id TEXT NOT NULL REFERENCES threads(id) ON DELETE CASCADE,record BLOB NOT NULL)");
                    db.Query("INSERT INTO guardian_review_feedback VALUES('fixture',?,X'0102')", session.ThreadId);
                    if (version == 61)
                    {
                        db.Query("CREATE TABLE thread_read_receipts(thread_id TEXT PRIMARY KEY NOT NULL,first_unread_turn TEXT,revision TEXT NOT NULL,last_published_turn TEXT)");
                        db.Query("INSERT INTO thread_read_receipts VALUES(?,'unread','keep revision','published')", session.ThreadId);
                    }
                }
                var before = f.UnrelatedColumns(); var audit = f.Service.Audit();
                Check(audit.SchemaVersion == version && audit.Issues.Count == 0, "Actual schema version not reported");
                Check(f.Service.Repair(audit) == 1 && f.Service.Repair(f.Service.Audit()) == 0, "New schema repair failed");
                Check(f.UnrelatedColumns() == before, "Non-path metadata changed");
                using var check = f.Db();
                Check(check.Query("SELECT hex(record) FROM guardian_review_feedback").Single()[0] == "0102", "Guardian feedback changed");
                if (version == 61) Check(check.Query("SELECT revision FROM thread_read_receipts").Single()[0] == "keep revision", "Read state changed");
            }
        });
        Test("failed migration and supported migration change reject writes", () =>
        {
            var f = new StoreFixture(testRoot); var session = f.Thread(f.Profile()); var audit = f.Service.Audit();
            using (var db = f.Db()) db.Query("INSERT INTO _sqlx_migrations VALUES(60,0)");
            Reject(() => f.Service.Audit());
            using (var db = f.Db()) db.Query("UPDATE _sqlx_migrations SET success=1 WHERE version=60");
            Reject(() => f.Service.Repair(audit));
            Check(f.Path(session.ThreadId) == session.PreviousPath, "Stale schema audit updated path");
        });
        Test("offline account deletion preserves other accounts and compressed shared history on schema 61", () =>
        {
            var f = new StoreFixture(testRoot); var a = f.Profile(); var b = f.Profile(); var session = f.Thread(a); Compress(session.CanonicalPath);
            using (var db = f.Db()) { db.Query("INSERT INTO _sqlx_migrations VALUES(60,1)"); db.Query("INSERT INTO _sqlx_migrations VALUES(61,1)"); }
            var selected = new Account { Id = Path.GetFileName(a), ProfilePath = Path.Combine("profiles", Path.GetFileName(a)), DisplayName = "selected@example.test" };
            var other = new Account { Id = Path.GetFileName(b), ProfilePath = Path.Combine("profiles", Path.GetFileName(b)), DisplayName = "other@example.test" };
            var repository = new AccountRepository(f.Root);
            repository.SaveAccounts(new() { Accounts = [selected, other] }); repository.SaveSettings(new() { DefaultCodexHome = f.Home });
            File.WriteAllText(Path.Combine(a, "auth.json"), "fixture credential");
            File.WriteAllText(Path.Combine(b, "auth.json"), "other credential");
            var cache = Path.Combine(a, ".tmp", "plugins-clone-fixture", ".git", "objects", "pack"); Directory.CreateDirectory(cache);
            var readOnly = Path.Combine(cache, "fixture.idx"); File.WriteAllText(readOnly, "private plugin cache");
            File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
            File.SetAttributes(session.CanonicalPath + ".zst", File.GetAttributes(session.CanonicalPath + ".zst") | FileAttributes.ReadOnly);
            var before = File.ReadAllBytes(session.CanonicalPath + ".zst");
            try
            {
                new AccountDeletion(repository, () => { }).Delete(selected.Id, selected.DisplayName);
                Check((File.GetAttributes(session.CanonicalPath + ".zst") & FileAttributes.ReadOnly) != 0, "Shared file attributes changed");
            }
            finally
            {
                if (File.Exists(readOnly)) File.SetAttributes(readOnly, FileAttributes.Normal);
                File.SetAttributes(session.CanonicalPath + ".zst", FileAttributes.Normal);
            }
            Check(!Directory.Exists(a) && repository.LoadAccounts().Accounts.Single().Id == other.Id, "Wrong account removed");
            Check(File.ReadAllText(Path.Combine(b, "auth.json")) == "other credential", "Other credentials changed");
            Check(f.Path(session.ThreadId) == session.CanonicalPath && before.SequenceEqual(File.ReadAllBytes(session.CanonicalPath + ".zst")), "Shared chat lost");
        });
        Test("offline deletion rejects wrong identity, active clients and locked manager", () =>
        {
            var f = new StoreFixture(testRoot); var p = f.Profile(); var session = f.Thread(p);
            var account = new Account { Id = Path.GetFileName(p), ProfilePath = Path.Combine("profiles", Path.GetFileName(p)), DisplayName = "selected@example.test" };
            var repository = new AccountRepository(f.Root);
            repository.SaveAccounts(new() { Accounts = [account] }); repository.SaveSettings(new() { DefaultCodexHome = f.Home });
            var service = new AccountDeletion(repository, () => { });
            Reject(() => service.Delete("../invalid", account.DisplayName));
            Reject(() => service.Delete(Guid.NewGuid().ToString("N"), account.DisplayName));
            Reject(() => service.Delete(account.Id, "different@example.test"));
            using (var appLock = repository.AcquireLock()) Reject(() => service.Delete(account.Id, account.DisplayName));
            Reject(() => new AccountDeletion(repository, () => throw new IOException("active client")).Delete(account.Id, account.DisplayName));
            Check(Directory.Exists(p) && repository.LoadAccounts().Accounts.Count == 1 && f.Path(session.ThreadId) == session.PreviousPath, "Rejected deletion modified data");
        });
        Test("audit does not change database or session files", () =>
        {
            var f = new StoreFixture(testRoot); var session = f.Thread(f.Profile());
            var before = SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Home, "state_5.sqlite")));
            _ = f.Service.Audit();
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Home, "state_5.sqlite")))), "Audit wrote database");
            Check(f.Path(session.ThreadId) == session.PreviousPath, "Audit repaired a path");
        });
        Test("compressed rollout audit and repair preserve compressed bytes and logical database paths", () =>
        {
            var f = new StoreFixture(testRoot); var profile = f.Profile();
            var active = f.Thread(profile); var archived = f.Thread(profile, archived: true);
            Compress(active.CanonicalPath); Compress(archived.CanonicalPath);
            new JunctionService().Remove(Path.Combine(profile, "sessions"), Path.Combine(f.Home, "sessions"));
            var bytes = File.ReadAllBytes(active.CanonicalPath + ".zst"); var metadata = f.UnrelatedColumns();
            var audit = f.Service.Audit();
            Check(audit.Issues.Count == 0 && audit.Repairs.Count == 2 && audit.BrokenPaths == 1, "Compressed files treated as missing");
            Check(audit.Repairs.All(r => r.PhysicalPath == r.CanonicalPath + ".zst"), "Physical path unavailable to verifier");
            Check(f.Service.Repair(audit) == 2 && f.Path(active.ThreadId) == active.CanonicalPath, "Logical path not canonicalized");
            Check(!File.Exists(active.CanonicalPath) && bytes.SequenceEqual(File.ReadAllBytes(active.CanonicalPath + ".zst")), "Transcript was materialized or rewritten");
            Check(metadata == f.UnrelatedColumns() && f.Service.Audit().Issues.Count == 0 && f.Service.Repair(f.Service.Audit()) == 0, "Compressed repair changed metadata or is not idempotent");
        });
        Test("compressed mismatch and invalid frames block repair without partial updates", () =>
        {
            var f = new StoreFixture(testRoot); var p = f.Profile(); var good = f.Thread(p); var bad = f.Thread(p);
            File.WriteAllText(bad.CanonicalPath, JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = Guid.NewGuid().ToString() } }));
            Compress(bad.CanonicalPath);
            Check(f.Service.Audit().Issues.Single().Reason.Contains("ID"), "Compressed identity not checked");
            Reject(() => f.Service.Repair(f.Service.Audit()));
            File.WriteAllText(bad.CanonicalPath + ".zst", "not a Zstandard frame");
            Check(f.Service.Audit().Issues.Count == 1, "Invalid frame accepted");
            Reject(() => f.Service.Repair(f.Service.Audit()));
            Check(f.Path(good.ThreadId) == good.PreviousPath, "Partial repair after compressed-file failure");
        });
        Test("compression transition after audit rejects stale physical file assumptions", () =>
        {
            var f = new StoreFixture(testRoot); var session = f.Thread(f.Profile()); var audit = f.Service.Audit();
            Compress(session.CanonicalPath);
            Reject(() => f.Service.Repair(audit));
            Check(f.Path(session.ThreadId) == session.PreviousPath, "Stale audit wrote database");
        });
        Test("compressed session status and saved context match the plain transcript", () =>
        {
            var f = new StoreFixture(testRoot); var session = f.Thread(f.Profile());
            File.AppendAllText(session.CanonicalPath, JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "fixture-model", effort = "high", approval_policy = "never" } }) + "\n"
                + JsonSerializer.Serialize(new { timestamp = "2026-10-14T00:00:00Z", type = "event_msg", payload = new { type = "task_complete", last_agent_message = "fixture result" } }) + "\n");
            var context = RolloutStatus.LastContext(f.Home, session.ThreadId)!.Value.GetRawText();
            var status = JsonSerializer.Serialize(RolloutStatus.Read(f.Home, session.ThreadId));
            Compress(session.CanonicalPath);
            Check(RolloutStatus.Find(f.Home, session.ThreadId) == session.CanonicalPath + ".zst", "Compressed chat cannot be resumed");
            Check(RolloutStatus.LastContext(f.Home, session.ThreadId)!.Value.GetRawText() == context, "Saved policy/model changed");
            Check(JsonSerializer.Serialize(RolloutStatus.Read(f.Home, session.ThreadId)) == status, "Completion state changed");
            File.WriteAllText(session.CanonicalPath, "new live transcript\n");
            Check(RolloutStatus.Find(f.Home, session.ThreadId) == session.CanonicalPath, "Stale compressed copy preferred over live file");
        });
        Test("compressed tail stays bounded and drops partial records", () =>
        {
            var f = new StoreFixture(testRoot); var session = f.Thread(f.Profile());
            File.WriteAllText(session.CanonicalPath, new string('x', 2 * 1024 * 1024) + "\ncomplete\npartial");
            var plain = RolloutStatus.Tail(session.CanonicalPath); Compress(session.CanonicalPath);
            Check(plain.SequenceEqual(RolloutStatus.Tail(session.CanonicalPath + ".zst")) && plain.SequenceEqual(new[] { "complete" }), "Compressed tail includes truncated data");
        });
    }
    private static void Compress(string path)
    {
        using var compressor = new Compressor();
        File.WriteAllBytes(path + ".zst", compressor.Wrap(File.ReadAllBytes(path)).ToArray());
        File.Delete(path);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Unsafe operation succeeded"); }

    private sealed class StoreFixture
    {
        internal string Root { get; }
        internal string Home { get; }
        internal SessionStoreMaintenance Service { get; }
        internal StoreFixture(string parent)
        {
            var directory = System.IO.Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Root = System.IO.Path.Combine(directory, "manager"); Home = System.IO.Path.Combine(directory, "home");
            Directory.CreateDirectory(System.IO.Path.Combine(Root, "profiles"));
            Directory.CreateDirectory(System.IO.Path.Combine(Home, "sessions")); Directory.CreateDirectory(System.IO.Path.Combine(Home, "archived_sessions"));
            CreateDatabase(Home); Service = new(Root, Home, () => { });
        }
        internal string Profile()
        {
            var p = System.IO.Path.Combine(Root, "profiles", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p);
            var links = new JunctionService();
            foreach (var name in new[] { "sessions", "archived_sessions" }) links.Create(System.IO.Path.Combine(p, name), System.IO.Path.Combine(Home, name));
            return p;
        }
        internal SessionPathRepair Thread(string profile, bool archived = false)
        {
            var id = Guid.NewGuid().ToString(); var name = archived ? "archived_sessions" : "sessions";
            var tail = archived ? "rollout-" + id + ".jsonl" : System.IO.Path.Combine("2026", "10", "13", "rollout-" + id + ".jsonl");
            var target = System.IO.Path.Combine(Home, name, tail); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllText(target, JsonSerializer.Serialize(new { type = "session_meta", payload = new { id } }) + "\nKEEP CONTENT\n");
            var previous = System.IO.Path.Combine(profile, name, tail);
            using var db = Db(); db.Query("INSERT INTO threads(id,rollout_path,archived) VALUES(?,?,?)", id, previous, archived ? "1" : "0");
            return new(id, previous, target, archived, false);
        }
        internal NativeSqlite Db() => new(System.IO.Path.Combine(Home, "state_5.sqlite"), write: true);
        internal string Path(string id) { using var db = Db(); return db.Query("SELECT rollout_path FROM threads WHERE id=?", id).Single()[0]!; }
        internal string UnrelatedColumns() { using var db = Db(); return JsonSerializer.Serialize(db.Query("SELECT id,archived,title,project_id,updated_at FROM threads ORDER BY id")); }
    }
}
