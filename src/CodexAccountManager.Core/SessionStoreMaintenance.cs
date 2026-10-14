using System.Diagnostics;
using System.Text.Json;

namespace CodexAccountManager.Core;

public sealed record SessionPathRepair(string ThreadId, string PreviousPath, string CanonicalPath, bool Archived, bool MissingPreviousPath,
    string? PhysicalPath = null);
public sealed record SessionStoreIssue(string ThreadId, string Path, string Reason);
public sealed record SessionStoreAudit(int SchemaVersion, int CheckedThreads, IReadOnlyList<SessionPathRepair> Repairs,
    IReadOnlyList<SessionStoreIssue> Issues)
{
    public int BrokenPaths => Repairs.Count(r => r.MissingPreviousPath);
}

public sealed class SessionStoreMaintenance
{
    private readonly string root;
    private readonly string home;
    private readonly string database;
    private readonly Action ensureIdle;
    public SessionStoreMaintenance(string root, string home) : this(root, home, EnsureCodexClosed) { }
    internal SessionStoreMaintenance(string root, string home, Action ensureIdle)
    {
        this.root = PathSafety.Canonical(root); this.home = PathSafety.Canonical(home);
        database = Path.Combine(this.home, "state_5.sqlite"); this.ensureIdle = ensureIdle;
    }

    public SessionStoreAudit Audit(string? profileId = null)
    {
        ValidatePaths();
        using var db = new NativeSqlite(database);
        db.Query("BEGIN");
        try { ValidateSchema(db); return Read(db, profileId); }
        finally { db.Query("ROLLBACK"); }
    }

    public int Repair(SessionStoreAudit expected, string? profileId = null)
    {
        if (expected.Issues.Count != 0) throw new IOException("Session paths need manual review. No paths were changed.");
        if (expected.Repairs.Count == 0) return 0;
        ensureIdle(); ValidatePaths();
        var pins = new List<IDisposable>();
        try
        {
            // Hold all ordinary ancestors and every verified file against rename/write.
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in expected.Repairs.Select(r => r.PhysicalPath ?? r.CanonicalPath).Append(database))
                for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent)) directories.Add(parent);
            foreach (var directory in directories.OrderBy(p => p.Length)) pins.Add(JunctionService.PinDirectory(directory, allowWrites: true));
            foreach (var repair in expected.Repairs)
            {
                if (!PathSafety.Within(repair.CanonicalPath, home) || !PathSafety.Within(repair.PhysicalPath ?? repair.CanonicalPath, home))
                    throw new IOException("Repair target is outside the shared store.");
                pins.Add(VerifyHeader(repair.PhysicalPath ?? repair.CanonicalPath, repair.ThreadId, lockFile: true));
            }
            using var db = new NativeSqlite(database, write: true);
            db.Query("BEGIN IMMEDIATE");
            var committed = false;
            try
            {
                ValidateSchema(db);
                var current = Read(db, profileId);
                if (current.Issues.Count != 0 || !current.Repairs.SequenceEqual(expected.Repairs) || current.CheckedThreads != expected.CheckedThreads)
                    throw new IOException("Session store changed since audit. Run the audit again.");
                ensureIdle();
                foreach (var repair in current.Repairs)
                {
                    db.Query("UPDATE threads SET rollout_path=? WHERE id=? AND rollout_path=? AND archived=?",
                        repair.CanonicalPath, repair.ThreadId, repair.PreviousPath, repair.Archived ? "1" : "0");
                    if (db.Changes != 1) throw new IOException("Session path changed concurrently. Repair rolled back.");
                }
                ensureIdle();
                db.Query("COMMIT"); committed = true;
                return current.Repairs.Count;
            }
            finally { if (!committed) db.Query("ROLLBACK"); }
        }
        finally { foreach (var pin in pins.AsEnumerable().Reverse()) pin.Dispose(); }
    }

    // Called before detaching ANY profile junction, including when no repairs are needed.
    public void PrepareProfileDeletion(string profileId)
    {
        ensureIdle();
        var audit = Audit(profileId);
        if (audit.Issues.Count != 0) throw new IOException("Cannot delete account: session paths need repair.");
        Repair(audit, profileId);
        if (Audit(profileId) is { Repairs.Count: > 0 } or { Issues.Count: > 0 })
            throw new IOException("Cannot delete account: session paths still depend on its profile.");
        ensureIdle();
    }

    private void ValidatePaths()
    {
        PathSafety.OrdinaryDirectory(root); PathSafety.OrdinaryDirectory(home);
        if (Directory.EnumerateFiles(home, "state_*.sqlite").Any(p => !PathSafety.Same(p, database)))
            throw new IOException("Unrecognized Codex state database. No changes allowed.");
        if (!File.Exists(database)) throw new IOException("Codex session database is unavailable. No changes allowed.");
        PathSafety.NoReparseAncestors(database);
    }

    private static void ValidateSchema(NativeSqlite db)
    {
        var migration = db.Query("SELECT MAX(version), SUM(CASE WHEN success=0 THEN 1 ELSE 0 END) FROM _sqlx_migrations").Single();
        if (migration[0] != "59" || migration[1] != "0") throw new IOException("Unsupported Codex session schema. No changes allowed.");
        var columns = db.Query("PRAGMA table_info(threads)").ToDictionary(r => r[1]!, r => r);
        foreach (var (name, type) in new[] { ("id", "TEXT"), ("rollout_path", "TEXT"), ("archived", "INTEGER") })
            if (!columns.TryGetValue(name, out var column) || !string.Equals(column[2], type, StringComparison.OrdinalIgnoreCase)
                || (name == "id" && column[5] != "1")) throw new IOException("Unsupported Codex threads table. No changes allowed.");
        // The known timestamp triggers do not fire for rollout_path-only updates.
        foreach (var trigger in db.Query("SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name='threads'"))
        {
            var operation = trigger[0] switch
            {
                "threads_created_at_ms_after_insert" or "threads_updated_at_ms_after_insert" or "threads_recency_at_after_insert" => "AFTER INSERT ON threads",
                "threads_created_at_ms_after_update" => "AFTER UPDATE OF created_at ON threads",
                "threads_updated_at_ms_after_update" => "AFTER UPDATE OF updated_at ON threads",
                _ => null
            };
            var sql = string.Join(" ", (trigger[1] ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (operation is null || !sql.StartsWith($"CREATE TRIGGER {trigger[0]} {operation} WHEN ", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unrecognized Codex thread trigger. No changes allowed.");
        }
    }

    private SessionStoreAudit Read(NativeSqlite db, string? profileId)
    {
        if (profileId is not null && !Guid.TryParseExact(profileId, "N", out _)) throw new IOException("Invalid profile ID.");
        var repairs = new List<SessionPathRepair>(); var issues = new List<SessionStoreIssue>(); var count = 0;
        var profilesRoot = Path.Combine(root, "profiles");
        foreach (var row in db.Query("SELECT id,rollout_path,archived FROM threads ORDER BY id"))
        {
            var id = row[0] ?? ""; var previous = row[1] ?? "";
            try
            {
                var stored = previous.StartsWith(@"\\?\", StringComparison.Ordinal) && previous.Length > 6 && previous[5] == ':' ? previous[4..] : previous;
                var path = PathSafety.Canonical(stored);
                if (profileId is not null && !PathSafety.Within(path, Path.Combine(profilesRoot, profileId))) continue;
                count++;
                if (!Guid.TryParse(id, out _) || row[2] is not ("0" or "1")) throw new IOException("Invalid thread metadata.");
                var archived = row[2] == "1"; var folder = archived ? "archived_sessions" : "sessions";
                string canonical;
                var managedAlias = PathSafety.Within(path, profilesRoot);
                if (managedAlias)
                {
                    var relative = Path.GetRelativePath(profilesRoot, path).Split('\\');
                    if (relative.Length < 3 || !Guid.TryParseExact(relative[0], "N", out _) || relative[1] != folder)
                        throw new IOException("Unexpected managed session path.");
                    var profile = Path.Combine(profilesRoot, relative[0]); PathSafety.NoReparseAncestors(profile);
                    var link = Path.Combine(profile, folder);
                    if (PathSafety.Exists(link)) new JunctionService().Verify(link, Path.Combine(home, folder));
                    canonical = PathSafety.Canonical(Path.Combine(home, folder, Path.Combine(relative[2..])));
                }
                else canonical = path;
                if (!PathSafety.Within(canonical, Path.Combine(home, folder))) throw new IOException("Path is outside the expected shared session directory.");
                using var file = VerifyHeader(canonical, id);
                if (managedAlias)
                    repairs.Add(new(id, previous, canonical, archived, RolloutFile.Resolve(previous) is null, RolloutFile.Resolve(canonical)));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
            {
                // Only bounded, fixed diagnostics; no transcript or parser excerpts.
                var reason = ex is FileNotFoundException or DirectoryNotFoundException ? "Session file is missing."
                    : ex is JsonException ? "Invalid session metadata." : ex is UnauthorizedAccessException ? "Session file is inaccessible."
                    : ex is ArgumentException ? "Invalid session path." : ex.Message;
                issues.Add(new(id, previous, reason));
            }
        }
        return new(59, count, repairs, issues);
    }

    private static Stream VerifyHeader(string path, string threadId, bool lockFile = false)
    {
        var stream = RolloutFile.OpenRead(path, lockFile);
        try
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            var header = new System.Text.StringBuilder();
            while (reader.Read() is var ch && ch >= 0 && ch != '\n')
            {
                if (header.Length >= 16 * 1024 * 1024) throw new IOException("Session metadata is too large.");
                header.Append((char)ch);
            }
            using var doc = JsonDocument.Parse(header.ToString());
            if (CodexThreadReader.Text(doc.RootElement, "type") != "session_meta"
                || !doc.RootElement.TryGetProperty("payload", out var meta)
                || !string.Equals(CodexThreadReader.Text(meta, "id"), threadId, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Session file ID does not match the database.");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public static void EnsureCodexClosed()
    {
        // Conservative because another account process can own any shared thread.
        // No process is terminated. Also covers Desktop's ChatGPT executable name.
        foreach (var name in new[] { "codex", "ChatGPT" })
        foreach (var process in Process.GetProcessesByName(name))
            using (process)
                if (!process.HasExited) throw new IOException("Close Codex Desktop and all Codex CLI sessions before repairing paths or deleting an account.");
    }
}
