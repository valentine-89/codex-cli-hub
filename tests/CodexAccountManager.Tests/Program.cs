using CodexAccountManager.Core;
using System.Text.Json;

if (args.Contains("--echo-arguments"))
{ Console.OutputEncoding = new System.Text.UTF8Encoding(false); Console.Write(JsonSerializer.Serialize(args)); return 0; }

if (args.Contains("exec"))
{
    if (!args.Contains("--ephemeral") || !args.Contains("--ignore-user-config") || !args.Contains("reply \"OK\"")
        || !args.Contains("read-only") || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_HOME"))) return 42;
    Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"OK\"}}");
    return 0;
}

// Synthetic app-server for protocol tests; never reads credentials or contacts a server.
if (args.Contains("app-server"))
{
    while (Console.ReadLine() is { } line)
    {
        using var request = System.Text.Json.JsonDocument.Parse(line);
        var json = request.RootElement;
        if (!json.TryGetProperty("id", out var id)) continue;
        if (json.TryGetProperty("result", out _))
        {
            Console.WriteLine("{\"method\":\"serverRequest/resolved\",\"params\":{\"requestId\":\"approval-1\"}}");
            continue;
        }
        var method = json.GetProperty("method").GetString();
        if (method == "account/rateLimitResetCredit/consume")
        {
            var key = json.GetProperty("params").GetProperty("idempotencyKey").GetString();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { outcome = key == "00000000-0000-0000-0000-000000000001" ? "alreadyRedeemed" : "noCredit" } }));
        }
        else if (method == "thread/list")
        {
            var parameters = json.GetProperty("params");
            if (!parameters.GetProperty("useStateDbOnly").GetBoolean() || parameters.GetProperty("archived").GetBoolean()
                || parameters.GetProperty("modelProviders").GetArrayLength() != 0
                || !args.Any(a => a.StartsWith("sqlite_home=")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME"))) return 43;
            var profileHome = Environment.GetEnvironmentVariable("CODEX_HOME")!;
            var modeFile = Path.Combine(profileHome, "catalog-mode.txt");
            var mode = File.Exists(modeFile) ? File.ReadAllText(modeFile) : "";
            if (mode == "error") { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { id = id.GetInt32(), error = new { code = -32601 } })); continue; }
            if (mode == "cancel") { await Task.Delay(10000); continue; }
            var second = parameters.GetProperty("cursor").ValueKind == System.Text.Json.JsonValueKind.String;
            var data = second ? new object[] {
                new { id = "00000000-0000-0000-0000-000000000001", cwd = profileHome, name = "Project chat", updatedAt = 100 },
                new { id = "00000000-0000-0000-0000-000000000002", cwd = (string?)null, name = "Chat without folder", updatedAt = 200 }
            } : new object[] { new { id = "00000000-0000-0000-0000-000000000001", cwd = profileHome, name = "Project chat", updatedAt = 100 } };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { data, nextCursor = second && mode != "repeat" ? null : "page-2" } }));
        }
        else if (method is "thread/start" or "thread/resume")
        {
            var parameters = json.GetProperty("params");
            if (parameters.TryGetProperty("sandbox", out var sandbox) && sandbox.GetString() is not ("danger-full-access" or "read-only" or "workspace-write")) return 45;
            var threadId = parameters.TryGetProperty("threadId", out var tid) ? tid.GetString() : "00000000-0000-0000-0000-000000000003";
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { thread = new { id = threadId } } }));
        }
        else if (method == "turn/start")
        {
            var text = json.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { turn = new { id = "turn-test", status = "inProgress" } } }));
            Console.WriteLine("{\"method\":\"turn/started\",\"params\":{\"turn\":{\"id\":\"turn-test\"}}}");
            if (text == "approval") Console.WriteLine("{\"id\":\"approval-1\",\"method\":\"item/commandExecution/requestApproval\",\"params\":{\"threadId\":\"00000000-0000-0000-0000-000000000003\",\"turnId\":\"turn-test\",\"command\":\"echo fixture\"}}");
        }
        else if (method == "turn/steer") Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { turnId = "turn-test" } }));
        else if (method == "turn/interrupt")
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { } }));
            Console.WriteLine("{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"id\":\"turn-test\",\"status\":\"interrupted\"}}}");
        }
        else Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { } }));
    }
    return 0;
}

if (args.Length == 1 && args[0] == "--echo-stdin")
{
    Console.InputEncoding = new System.Text.UTF8Encoding(false); Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    Console.Write(Console.In.ReadToEnd()); return 0;
}

if (args.Length == 2 && args[0] == "--refresh-accounts")
{
    var repo = new AccountRepository(args[1]); using var appLock = repo.AcquireLock();
    var doc = repo.LoadAccounts(); var settings = repo.LoadSettings(); var deps = await DependencyDetector.DetectAsync();
    foreach (var account in doc.Accounts)
    {
        var home = new CodexProfileService(repo.Root, new JunctionService()).Validate(account, settings);
        CredentialConfig.EnsureFile(home);
        account.LoginStatus = await new CodexStatusService().CheckAsync(deps, home);
        account.Quota = await new CodexQuotaService().ReadAsync(deps, home); account.QuotaError = null;
        account.LastCheckedAt = DateTimeOffset.UtcNow;
        Console.WriteLine(account.DisplayName + ": " + account.LoginStatus);
        foreach (var line in account.Quota.Lines) Console.WriteLine(line.Display());
    }
    repo.SaveAccounts(doc); return 0;
}

if (args.Length == 2 && args[0] == "--catalog")
{
    var result = new SessionProjectCatalog().Read(args[1]);
    Console.WriteLine($"Projects: {result.Projects.Count}; skipped entries: {result.SkippedFiles}");
    foreach (var project in result.Projects.Take(8)) Console.WriteLine($"{project.Directory} (sessions={project.SessionCount}, exists={project.Exists})");
    return 0;
}

if (args.Length == 3 && args[0] == "--workspace-catalog")
{
    var deps = await DependencyDetector.DetectAsync();
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var result = await new WorkspaceCatalogService().ReadAsync(deps, args[2], args[1]);
    Console.WriteLine($"Projects: {result.Projects.Count}; sessions: {result.Sessions.Count}; projectless: {result.Sessions.Count(s => s.IsProjectless)}; warning: {result.Warning ?? "none"}; elapsed: {timer.ElapsedMilliseconds} ms");
    foreach (var project in result.Projects) Console.WriteLine($"{project.Directory} (sessions={project.SessionCount}, exists={project.Exists})");
    return 0;
}

if (args.Length == 2 && args[0] == "--prepare-shared-store")
{
    var repo = new AccountRepository(args[1]); using var appLock = repo.AcquireLock();
    var service = new CodexProfileService(repo.Root, new JunctionService());
    foreach (var account in repo.LoadAccounts().Accounts)
    {
        service.PrepareSharedStore(account, repo.LoadSettings());
        Console.WriteLine("Shared store prepared: " + account.Id);
    }
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "CodexManagerTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Expected fail-safe rejection."); }
Fixture Fixture() => new(Path.Combine(root, Guid.NewGuid().ToString("N")));

Test("create independent profiles, real NTFS junction and safe delete", () =>
{
    var f = Fixture();
    var a = f.Create("A"); var b = f.Create("B");
    Assert(a.Id != b.Id && a.ProfilePath != b.ProfilePath, "Profiles not independent");
    Assert(Directory.Exists(f.Path(a)), "Missing profile");
    Assert(PathSafety.Same(f.Junctions.GetTarget(f.Link(a)), f.Shared), "Wrong junction target");
    File.WriteAllText(Path.Combine(f.Path(a), "auth.json"), "test-only credential placeholder");
    Directory.CreateDirectory(Path.Combine(f.Path(a), "nested"));
    File.WriteAllText(Path.Combine(f.Path(a), "nested", "data"), "private");
    f.Service.Delete(a, f.Settings);
    Assert(!PathSafety.Exists(f.Path(a)) && !PathSafety.Exists(f.Link(a)), "Profile remains");
    Assert(File.ReadAllText(f.Sentinel) == "shared-do-not-delete", "Shared content lost");
    Assert(Directory.Exists(f.Path(b)), "Other account lost");
    f.Service.Delete(b, f.Settings);
});
Test("real sessions directory aborts before any deletion", () =>
{
    var f = Fixture(); var a = f.Create("A");
    f.Junctions.Remove(f.Link(a), f.Shared); Directory.CreateDirectory(f.Link(a));
    File.WriteAllText(Path.Combine(f.Link(a), "keep"), "keep");
    Reject(() => f.Service.Delete(a, f.Settings));
    Assert(File.Exists(Path.Combine(f.Link(a), "keep")) && File.Exists(Path.Combine(f.Path(a), "config.toml")), "Files removed on abort");
});
Test("shared archive survives deletion and conflicting archive fails before mutation", () =>
{
    var f = Fixture(); var a = f.Create("A"); var b = f.Create("B");
    var target = Path.Combine(f.Settings.DefaultCodexHome, "archived_sessions");
    File.WriteAllText(Path.Combine(target, "keep.jsonl"), "archive-sentinel");
    var link = Path.Combine(f.Path(a), "archived_sessions");
    f.Junctions.Remove(link, target); Directory.CreateDirectory(link);
    File.WriteAllText(Path.Combine(link, "private.jsonl"), "private-sentinel");
    var before = File.ReadAllText(Path.Combine(f.Path(a), "config.toml"));
    Reject(() => f.Service.PrepareSharedStore(a, f.Settings));
    Reject(() => f.Service.Delete(a, f.Settings));
    Assert(File.ReadAllText(Path.Combine(link, "private.jsonl")) == "private-sentinel", "Private archive lost");
    Assert(File.ReadAllText(Path.Combine(f.Path(a), "config.toml")) == before && PathSafety.Exists(f.Link(a)), "Partial mutation on failure");
    f.Service.Delete(b, f.Settings);
    Assert(File.ReadAllText(Path.Combine(target, "keep.jsonl")) == "archive-sentinel", "Shared archive deleted");
});
Test("existing profiles converge on shared SQLite home without touching private credentials", () =>
{
    var f = Fixture(); var a = f.Create("A"); var b = f.Create("B");
    var config = Path.Combine(f.Path(a), "config.toml");
    File.WriteAllText(config, "prompt = '''\nsqlite_home = 'keep prompt'\n'''\n'sqlite_home' = 'old' # retain\n[mcp_servers.x]\ncommand = 'keep'\n");
    File.WriteAllText(Path.Combine(f.Path(a), "auth.json"), "private-a");
    var archive = Path.Combine(f.Settings.DefaultCodexHome, "archived_sessions");
    f.Junctions.Remove(Path.Combine(f.Path(a), "archived_sessions"), archive);
    f.Service.PrepareSharedStore(a, f.Settings);
    var once = File.ReadAllText(config); f.Service.PrepareSharedStore(a, f.Settings);
    Assert(once == File.ReadAllText(config), "Preparation is not idempotent");
    Assert(once.Contains("sqlite_home = 'keep prompt'") && once.Contains("# retain") && once.Contains("command = 'keep'"), "Unrelated TOML changed");
    Assert(once.Contains(System.Text.Json.JsonSerializer.Serialize(f.Settings.DefaultCodexHome)), "Wrong SQLite home");
    Assert(File.ReadAllText(Path.Combine(f.Path(a), "auth.json")) == "private-a", "Authentication changed");
    Assert(PathSafety.Same(f.Junctions.GetTarget(Path.Combine(f.Path(b), "archived_sessions")), archive), "Profiles do not share archive");
});
Test("wrong junction target aborts before deletion", () =>
{
    var f = Fixture(); var a = f.Create("A");
    f.Junctions.Remove(f.Link(a), f.Shared);
    var wrong = Path.Combine(f.Root, "wrong"); Directory.CreateDirectory(wrong);
    f.Junctions.Create(f.Link(a), wrong);
    Reject(() => f.Service.Delete(a, f.Settings));
    Assert(PathSafety.Exists(f.Link(a)) && File.Exists(f.Sentinel), "Changed wrong link or target");
});
Test("missing junction aborts", () =>
{
    var f = Fixture(); var a = f.Create("A"); f.Junctions.Remove(f.Link(a), f.Shared);
    Reject(() => f.Service.Delete(a, f.Settings)); Assert(Directory.Exists(f.Path(a)), "Deleted profile with missing link");
});
Test("broken target aborts", () =>
{
    var f = Fixture(); var a = f.Create("A");
    File.Delete(f.Sentinel); Directory.Delete(f.Shared, false);
    Reject(() => f.Service.Delete(a, f.Settings)); Assert(PathSafety.Exists(f.Link(a)), "Deleted broken junction");
});
Test("nested unexpected junction aborts before detaching sessions", () =>
{
    var f = Fixture(); var a = f.Create("A");
    f.Junctions.Create(Path.Combine(f.Path(a), "unexpected"), f.Shared);
    Reject(() => f.Service.Delete(a, f.Settings));
    Assert(PathSafety.Exists(f.Link(a)) && File.Exists(f.Sentinel), "Partial delete on failed preflight");
});
Test("junction creation never replaces existing data", () =>
{
    var f = Fixture(); var destination = Path.Combine(f.App, "existing"); Directory.CreateDirectory(destination);
    File.WriteAllText(Path.Combine(destination, "keep"), "keep");
    Reject(() => f.Junctions.Create(destination, f.Shared));
    Assert(File.Exists(Path.Combine(destination, "keep")), "Existing data removed");
});
Test("profile traversal and malicious identifiers rejected", () =>
{
    var f = Fixture(); var a = f.Create("A");
    foreach (var value in new[] { "../outside", "profiles/../outside", "D:/outside", "profiles/" + a.Id + "/.." })
    { var bad = new Account { Id = a.Id, ProfilePath = value }; Reject(() => f.Service.Delete(bad, f.Settings)); }
    foreach (var id in new[] { "..", "a/b", "a\\b", "CON", "", "x:stream" })
        Reject(() => PathSafety.Profile(f.App, new Account { Id = id, ProfilePath = "profiles/" + id }));
    Assert(File.Exists(f.Sentinel), "Traversal damaged shared files");
});
Test("reparse profile ancestor rejected", () =>
{
    var f = Fixture(); var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
    f.Junctions.Create(Path.Combine(f.App, "profiles"), outside);
    Reject(() => f.Create("A")); Assert(!Directory.EnumerateFileSystemEntries(outside).Any(), "Wrote through ancestor junction");
});
Test("shared target cannot overlap portable app", () =>
{
    var f = Fixture(); var inside = Path.Combine(f.App, "default"); Directory.CreateDirectory(Path.Combine(inside, "sessions"));
    Reject(() => f.Service.Create("A", "", new AppSettings { DefaultCodexHome = inside }));
});
Test("atomic JSON roundtrip and exclusive manager lock", () =>
{
    var f = Fixture(); var repo = new AccountRepository(f.App); var doc = new AccountDocument();
    doc.Accounts.Add(f.Create("Tài khoản 日本語")); repo.SaveAccounts(doc); repo.SaveSettings(f.Settings);
    Assert(repo.LoadAccounts().Accounts[0].DisplayName == "Tài khoản 日本語", "Unicode failed");
    Assert(repo.LoadSettings().DefaultCodexHome == f.Settings.DefaultCodexHome, "Settings failed");
    using var appLock = repo.AcquireLock(); Reject(() => repo.AcquireLock());
    Assert(!Directory.EnumerateFiles(f.App, "*.tmp").Any(), "Temporary JSON leaked");
});
Test("unsupported JSON versions are preserved", () =>
{
    var f = Fixture(); var file = Path.Combine(f.App, "accounts.json"); File.WriteAllText(file, "{\"version\":99,\"accounts\":[]}");
    Reject(() => new AccountRepository(f.App).LoadAccounts());
    Assert(File.ReadAllText(file).Contains("99"), "Invalid JSON overwritten");
});
Test("status classification never exposes CLI output", () =>
{
    var secret = "sensitive-placeholder";
    Assert(CodexStatusService.Classify(new(0, "Logged in using API key: " + secret)) == "Logged in (local credentials)", "Incorrect status");
    Assert(!CodexStatusService.Classify(new(2, secret)).Contains(secret), "Output leaked");
    Assert(CodexStatusService.Classify(new(1, "Not logged in")) == "Not logged in", "Logged-out classification failed");
    Assert(CodexStatusService.Classify(new(1, "invalid config")) == "Check failed (exit 1)", "Config error called logged-out");
});
Test("quota weekly-only does not invent a five-hour window", () =>
{
    using var json = System.Text.Json.JsonDocument.Parse("""
    {"rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":null,"secondary":{"usedPercent":21,"windowDurationMins":10080,"resetsAt":1900000000}}},
     "rateLimits":{"limitId":"codex","primary":null,"secondary":{"usedPercent":21,"windowDurationMins":10080,"resetsAt":1900000000}}}
    """);
    var quota = CodexQuotaService.Parse(json.RootElement);
    Assert(quota.Lines.Count == 1 && quota.Lines[0].Title.Contains("Weekly") && quota.Lines[0].RemainingPercent == 79, "Weekly quota missing/duplicated");
    Assert(!quota.Lines.Any(l => l.Title.Contains("5 hours")), "Invented short quota");
});
Test("quota includes all buckets, credits, spend limits and reset count", () =>
{
    using var json = System.Text.Json.JsonDocument.Parse("""
    {"rateLimitsByLimitId":{
      "codex":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":1900000000},"secondary":{"usedPercent":35,"windowDurationMins":10080,"resetsAt":1901000000},"credits":{"unlimited":false,"hasCredits":true,"balance":"12.50"}},
      "review":{"limitId":"review","limitName":"Review","primary":{"usedPercent":100,"windowDurationMins":1440,"resetsAt":null},"individualLimit":{"remainingPercent":40,"used":"6","limit":"10","resetsAt":1900000000}}
    },"rateLimitResetCredits":{"availableCount":2}}
    """);
    var quota = CodexQuotaService.Parse(json.RootElement);
    Assert(quota.Lines.Count == 7, "Quota fields were dropped");
    Assert(quota.Lines.Any(l => l.Title.Contains("Review") && l.RemainingPercent == 0), "Other bucket missing");
    Assert(quota.Lines.Any(l => l.Detail == "12.50") && quota.Lines.Any(l => l.Title == "Reset count" && l.Detail == "2"), "Credits missing");
});
Test("quota period is based on duration, missing metrics stay unavailable", () =>
{
    using var json = System.Text.Json.JsonDocument.Parse("""
    {"rateLimits":{"limitId":"codex","primary":{"usedPercent":null,"windowDurationMins":10080,"resetsAt":null},"secondary":null}}
    """);
    var quota = CodexQuotaService.Parse(json.RootElement);
    Assert(quota.Lines.Count == 1 && quota.Lines[0].Title.Contains("Weekly") && quota.Lines[0].RemainingPercent is null, "Null usage treated as zero or wrong period");
});
tests.Add(("Windows PowerShell fallback preserves Unicode native stdin and stdout", async () =>
{
    var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    var deps = await DependencyDetector.DetectAsync([legacy]);
    Assert(deps.PowerShellMajor == 5 && deps.PowerShell == legacy, $"Legacy shell not selected: major={deps.PowerShellMajor}, path={deps.PowerShell}");
    var f = Fixture();
    var script = "'Tiếng Việt 日本語 ☀' | & " + ShellRunner.Quote(Environment.ProcessPath!) + " --echo-stdin";
    var result = await ShellRunner.RunAsync(legacy, script, f.App);
    Assert(result.ExitCode == 0 && result.Output.Contains("Tiếng Việt 日本語 ☀"), "Native UTF-8 pipeline corrupted");
    var preferred = await DependencyDetector.DetectAsync();
    Assert(preferred.PowerShellMajor >= 7, "Installed PowerShell 7 not preferred");
}));
Test("new login copies settings while enforcing file credentials without copying auth", () =>
{
    var f = Fixture(); var original = Path.Combine(f.Settings.DefaultCodexHome, "config.toml");
    var toml = "# Unicode thử nghiệm\r\nmodel = \"example-model\"\r\ncli_auth_credentials_store = \"keyring\"\r\n[mcp_servers.test]\r\ncommand = \"test\"\r\n";
    File.WriteAllText(original, toml); File.WriteAllText(Path.Combine(f.Settings.DefaultCodexHome, "auth.json"), "test-only");
    var a = f.Service.Create("New login", "", f.Settings, false);
    Assert(File.ReadAllText(Path.Combine(f.Path(a), "config.toml")).EndsWith(toml.Replace("\"keyring\"", "\"file\"")), "Unrelated TOML changed or file credentials not enforced");
    Assert(!File.Exists(Path.Combine(f.Path(a), "auth.json")), "New login copied credentials without opt-in");
    Assert(File.ReadAllText(original) == toml, "Original config changed");
});
Test("credential config ignores keys in multiline prompts and preserves comments", () =>
{
    var input = "prompt = \"\"\"\ncli_auth_credentials_store = 'keyring'\n\"\"\"\n'cli_auth_credentials_store' = 'auto' # keep\n[mcp_servers.x]\ncommand = 'unchanged'\n";
    var result = CredentialConfig.UseFile(input);
    Assert(result.Contains("cli_auth_credentials_store = 'keyring'\n\"\"\"") && result.Contains("'cli_auth_credentials_store' = \"file\" # keep"), "Edited prompt or lost comment");
    Assert(result.Contains("command = 'unchanged'"), "Unrelated settings changed");
    var table = "[mcp_servers.x]\ncommand = 'tool'\n";
    Assert(CredentialConfig.UseFile(table).StartsWith("cli_auth_credentials_store = \"file\"\n["), "Inserted root key inside table");
});
Test("explicit account copy creates independent auth and preserves original on delete", () =>
{
    var f = Fixture(); var source = Path.Combine(f.Settings.DefaultCodexHome, "auth.json");
    const string fake = "{\"testOnly\":\"not-a-real-credential\"}"; File.WriteAllText(source, fake);
    var a = f.Service.Create("Copy", "", f.Settings, true);
    Assert(File.ReadAllText(Path.Combine(f.Path(a), "auth.json")) == fake, "Auth not copied");
    File.WriteAllText(Path.Combine(f.Path(a), "auth.json"), "changed-private-copy");
    Assert(File.ReadAllText(source) == fake, "Auth shares file identity with original");
    f.Service.Delete(a, f.Settings);
    Assert(File.ReadAllText(source) == fake && File.Exists(f.Sentinel), "Original damaged by delete");
});
Test("missing source auth stops explicit copy before creating profile", () =>
{
    var f = Fixture(); Reject(() => f.Service.Create("Copy", "", f.Settings, true));
    Assert(!Directory.Exists(Path.Combine(f.App, "profiles")), "Created partial profile without source credentials");
});
Test("source auth cannot be written while copied", () =>
{
    var f = Fixture(); var source = Path.Combine(f.Settings.DefaultCodexHome, "auth.json"); File.WriteAllText(source, "test-only");
    using var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.None);
    Reject(() => f.Service.Create("Copy", "", f.Settings, true));
    Assert(!Directory.Exists(Path.Combine(f.App, "profiles")), "Created partial profile during source write");
});
Test("Apply replaces only default auth and preserves both session trees and configs", () =>
{
    var f = Fixture(); var a = f.Create("Apply");
    var source = Path.Combine(f.Path(a), "auth.json"); var target = Path.Combine(f.Settings.DefaultCodexHome, "auth.json");
    File.WriteAllText(source, "{\"testOnly\":\"selected\"}"); File.WriteAllText(target, "{\"testOnly\":\"original\"}");
    var config = Path.Combine(f.Settings.DefaultCodexHome, "config.toml"); File.WriteAllText(config, "model = 'unchanged'");
    f.Service.ApplyAuthentication(a, f.Settings);
    Assert(File.ReadAllText(target) == File.ReadAllText(source), "Apply did not replace auth");
    Assert(File.ReadAllText(config) == "model = 'unchanged'" && File.ReadAllText(f.Sentinel) == "shared-do-not-delete", "Apply touched config/sessions");
    Assert(!Directory.EnumerateFiles(f.Settings.DefaultCodexHome, "*.tmp").Any(), "Apply temporary file leaked");
});
Test("Apply rejects invalid source auth without altering the default login", () =>
{
    var f = Fixture(); var a = f.Create("Apply"); var target = Path.Combine(f.Settings.DefaultCodexHome, "auth.json");
    File.WriteAllText(target, "{\"testOnly\":\"keep\"}"); File.WriteAllText(Path.Combine(f.Path(a), "auth.json"), "not JSON");
    Reject(() => f.Service.ApplyAuthentication(a, f.Settings));
    Assert(File.ReadAllText(target).Contains("keep") && !Directory.EnumerateFiles(f.Settings.DefaultCodexHome, "*.tmp").Any(), "Failed Apply damaged default auth");
});
Test("Apply leaves locked default auth untouched", () =>
{
    var f = Fixture(); var a = f.Create("Apply"); var target = Path.Combine(f.Settings.DefaultCodexHome, "auth.json");
    File.WriteAllText(target, "{\"testOnly\":\"keep\"}"); File.WriteAllText(Path.Combine(f.Path(a), "auth.json"), "{\"testOnly\":\"new\"}");
    using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        Reject(() => f.Service.ApplyAuthentication(a, f.Settings));
    Assert(File.ReadAllText(target).Contains("keep"), "Locked auth altered");
});
Test("session projects use metadata cwd, deduplicate and mark missing directories", () =>
{
    var f = Fixture(); var project = Path.Combine(f.Root, "Dự án có dấu"); Directory.CreateDirectory(project);
    var missing = Path.Combine(f.Root, "missing-project");
    string Header(string path) => System.Text.Json.JsonSerializer.Serialize(new { type = "session_meta", payload = new { cwd = path } });
    File.WriteAllText(Path.Combine(f.Shared, "one.jsonl"), Header(project) + "\n{broken body ignored");
    File.WriteAllText(Path.Combine(f.Shared, "two.jsonl"), Header(project.ToUpperInvariant()));
    File.WriteAllText(Path.Combine(f.Shared, "missing.jsonl"), Header(missing));
    File.WriteAllText(Path.Combine(f.Shared, "unrelated.jsonl"), System.Text.Json.JsonSerializer.Serialize(new { type = "event_msg", payload = new { cwd = f.App } }));
    var result = new SessionProjectCatalog().Read(f.Shared);
    Assert(result.Projects.Count == 2, "Project catalog did not deduplicate/filter metadata");
    Assert(result.Projects[0].Exists && result.Projects[0].SessionCount == 2, "Existing project not first or incorrect session count");
    Assert(!result.Projects[1].Exists, "Missing directory not marked");
});
Test("session projects sort by name then path with missing directories last", () =>
{
    var f = Fixture();
    var paths = new[] { Path.Combine(f.Root, "z", "Alpha"), Path.Combine(f.Root, "a", "beta"), Path.Combine(f.Root, "a", "Alpha"), Path.Combine(f.Root, "missing") };
    for (var i = 0; i < paths.Length; i++)
    {
        if (i < 3) Directory.CreateDirectory(paths[i]);
        File.WriteAllText(Path.Combine(f.Shared, i + ".jsonl"), System.Text.Json.JsonSerializer.Serialize(new { type = "session_meta", payload = new { cwd = paths[i] } }));
    }
    var result = new SessionProjectCatalog().Read(f.Shared);
    Assert(result.Projects.Select(p => p.Directory).SequenceEqual(new[] { paths[2], paths[0], paths[1], paths[3] }), "Incorrect alphabetical ordering");
});
Test("catalog normalizes device paths and existing WSL drive mounts only", () =>
{
    var f = Fixture();
    Assert(PathSafety.Same(SessionProjectCatalog.NormalizeWorkingDirectory(@"\\?\" + f.App), f.App), "Device prefix not normalized");
    var mount = "/mnt/" + char.ToLowerInvariant(f.App[0]) + f.App[2..].Replace('\\', '/');
    Assert(PathSafety.Same(SessionProjectCatalog.NormalizeWorkingDirectory(mount), f.App), "Existing WSL mount not mapped");
    Reject(() => SessionProjectCatalog.NormalizeWorkingDirectory(mount + "/missing"));
    Assert(SessionProjectCatalog.NormalizeWorkingDirectory(@"\\?\UNC\server\share") == @"\\server\share", "UNC device prefix not normalized");
    Assert(SessionProjectCatalog.NormalizeWorkingDirectory(@"\\?\UNC\wsl$\Ubuntu\home\user\project") == @"\\wsl.localhost\Ubuntu\home\user\project", "WSL share not normalized");
    var broken = @"C:\mnt\" + char.ToLowerInvariant(f.App[0]) + f.App[2..];
    Assert(PathSafety.Same(SessionProjectCatalog.NormalizeWorkingDirectory(broken), f.App), "Historical Windows mount not repaired");
    Reject(() => SessionProjectCatalog.NormalizeWorkingDirectory(@"\\.\pipe\invalid"));
    Reject(() => SessionProjectCatalog.NormalizeWorkingDirectory(@"\\server\share\..\other"));
});
Test("session catalog skips nested links and supports large metadata headers", () =>
{
    var f = Fixture(); var outside = Path.Combine(f.Root, "external"); Directory.CreateDirectory(outside);
    f.Junctions.Create(Path.Combine(f.Shared, "link"), outside);
    var header = System.Text.Json.JsonSerializer.Serialize(new { type = "session_meta", payload = new { cwd = f.App, base_instructions = new string('x', 160000) } });
    File.WriteAllText(Path.Combine(f.Shared, "large.jsonl"), header);
    File.WriteAllText(Path.Combine(outside, "hidden.jsonl"), System.Text.Json.JsonSerializer.Serialize(new { type = "session_meta", payload = new { cwd = outside } }));
    var result = new SessionProjectCatalog().Read(f.Shared);
    Assert(result.Projects.Count == 1 && PathSafety.Same(result.Projects[0].Directory, f.App), "Catalog followed junction or failed large metadata");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { new SessionProjectCatalog().Read(f.Shared, canceled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
});
Test("workspace catalog separates explicit projectless chats and keeps missing projects", () =>
{
    var f = Fixture();
    var noProject = Guid.NewGuid().ToString(); var reassigned = Guid.NewGuid().ToString(); var canonical = Guid.NewGuid().ToString();
    var projectlessDirectory = Path.Combine(f.Root, "chat-files"); Directory.CreateDirectory(projectlessDirectory);
    var missing = Path.Combine(f.Root, "missing-project");
    var state = Path.Combine(f.Settings.DefaultCodexHome, ".codex-global-state.json");
    File.WriteAllText(state, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> {
        ["projectless-thread-ids"] = new[] { noProject, reassigned, canonical },
        ["thread-project-assignments"] = new Dictionary<string, object> { [reassigned] = new { projectId = "assigned-project" } },
        ["local-projects"] = new { saved = new { rootPaths = new[] { f.App } } }
    }));
    var before = File.ReadAllText(state);
    var result = WorkspaceCatalogService.Build(f.Settings.DefaultCodexHome, [
        new(noProject, "No project", projectlessDirectory, DateTime.UtcNow),
        new(reassigned, "Assigned again", f.App, DateTime.UtcNow),
        new(canonical, "Server assignment wins", f.App, DateTime.UtcNow, "server-project"),
        new(Guid.NewGuid().ToString(), "Missing project", missing, DateTime.UtcNow),
        new(Guid.NewGuid().ToString(), "No cwd", null, DateTime.UtcNow),
        new(Guid.NewGuid().ToString(), "Manager chat", Path.Combine(WorkspaceCatalogService.ChatRoot(f.Settings.DefaultCodexHome), "one"), DateTime.UtcNow)
    ]);
    Assert(result.Sessions.Count(s => s.IsProjectless) == 3, "Incorrect projectless classification");
    Assert(result.Projects.Count == 2 && result.Projects[0].SessionCount == 2, "Folderless chat polluted project list");
    Assert(result.Projects.Any(p => p.Directory == missing && !p.Exists), "Missing project became a folderless chat");
    Assert(File.ReadAllText(state) == before, "Desktop state changed during discovery");
});
Test("workspace metadata failures preserve current sessions and legacy headers allow late cwd", () =>
{
    var f = Fixture();
    File.WriteAllText(Path.Combine(f.Settings.DefaultCodexHome, ".codex-global-state.json"), "{broken");
    var result = WorkspaceCatalogService.Build(f.Settings.DefaultCodexHome, [new(Guid.NewGuid().ToString(), "Current", f.App, DateTime.UtcNow)]);
    Assert(result.Projects.Count == 1 && result.Warning is not null, "Bad Desktop state lost current sessions");
    var id = Guid.NewGuid().ToString();
    File.WriteAllText(Path.Combine(f.Shared, "late-cwd.jsonl"), System.Text.Json.JsonSerializer.Serialize(new {
        payload = new { base_instructions = new string('x', 300000), cwd = f.App, id }, type = "session_meta"
    }) + "\n{invalid conversation ignored");
    var legacy = new SessionProjectCatalog().Read(f.Shared);
    Assert(legacy.Projects.Count == 1 && legacy.Sessions.Single().Id == id, "Late metadata after 128 KiB lost");
});
Test("projectless launch uses Desktop layout and preserves existing chat files", () =>
{
    var f = Fixture(); var id = Guid.NewGuid().ToString();
    var saved = WorkspaceCatalogService.ResolveDirectory(new(f.App, id, true), f.Settings.DefaultCodexHome);
    Assert(saved == f.App, "Existing chat workspace changed");
    var selection = new CliSelection(Path.Combine(f.Root, "missing"), id, true);
    var path = WorkspaceCatalogService.ResolveDirectory(selection, f.Settings.DefaultCodexHome, f.Root);
    Assert(Directory.Exists(path) && PathSafety.Within(path, DesktopChatWorkspace.Root(f.Root)), "Chat did not use Desktop's Documents/Codex root");
    Assert(Directory.Exists(Path.Combine(path, "work")) && Directory.Exists(Path.Combine(path, "outputs")), "Desktop split folders missing");
    Assert(WorkspaceCatalogService.ResolveDirectory(selection with { Directory = path }, f.Settings.DefaultCodexHome, f.Root) == path, "Resume workspace changed");
    var fresh = WorkspaceCatalogService.ResolveDirectory(new(null, IsProjectless: true), f.Settings.DefaultCodexHome, f.Root);
    Assert(fresh != path, "New chat reused an old workspace");
    Reject(() => WorkspaceCatalogService.ResolveDirectory(selection with { IsProjectless = false }, f.Settings.DefaultCodexHome));
});
Test("Desktop identifies CLI chats by cwd without global-state mutation", () =>
{
    var f = Fixture();
    var root = DesktopChatWorkspace.Root(f.Root);
    var path = DesktopChatWorkspace.Create(f.Root, new DateTime(2026, 9, 27));
    Assert(path == Path.Combine(root, "2026-09-27", "new-chat"), "Unexpected Desktop folder format");
    File.WriteAllText(Path.Combine(path, "keep.txt"), "keep");
    var other = DesktopChatWorkspace.Create(f.Root, new DateTime(2026, 9, 27));
    Assert(other.EndsWith("new-chat-2") && File.ReadAllText(Path.Combine(path, "keep.txt")) == "keep", "Existing chat folder reused");
    var paths = new[] { path, @"\\?\" + path, Path.Combine(root, "2026-09-26-old-chat"), Path.Combine(root, "ordinary-project") };
    var result = WorkspaceCatalogService.Build(f.Settings.DefaultCodexHome, paths.Select(p =>
        new CatalogSession(Guid.NewGuid().ToString(), "Chat", p, DateTime.UtcNow)).ToArray());
    Assert(result.Sessions.Count(s => s.IsProjectless) == 3 && result.Projects.Count == 1, "Desktop path classifier differs");
    Assert(!File.Exists(Path.Combine(f.Settings.DefaultCodexHome, ".codex-global-state.json")), "Discovery modified Desktop state");
    Assert(DesktopChatWorkspace.RecognizedRoot(Path.Combine(path, "outputs")) is null, "Nested project mistaken for chat root");
    var linkedHome = Path.Combine(f.Root, "linked-home"); Directory.CreateDirectory(linkedHome);
    f.Junctions.Create(Path.Combine(linkedHome, "Documents"), f.App);
    Reject(() => DesktopChatWorkspace.Create(linkedHome));
});
tests.Add(("current thread discovery paginates, deduplicates and uses shared state", async () =>
{
    var f = Fixture();
    var deps = new Dependencies(null, null, false, Environment.ProcessPath!);
    var result = await new WorkspaceCatalogService().ReadAsync(deps, f.App, f.Settings.DefaultCodexHome);
    Assert(result.Warning is null && result.Sessions.Count == 2 && result.Projects.Single().Directory == f.App, "Current session discovery failed");
    Assert(result.Sessions.Count(s => s.IsProjectless) == 1, "No-cwd session lost");
    File.WriteAllText(Path.Combine(f.App, "catalog-mode.txt"), "repeat");
    try { await new CodexThreadReader().ReadAsync(deps, f.App, f.Settings.DefaultCodexHome); throw new Exception("Repeated cursor accepted"); }
    catch (IOException) { }
}));
tests.Add(("unavailable thread API falls back but cancellation stops discovery", async () =>
{
    var f = Fixture(); var deps = new Dependencies(null, null, false, Environment.ProcessPath!);
    File.WriteAllText(Path.Combine(f.App, "catalog-mode.txt"), "error");
    File.WriteAllText(Path.Combine(f.Shared, "old.jsonl"), System.Text.Json.JsonSerializer.Serialize(new {
        type = "session_meta", payload = new { cwd = f.App, id = Guid.NewGuid().ToString() }
    }));
    var result = await new WorkspaceCatalogService().ReadAsync(deps, f.App, f.Settings.DefaultCodexHome);
    Assert(result.Warning is not null && result.Projects.Single().Directory == f.App, "Legacy fallback missing");
    File.WriteAllText(Path.Combine(f.App, "catalog-mode.txt"), "cancel");
    using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
    try { await new WorkspaceCatalogService().ReadAsync(deps, f.App, f.Settings.DefaultCodexHome, canceled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
}));
Test("pinned directory cannot be renamed during deletion checks", () =>
{
    var f = Fixture(); var a = f.Create("A");
    using var pin = JunctionService.PinDirectory(f.Path(a));
    Reject(() => Directory.Move(f.Path(a), f.Path(a) + "-moved"));
    Assert(PathSafety.Exists(f.Link(a)), "Pinned directory changed");
});
Test("malformed JSON is not overwritten", () =>
{
    var f = Fixture(); var file = Path.Combine(f.App, "accounts.json"); File.WriteAllText(file, "{broken");
    try { new AccountRepository(f.App).LoadAccounts(); throw new Exception("Malformed JSON accepted"); }
    catch (System.Text.Json.JsonException) { }
    Assert(File.ReadAllText(file) == "{broken", "Malformed JSON replaced");
});
Test("manual refresh isolates accounts, preserves other controls and scroll, and cancels on close", () =>
{
    var f = Fixture();
    var items = Enumerable.Range(1, 8).Select(i => f.Create("Account " + i)).ToArray();
    foreach (var account in items) account.Quota = new QuotaSnapshot { Lines = [new("codex · Weekly", 80, 2000000000)] };
    var repo = new AccountRepository(f.App); repo.SaveSettings(f.Settings); repo.SaveAccounts(new AccountDocument { Accounts = items.ToList() });
    var gates = items.ToDictionary(a => a.Id, _ => new TaskCompletionSource<AccountRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously));
    var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
    var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Exception? error = null;
    var thread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            using var form = new CodexAccountManager.MainForm(repo, async (path, token) =>
            {
                var id = Path.GetFileName(path); calls.AddOrUpdate(id, 1, (_, n) => n + 1);
                try { return await gates[id].Task.WaitAsync(token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            });
            void Until(Func<bool> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                Assert(condition(), "Timed out waiting for UI refresh"); Application.DoEvents();
            }
            IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
            form.Opacity = 0; form.ShowInTaskbar = false;
            // Use the real WinForms message loop so async button handlers resume on the UI thread.
            form.Shown += (_, _) => form.BeginInvoke(new Action(() =>
            {
                try
                {
            Until(() => form.Ready.IsCompleted);
            var flow = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<FlowLayoutPanel>().Single();
            Control Card(Account a) => flow.Controls.Cast<Control>().Single(c => Equals(c.Tag, a.Id));
            Button Refresh(Account a) => Card(a).Controls.Find("refresh", true).OfType<Button>().Single();
            var untouched = Card(items[2]); var untouchedControls = Descendants(untouched).ToArray();
            Refresh(items[0]).Focus(); Application.DoEvents();
            var beforeStart = flow.AutoScrollPosition;
            Refresh(items[0]).PerformClick(); Until(() => calls.ContainsKey(items[0].Id));
            Assert(flow.AutoScrollPosition == beforeStart, "Disabling the focused Refresh moved scroll");
            Assert(!Card(items[0]).Enabled && Card(items[1]).Enabled && flow.Enabled, "Refresh locked another account or scrolling");
            Assert(Descendants(form).OfType<Button>().Single(b => b.Text == "+ Add account").Enabled, "Refresh locked header input");
            Refresh(items[1]).PerformClick(); Until(() => calls.ContainsKey(items[1].Id));
            Refresh(items[0]).PerformClick();
            Assert(calls[items[0].Id] == 1 && !Card(items[1]).Enabled, "Duplicate refresh or second account not locked");
            flow.AutoScrollPosition = new System.Drawing.Point(0, 100); Application.DoEvents(); var scroll = flow.AutoScrollPosition;
            Assert(scroll.Y < 0, "Fixture did not exercise scrolling");
            gates[items[0].Id].SetResult(new("Logged in (local credentials)", new QuotaSnapshot { Lines = [new("codex · Weekly", 55, 2000000000)] }, null));
            Until(() => Card(items[0]).Enabled);
            Assert(!Card(items[1]).Enabled && ReferenceEquals(Card(items[2]), untouched), "Completing refresh replaced or unlocked another card");
            Assert(untouchedControls.SequenceEqual(Descendants(untouched)) && flow.AutoScrollPosition == scroll, "Refresh rebuilt other controls or moved scroll");
            Assert(repo.LoadAccounts().Accounts.Single(a => a.Id == items[0].Id).Quota!.Lines.Single().RemainingPercent == 55, "Refresh result not saved");
            gates[items[1].Id].SetException(new IOException("Synthetic quota failure"));
            var positions = flow.Controls.Cast<Control>().Select(c => c.Bounds).ToArray();
            Until(() => Card(items[1]).Enabled);
            Assert(flow.AutoScrollPosition == scroll && positions.SequenceEqual(flow.Controls.Cast<Control>().Select(c => c.Bounds)), "An error row moved the list or account buttons");
            Assert(ReferenceEquals(Card(items[2]), untouched) && untouchedControls.SequenceEqual(Descendants(untouched)), "Failed refresh rebuilt other accounts");
            Assert(Card(items[0]).Height == Card(items[1]).Height, "Quota/error row height mismatch");
            Assert(Descendants(Card(items[1])).OfType<Label>().Any(l => l.Text.StartsWith("Cached quota")), "Failed refresh lost cached quota");
            // Real focus, both layouts, and viewport changes while the request is still pending.
            foreach (var width in new[] { 940, 620 })
            foreach (var offset in new[] { 0, 100, 100000 })
            {
                var scale = form.DeviceDpi / 96f;
                form.ClientSize = new System.Drawing.Size((int)(width * scale), (int)(565 * scale)); Application.DoEvents();
                Assert((Card(items[0]).Top == Card(items[1]).Top) == (width == 940), "Fixture did not exercise the intended column layout");
                var account = items[4]; var card = Card(account); var refresh = Refresh(account);
                var buttons = Descendants(card).OfType<Button>().ToArray();
                gates[account.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var previousCalls = calls.GetValueOrDefault(account.Id);
                refresh.Focus(); Application.DoEvents();
                flow.AutoScrollPosition = new System.Drawing.Point(0, offset); Application.DoEvents();
                var before = flow.AutoScrollPosition;
                var bounds = flow.Controls.Cast<Control>().Select(c => c.Bounds).ToArray();
                refresh.PerformClick(); Until(() => calls.GetValueOrDefault(account.Id) == previousCalls + 1);
                Assert(flow.AutoScrollPosition == before && bounds.SequenceEqual(flow.Controls.Cast<Control>().Select(c => c.Bounds)), "Focused refresh moved accounts on start");
                // Scrolling during the request must win over the position at request start.
                flow.AutoScrollPosition = new System.Drawing.Point(0, offset == 0 ? 100000 : 0); Application.DoEvents();
                before = flow.AutoScrollPosition; bounds = flow.Controls.Cast<Control>().Select(c => c.Bounds).ToArray();
                var lines = Enumerable.Range(0, offset == 100 ? 1 : 5).Select(i => new QuotaLine("Window " + i, 55, 2000000000)).ToList();
                gates[account.Id].SetResult(new("Logged in (local credentials)", new QuotaSnapshot { Lines = lines }, null));
                Until(() => Card(account).Enabled);
                Assert(ReferenceEquals(Card(account), card) && ReferenceEquals(Refresh(account), refresh) && buttons.SequenceEqual(Descendants(card).OfType<Button>()), "Refresh replaced the card or its buttons");
                Assert(flow.AutoScrollPosition == before && bounds.SequenceEqual(flow.Controls.Cast<Control>().Select(c => c.Bounds)), "Changed quota rows shifted accounts at completion");
                var viewport = card.Controls.Find("quotaViewport", true).OfType<Panel>().Single();
                Assert(Descendants(viewport).OfType<Label>().Count() == lines.Count, "Quota overflow lost rows");
                if (lines.Count == 5)
                {
                    Assert(viewport.VerticalScroll.Visible, "Additional quota rows are inaccessible");
                    viewport.AutoScrollPosition = new System.Drawing.Point(0, 10000); Application.DoEvents();
                    Assert(viewport.AutoScrollPosition.Y < 0 && flow.AutoScrollPosition == before, "Quota overflow scrolling moved the list");
                }
            }
            flow.AutoScrollPosition = new System.Drawing.Point(0, 0);
            Refresh(items[^1]).Focus(); Application.DoEvents();
            Assert(flow.AutoScrollPosition.Y < 0, "Keyboard focus no longer reveals off-screen accounts");
            Refresh(items[3]).PerformClick(); Until(() => calls.ContainsKey(items[3].Id));
            form.Close(); Until(() => canceled.Task.IsCompleted);
            Assert(form.IsDisposed, "Refresh prevented closing the app");
                }
                catch (Exception ex) { error = ex; }
                finally { if (!form.IsDisposed) form.Close(); }
            }));
            Application.Run(form);
        }
        catch (Exception ex) { error = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if (error is not null) throw error;
});
Test("WinForms renders populated and empty account lists", () =>
{
    var f = Fixture(); var a = f.Create("Work account — thử nghiệm"); var b = f.Create("Personal account");
    a.LoginStatus = "Logged in (local credentials)"; a.LastCheckedAt = DateTimeOffset.Now;
    a.Quota = new QuotaSnapshot { PlanType = "Plus", Lines = [new("codex · Weekly", 72, 1900000000)] };
    b.Quota = new QuotaSnapshot { PlanType = "Pro", Lines = [new("codex · 5 hours", 5, 1900000000), new("codex · Weekly", 81, 1901000000), new("Review · Weekly", 44, 1901000000)] };
    a.Note = "UI fixture — no real credentials";
    var repo = new AccountRepository(f.App); repo.SaveSettings(f.Settings);
    repo.SaveAccounts(new AccountDocument { Accounts = [a, b] });
    Exception? error = null;
    var thread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            using var form = new CodexAccountManager.MainForm(repo);
            void Prepare(CodexAccountManager.MainForm window)
            {
                window.Opacity = 0; window.ShowInTaskbar = false; window.Show();
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!window.Ready.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                Assert(window.Ready.IsCompleted, "UI startup did not complete");
                window.PerformLayout(); Application.DoEvents();
            }
            Prepare(form);
            var flow = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<FlowLayoutPanel>().Single();
            Assert(flow.Controls.Count == 2 && flow.Controls[0].Height == flow.Controls[1].Height, "Card heights differ");
            Assert(flow.Controls[0].BackColor == System.Drawing.Color.FromArgb(245, 218, 140) && flow.Controls[1].BackColor == System.Drawing.Color.FromArgb(225, 228, 231), "Card colors incorrect");
            var output = System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts");
            Directory.CreateDirectory(output);
            using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(System.IO.Path.Combine(output, "ui-populated.png")); }
            repo.SaveAccounts(new AccountDocument());
            using var empty = new CodexAccountManager.MainForm(repo);
            Prepare(empty);
            using var image = new System.Drawing.Bitmap(empty.Width, empty.Height);
            empty.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, empty.Width, empty.Height));
            image.Save(System.IO.Path.Combine(output, "ui-empty.png"));
            void CaptureDialog(Form dialog, string file)
            {
                using (dialog)
                {
                    dialog.Opacity = 0; dialog.Show(); Application.DoEvents();
                    if (dialog is CodexAccountManager.ProjectPickerForm picker)
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(10);
                        while (!picker.Ready.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                        Assert(picker.Ready.IsCompleted, "Project picker did not finish loading");
                        Application.DoEvents();
                    }
                    using var capture = new System.Drawing.Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(capture, new System.Drawing.Rectangle(0, 0, dialog.Width, dialog.Height));
                    capture.Save(System.IO.Path.Combine(output, file));
                    if (dialog is CodexAccountManager.ProjectPickerForm chatPicker)
                    {
                        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
                        var controls = Descendants(dialog).ToArray();
                        controls.OfType<TabControl>().Single().SelectedIndex = 1; Application.DoEvents();
                        dialog.DrawToBitmap(capture, new System.Drawing.Rectangle(0, 0, dialog.Width, dialog.Height));
                        capture.Save(System.IO.Path.Combine(output, "ui-projectless-picker.png"));
                        var resumeButton = controls.OfType<Button>().Single(b => b.Text == "Resume chat");
                        Assert(resumeButton.Enabled, "Projectless resume is disabled");
                        resumeButton.PerformClick();
                        Assert(chatPicker.Selection is { IsProjectless: true, SessionId: not null, Directory: null }, "Projectless resume selection lost");
                    }
                    dialog.Close();
                }
            }
            CaptureDialog(new CodexAccountManager.AddAccountForm(), "ui-add-account.png");
            CaptureDialog(new CodexAccountManager.AccountDetailsForm(a, f.Path(a), f.Shared), "ui-account-details.png");
            CaptureDialog(new CodexAccountManager.AdvancedSettingsForm(f.Settings, new(null, null, false)), "ui-settings.png");
            File.WriteAllText(Path.Combine(f.Shared, "project.jsonl"), System.Text.Json.JsonSerializer.Serialize(new { type = "session_meta", payload = new { cwd = f.App } }));
            var catalog = WorkspaceCatalogService.Build(f.Settings.DefaultCodexHome, [
                new(Guid.NewGuid().ToString(), "Update the project", f.App, DateTime.UtcNow),
                new(Guid.NewGuid().ToString(), "Plan a presentation", null, DateTime.UtcNow)]);
            CaptureDialog(new CodexAccountManager.ProjectPickerForm("Work account", _ => Task.FromResult(catalog)), "ui-project-picker.png");
        }
        catch (Exception ex) { error = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if (error is not null) throw error;
});
tests.Add(("PowerShell argument quoting and process environment isolation", async () =>
{
    var f = Fixture(); var deps = await DependencyDetector.DetectAsync(); deps.Require();
    var special = Path.Combine(f.Root, "Tài khoản ' ; $() & space"); Directory.CreateDirectory(special);
    var fake = Path.Combine(special, "fake codex.ps1");
    File.WriteAllText(fake, "[Console]::Out.WriteLine($env:CODEX_HOME); [Console]::Out.WriteLine($env:CODEX_SQLITE_HOME); [Console]::Out.WriteLine(($args -join '|')); if ($env:OPENAI_API_KEY) { exit 23 }");
    var original = Environment.GetEnvironmentVariable("CODEX_HOME");
    var originalKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    try
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-only");
        var script = CodexProcessLauncher.BuildScript(fake, special, special, CodexAction.Resume, f.Settings.DefaultCodexHome);
        var result = await ShellRunner.RunAsync(deps.PowerShell!, script, special);
        Assert(result.ExitCode == 0 && result.Output.Contains(special) && result.Output.Contains("resume|--all"), "Quoting or isolation failed");
        Assert(result.Output.Contains(f.Settings.DefaultCodexHome) && result.Output.Contains("sqlite_home="), "Shared SQLite override missing");
        var sessionId = Guid.NewGuid().ToString();
        var resumeScript = CodexProcessLauncher.BuildScript(fake, special, special, CodexAction.Resume, f.Settings.DefaultCodexHome, sessionId);
        var resumed = await ShellRunner.RunAsync(deps.PowerShell!, resumeScript, special);
        Assert(resumed.ExitCode == 0 && resumed.Output.Contains("resume|--all|" + sessionId + "|--cd|" + special), "Exact session ID or cwd override lost");
        var prompt = "--tiếp tục ' $() &\nDòng hai";
        using var context = JsonDocument.Parse("{\"model\":\"fixture-model\",\"effort\":\"high\",\"approval_policy\":\"never\",\"sandbox_policy\":{\"type\":\"danger-full-access\"}}");
        var prompted = await ShellRunner.RunAsync(deps.PowerShell!, CodexProcessLauncher.BuildScript(fake, special, special,
            CodexAction.Resume, f.Settings.DefaultCodexHome, sessionId, prompt, context.RootElement), special);
        Assert(prompted.ExitCode == 0 && prompted.Output.Contains("|" + prompt)
            && prompted.Output.Contains("|--model|fixture-model") && prompted.Output.Contains("|--ask-for-approval|never")
            && prompted.Output.Contains("|--sandbox|danger-full-access"), "Prompt quoting or saved policy lost");
        using var nativeContext = JsonDocument.Parse("{\"model\":\"--echo-arguments\"}");
        var native = await ShellRunner.RunAsync(deps.PowerShell!, CodexProcessLauncher.BuildScript(Environment.ProcessPath!, special, special,
            CodexAction.Resume, f.Settings.DefaultCodexHome, sessionId, prompt, nativeContext.RootElement), special);
        using var nativeArgs = JsonDocument.Parse(native.Output);
        Assert(native.ExitCode == 0 && nativeArgs.RootElement.EnumerateArray().Last().GetString() == prompt
            && nativeArgs.RootElement.EnumerateArray().Select(a => a.GetString()).Contains("--"), "Native prompt argument or separator lost");
        Reject(() => CodexProcessLauncher.BuildScript(fake, special, special, CodexAction.Resume, f.Settings.DefaultCodexHome, "bad'; exit 99"));
        Assert(Environment.GetEnvironmentVariable("CODEX_HOME") == original, "Parent environment changed");
    }
    finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalKey); }
}));
tests.Add(("interactive launches repair inherited dumb TERM and preserve other terminal settings", async () =>
{
    var f = Fixture(); var deps = await DependencyDetector.DetectAsync(); deps.Require();
    var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    var fake = Path.Combine(f.Root, "fake-terminal.ps1");
    File.WriteAllText(fake, "[Console]::Out.WriteLine('TERM=' + $env:TERM); exit 0");
    var originalTerm = Environment.GetEnvironmentVariable("TERM");
    foreach (var shell in new[] { deps.PowerShell!, legacy }.Distinct(StringComparer.OrdinalIgnoreCase))
    foreach (var action in new[] { CodexAction.Open, CodexAction.Resume, CodexAction.Login })
    foreach (var term in new[] { "dumb", "DUMB", "xterm-256color", "vt100", null })
    {
        var setup = term is null ? "Remove-Item Env:TERM -ErrorAction SilentlyContinue; "
            : "$env:TERM = " + ShellRunner.Quote(term) + "; ";
        var script = setup + CodexProcessLauncher.BuildScript(fake, f.App, f.App, action, f.Settings.DefaultCodexHome);
        var result = await ShellRunner.RunAsync(shell, script, f.App);
        var expected = string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase) ? "xterm-256color" : term;
        Assert(result.ExitCode == 0 && result.Output.Trim() == "TERM=" + expected,
            $"Unexpected terminal environment for {shell}, {action}, TERM={term ?? "unset"}: {result.Output} {result.Error}");
    }
    Assert(Environment.GetEnvironmentVariable("TERM") == originalTerm, "Parent terminal environment changed");
}));
tests.Add(("login exits only on success in PowerShell 7 and 5.1", async () =>
{
    var f = Fixture(); var deps = await DependencyDetector.DetectAsync(); deps.Require();
    var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    foreach (var shell in new[] { deps.PowerShell!, legacy }.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        foreach (var code in new[] { 0, 1 })
        {
            var fake = Path.Combine(f.Root, "fake-login.ps1"); File.WriteAllText(fake, "exit " + code);
            var script = CodexProcessLauncher.BuildScript(fake, f.App, f.App, CodexAction.Login, f.Settings.DefaultCodexHome) + "; [Console]::Out.WriteLine('terminal-kept'); exit 37";
            var result = await ShellRunner.RunAsync(shell, script, f.App);
            Assert(code == 0 ? result.ExitCode == 0 && !result.Output.Contains("terminal-kept") : result.ExitCode == 37 && result.Output.Contains("terminal-kept"), "Login exit behavior incorrect");
        }
    }
}));
tests.Add(("official CLI status against a fresh isolated profile", async () =>
{
    var f = Fixture(); var a = f.Create("Empty"); var deps = await DependencyDetector.DetectAsync(); deps.Require();
    Assert(deps.LoginStatusSupported, "Installed CLI login status not detected");
    var state = await new CodexStatusService().CheckAsync(deps, f.Path(a));
    Assert(state == "Not logged in", "Unexpected empty profile status: " + state);
    Assert(!File.Exists(Path.Combine(f.Path(a), "auth.json")), "Check created credentials");
    f.Service.Delete(a, f.Settings);
    Assert(File.ReadAllText(f.Sentinel) == "shared-do-not-delete", "Shared fixture changed");
}));

Test("cached quota labels render in English without mutating stored values", () =>
{
    var line = new QuotaLine("Tên bucket · Tuần", 72, null);
    Assert(line.Display() == "Tên bucket · Weekly: 72% left" && line.Title == "Tên bucket · Tuần", "Cached quota or server bucket changed");
    Assert(new QuotaLine("codex · 5 giờ", null, null, "Không có dữ liệu").Display() == "codex · 5 hours: No data", "Cached duration not translated");
    Assert(new QuotaLine("Lượt reset", null, null, "2").Display() == "Reset count: 2", "Cached reset label not translated");
});
Test("quota card styles reflect only weekly and five-hour windows", () =>
{
    var quota = new QuotaSnapshot { Lines = [new("codex · Weekly", 10, null)] };
    Assert(QuotaPresentation.WeeklyOnly(quota) && QuotaPresentation.Low(quota), "Weekly low quota not classified");
    quota.Lines.Add(new("codex · 5 hours", 80, null));
    Assert(!QuotaPresentation.WeeklyOnly(quota) && QuotaPresentation.Low(quota), "Weekly exhaustion ignored");
    quota.Lines = [new("Credits", 0, null), new("codex · Weekly", null, null)];
    Assert(!QuotaPresentation.Low(quota), "Unknown usage or credits treated as exhausted");
    Assert(!QuotaPresentation.WeeklyOnly(null), "Missing data treated as weekly-only");
});
Test("reset refresh is due once across reloads and allows the next reset", () =>
{
    var f = Fixture(); var account = f.Create("Schedule");
    account.Quota = new QuotaSnapshot { FetchedAt = DateTimeOffset.FromUnixTimeSeconds(100), Lines = [new("codex · 5 hours", 0, 200), new("codex · Weekly", 20, 300)] };
    Assert(QuotaPresentation.DueReset(account, DateTimeOffset.FromUnixTimeSeconds(259)) is null, "Early refresh");
    Assert(QuotaPresentation.DueReset(account, DateTimeOffset.FromUnixTimeSeconds(260)) == 200, "Reset not due");
    account.LastAutoRefreshReset = 200;
    var repo = new AccountRepository(f.App); repo.SaveAccounts(new AccountDocument { Accounts = [account] });
    account = repo.LoadAccounts().Accounts.Single();
    Assert(QuotaPresentation.DueReset(account, DateTimeOffset.FromUnixTimeSeconds(299)) is null, "Repeated reset attempt after restart/failure");
    Assert(QuotaPresentation.DueReset(account, DateTimeOffset.FromUnixTimeSeconds(360)) == 300, "Next reset not due");
    account.Quota!.FetchedAt = DateTimeOffset.FromUnixTimeSeconds(301);
    Assert(QuotaPresentation.DueReset(account, DateTimeOffset.FromUnixTimeSeconds(362)) is null, "Already refreshed snapshot retriggered");
});
Test("quota parser preserves plan and duration metadata", () =>
{
    using var json = System.Text.Json.JsonDocument.Parse("""{"rateLimits":{"planType":"plus","primary":{"usedPercent":20,"windowDurationMins":300}}}""");
    var quota = CodexQuotaService.Parse(json.RootElement);
    Assert(quota.PlanType == "plus" && quota.Lines.Single().WindowDurationMins == 300, "Plan or duration missing");
});
Test("background queue spaces accounts by two seconds and rejects overlap", () =>
{
    var gate = new RefreshQueueGate(); var now = DateTimeOffset.UtcNow; gate.DelayStart(now);
    Assert(!gate.TryStart("a", now.AddMilliseconds(1999)), "Startup delay missing");
    Assert(gate.TryStart("a", now.AddSeconds(2)), "First account not started");
    Assert(!gate.TryStart("b", now.AddSeconds(30)), "Concurrent background refresh allowed");
    gate.Complete(now.AddSeconds(30));
    Assert(!gate.TryStart("b", now.AddSeconds(31)), "Completion delay missing");
    Assert(gate.TryStart("b", now.AddSeconds(32)), "Next account not started");
});
tests.Add(("reset protocol uses isolated profile and stable attempt key", async () =>
{
    var f = Fixture(); var account = f.Create("Reset fixture"); var repo = new AccountRepository(f.App);
    account.PendingResetAttempt = "00000000-0000-0000-0000-000000000001";
    repo.SaveAccounts(new AccountDocument { Accounts = [account] });
    var key = repo.LoadAccounts().Accounts.Single().PendingResetAttempt!;
    var service = new CodexQuotaService();
    var deps = new Dependencies(null, null, false, Environment.ProcessPath!);
    Assert(await service.ConsumeResetAsync(deps, f.Path(account), key) == "alreadyRedeemed", "Wrong method, key or result");
    Assert(await service.ConsumeResetAsync(deps, f.Path(account), key) == "alreadyRedeemed", "Retry changed key");
    Assert(await service.ConsumeResetAsync(deps, f.Path(account), Guid.NewGuid().ToString()) == "noCredit", "No-credit result lost");
    foreach (var outcome in new[] { "reset", "nothingToReset", "alreadyRedeemed", "noCredit" })
    {
        using var response = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { outcome }));
        Assert(CodexQuotaService.ParseResetOutcome(response.RootElement) == outcome, "Outcome altered");
    }
    using var unknown = System.Text.Json.JsonDocument.Parse("{\"outcome\":\"unknown\"}");
    Reject(() => CodexQuotaService.ParseResetOutcome(unknown.RootElement));
}));
tests.Add(("warm up uses ephemeral isolated CLI without a saved session", async () =>
{
    var f = Fixture(); var account = f.Create("Warm up fixture");
    var info = WarmUpService.StartInfo(Environment.ProcessPath!, f.Path(account), f.App);
    Assert(info.ArgumentList.Last() == "reply \"OK\"" && info.ArgumentList.Contains("--ephemeral"), "Wrong warm-up command");
    Assert(!info.Environment.ContainsKey("OPENAI_API_KEY") && info.Environment["CODEX_HOME"] == f.Path(account), "Wrong account environment");
    var before = Directory.GetFiles(f.Shared).Order().ToArray();
    await new WarmUpService().RunAsync(new(null, null, false, Environment.ProcessPath!), f.Path(account), CancellationToken.None);
    Assert(before.SequenceEqual(Directory.GetFiles(f.Shared).Order()), "Warm up changed sessions");
}));
Test("MCP schema rejects unknown tools, extra arguments and unsafe types", () =>
{
    using var empty = System.Text.Json.JsonDocument.Parse("{}");
    Reject(() => ManagerMcp.Validate("reset_credits", empty.RootElement));
    using var extra = System.Text.Json.JsonDocument.Parse("{\"arbitraryCommand\":\"bad\"}");
    Reject(() => ManagerMcp.Validate("manager_health", extra.RootElement));
    using var invalid = System.Text.Json.JsonDocument.Parse("{\"sessionId\":1,\"text\":\"hello\"}");
    Reject(() => ManagerMcp.Validate("session_send", invalid.RootElement));
    using var missing = System.Text.Json.JsonDocument.Parse("{\"sessionId\":\"valid\"}");
    Reject(() => ManagerMcp.Validate("session_send", missing.RootElement));
});
Test("automation account projection never includes notes or profile credential paths", () =>
{
    var a = new Account { DisplayName = "fixture@example.com", Note = "DO-NOT-EXPOSE-CREDENTIAL", ProfilePath = "profiles/private" };
    var json = System.Text.Json.JsonSerializer.Serialize(ManagerAutomation.AccountView(a, 0));
    Assert(!json.Contains("DO-NOT-EXPOSE") && !json.Contains("ProfilePath") && !json.Contains("Note"), "Private account data escaped");
});
Test("quota switching refuses stale, exhausted, missing-weekly and credits-only accounts", () =>
{
    var a = new Account { Quota = new() { Lines = [new("codex · 5 hours", 100, null, WindowDurationMins: 300), new("codex · Weekly", 25, null, WindowDurationMins: 10080)] } };
    Assert(ManagerAutomation.Eligible(a, 100), "Eligible account refused");
    a.Quota.FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-5); Assert(!ManagerAutomation.Eligible(a, 20), "Stale quota accepted");
    a.Quota.FetchedAt = DateTimeOffset.UtcNow; a.Quota.Lines[1] = new("codex · Weekly", 0, null, WindowDurationMins: 10080);
    Assert(!ManagerAutomation.Eligible(a, 20), "Exhausted weekly quota accepted");
    a.Quota.Lines = [new("codex · Credits", null, null, "500")]; Assert(!ManagerAutomation.Eligible(a, 20), "Credits accepted as quota");
});
Test("session runtime protects path traversal and reused process identity", () =>
{
    var f = Fixture(); var runtime = new SessionRuntime(f.App);
    Reject(() => runtime.PathFor("../accounts"));
    using var process = System.Diagnostics.Process.GetCurrentProcess();
    var record = runtime.Track(process, "account", f.App, null, "managed");
    Assert(SessionRuntime.Alive(record), "Current process not recognized");
    record.ProcessStartedTicks--; Assert(!SessionRuntime.Alive(record), "Reused PID accepted");
    Reject(() => SessionRuntime.StopTerminal(record));
});
Test("saved permission policy retains writable roots, network and temp exclusions", () =>
{
    using var saved = JsonDocument.Parse("{\"type\":\"workspace-write\",\"writable_roots\":[\"C:\\\\allowed\"],\"network_access\":false,\"exclude_tmpdir_env_var\":true,\"exclude_slash_tmp\":true}");
    var mapped = ManagedCliHost.SandboxOverride(saved.RootElement);
    Assert(mapped.GetProperty("type").GetString() == "workspaceWrite" && mapped.GetProperty("writableRoots")[0].GetString() == "C:\\allowed", "Writable roots changed");
    Assert(!mapped.GetProperty("networkAccess").GetBoolean() && mapped.GetProperty("excludeTmpdirEnvVar").GetBoolean() && mapped.GetProperty("excludeSlashTmp").GetBoolean(), "Sandbox policy weakened");
    using var unknown = JsonDocument.Parse("{\"type\":\"unsupported\"}"); Reject(() => ManagedCliHost.SandboxOverride(unknown.RootElement));
});
Test("rollout completion corrects stale managed state and does not treat quota failure as success", () =>
{
    var f = Fixture(); var runtime = new SessionRuntime(f.App);
    var record = new CliSessionRecord { ThreadId = Guid.NewGuid().ToString(), WorkingDirectory = f.App };
    var day = Path.Combine(f.Shared, "2026", "10", "03"); Directory.CreateDirectory(day);
    var path = Path.Combine(day, "rollout-" + record.ThreadId + ".jsonl");
    runtime.Save(record);
    runtime.Write(runtime.PathFor(record.Id, ".state.json"), new { status = "active", lastEventAt = "2026-10-03T00:00:00Z", lastMessage = (string?)null, pendingRequests = new object[0] });
    File.WriteAllText(path, JsonSerializer.Serialize(new { timestamp = "2026-10-03T01:00:00Z", type = "event_msg",
        payload = new { type = "task_complete", last_agent_message = "Finished package", error = (object?)null } }) + "\n");
    var state = runtime.ReadState(record, f.Settings.DefaultCodexHome);
    Assert(state.GetProperty("status").GetString() == "completed" && state.GetProperty("lastMessage").GetString() == "Finished package", "Completion remained active");
    File.WriteAllText(path, JsonSerializer.Serialize(new { timestamp = "2026-10-03T01:00:00Z", type = "event_msg",
        payload = new { type = "task_complete", last_agent_message = (string?)null, error = new { message = "Usage limit", codex_error_info = "usage_limit_exceeded" } } }) + "\n");
    state = runtime.ReadState(record, f.Settings.DefaultCodexHome);
    Assert(state.GetProperty("status").GetString() == "failed" && state.GetProperty("error").GetString() == "Usage limit", "Quota failure counted as completion");
    File.AppendAllText(path, "{\"timestamp\":\"2026-10-03T02:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n{\"incomplete\":");
    state = runtime.ReadState(record, f.Settings.DefaultCodexHome);
    Assert(state.GetProperty("status").GetString() == "active", "New turn or incomplete record broke state");
});
Test("bounded rollout reads ignore partial records and verified stop rejects reused identity", () =>
{
    var f = Fixture(); var path = Path.Combine(f.App, "large.jsonl");
    File.WriteAllText(path, new string('x', 1200000) + "\n{\"complete\":true}\n{\"partial\":");
    var tail = RolloutStatus.Tail(path);
    Assert(tail.Length == 1 && tail[0] == "{\"complete\":true}", "Unbounded or incomplete tail");
    using var current = System.Diagnostics.Process.GetCurrentProcess();
    var record = new SessionRuntime(f.App).Track(current, "account", f.App, null, "managed");
    record.ProcessStartedTicks--;
    Reject(() => SessionRuntime.StopVerified(record));
    Assert(!current.HasExited, "Unverified process was stopped");
});
tests.Add(("MCP stdio handshake and tool errors remain valid JSON-RPC", async () =>
{
    var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\"}}\n{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"reset_credits\",\"arguments\":{}}}\n");
    var output = new StringWriter();
    await ManagerMcp.Run(input, output, (_, _) => Task.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(new { healthy = true })));
    var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert(lines.Length == 3, "Notification produced a response or protocol request was lost");
    using var tools = System.Text.Json.JsonDocument.Parse(lines[1]);
    Assert(tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() == 16, "Tool inventory incomplete");
    using var error = System.Text.Json.JsonDocument.Parse(lines[2]);
    Assert(error.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(), "Unknown mutation was accepted");
}));
tests.Add(("current-user pipe request survives without an interactive desktop", async () =>
{
    var f = Fixture(); var name = AutomationPipe.Name(f.App, "fixture");
    using var host = new AutomationPipe(name, (method, arguments) => method == "unexpected"
        ? throw new NotSupportedException("private fixture detail") : Task.FromResult<object>(new { method, arguments }));
    host.Start();
    var result = await AutomationPipe.Call(name, "read", new { text = "Thuốc tiếng Việt" });
    Assert(result.GetProperty("arguments").GetProperty("text").GetString() == "Thuốc tiếng Việt", "Unicode or pipe routing lost");
    try { await AutomationPipe.Call(name, "unexpected", new { }); throw new Exception("Unexpected error was accepted"); }
    catch (IOException ex) { Assert(ex.Message.Contains("NotSupportedException") && !ex.Message.Contains("private fixture detail"), "Unknown failure closed the pipe or leaked details"); }
}));
tests.Add(("managed CLI resume, send, approval, steer, interrupt and stop use persistent protocol", async () =>
{
    var f = Fixture(); var account = f.Create("Managed fixture");
    var runtime = new SessionRuntime(f.App);
    using var current = System.Diagnostics.Process.GetCurrentProcess();
    var record = runtime.Track(current, account.Id, f.App, "00000000-0000-0000-0000-000000000003", "managed");
    var day = Path.Combine(f.Shared, "2026", "10", "03"); Directory.CreateDirectory(day);
    File.WriteAllText(Path.Combine(day, "rollout-" + record.ThreadId + ".jsonl"), JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "fixture-model", effort = "high", approval_policy = "never", sandbox_policy = new { type = "danger-full-access" } } }) + "\n");
    var deps = new Dependencies(null, null, false, Environment.ProcessPath!);
    var info = ManagedCliHost.StartInfo(Environment.ProcessPath!, f.Path(account), f.App, f.App);
    Assert(!info.Environment.ContainsKey("OPENAI_API_KEY") && info.Environment["CODEX_HOME"] == f.Path(account), "Inherited authentication overrides");
    using var worker = new ManagedCliHost(runtime, record, deps, f.Path(account), f.Settings.DefaultCodexHome);
    var running = worker.Run();
    var name = AutomationPipe.Name(f.App, "session-" + record.Id);
    JsonElement readyState = default;
    for (var retry = 0; retry < 25; retry++)
    {
        try { readyState = await AutomationPipe.Call(name, "read", new { }); break; }
        catch (IOException) { await Task.Delay(100); }
    }
    Assert(readyState.GetProperty("threadId").GetString() == record.ThreadId, "Resume changed thread ID");
    await AutomationPipe.Call(name, "send", new { text = "approval" });
    await Task.Delay(100);
    var state = await AutomationPipe.Call(name, "read", new { });
    Assert(state.GetProperty("pendingRequests").GetArrayLength() == 1, "Approval was automatically accepted or lost");
    using var bad = System.Text.Json.JsonDocument.Parse("{\"decision\":\"unsafe\"}");
    Reject(() => ManagedCliHost.ValidateReply("item/commandExecution/requestApproval", bad.RootElement));
    await AutomationPipe.Call(name, "reply", new { requestId = "approval-1", result = new { decision = "decline" } });
    await AutomationPipe.Call(name, "steer", new { text = "more guidance" });
    try { await AutomationPipe.Call(name, "send", new { text = "duplicate" }); throw new Exception("Duplicate active turn accepted"); }
    catch (IOException) { }
    var interrupted = await AutomationPipe.Call(name, "interrupt", new { });
    Assert(interrupted.GetProperty("status").GetString() == "interrupted", "Interrupt did not wait for acknowledgement");
    await AutomationPipe.Call(name, "stop", new { });
    await running.WaitAsync(TimeSpan.FromSeconds(10));
    using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(runtime.PathFor(record.Id, ".state.json")));
    Assert(saved.RootElement.GetProperty("status").GetString() == "stopped", "Final session state not persisted");
}));
var failures = 0;
try
{
    foreach (var test in tests)
    {
        try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
    }
}
finally
{
    // Test-owned tree only; do not follow any junction, including deliberately invalid test cases.
    void Cleanup(string directory)
    {
        if (!PathSafety.Within(directory, root) && !PathSafety.Same(directory, root)) throw new Exception("Cleanup outside fixture root");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attrs = File.GetAttributes(entry);
            if ((attrs & FileAttributes.Directory) != 0)
            { if ((attrs & FileAttributes.ReparsePoint) == 0) Cleanup(entry); else Directory.Delete(entry, false); }
            else File.Delete(entry);
        }
        Directory.Delete(directory, false);
    }
    Cleanup(root);
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;

sealed class Fixture
{
    public string Root { get; }
    public string App { get; }
    public string Shared { get; }
    public string Sentinel => System.IO.Path.Combine(Shared, "sentinel.txt");
    public JunctionService Junctions { get; } = new();
    public CodexProfileService Service { get; }
    public AppSettings Settings { get; }
    public Fixture(string root)
    {
        Root = root; App = System.IO.Path.Combine(root, "app"); Directory.CreateDirectory(App);
        var home = System.IO.Path.Combine(root, "default-home"); Shared = System.IO.Path.Combine(home, "sessions");
        Directory.CreateDirectory(Shared); File.WriteAllText(Sentinel, "shared-do-not-delete");
        Settings = new() { DefaultCodexHome = home, WorkingDirectory = App }; Service = new(App, Junctions);
    }
    public Account Create(string name) => Service.Create(name, "", Settings);
    public string Path(Account a) => PathSafety.Profile(App, a);
    public string Link(Account a) => System.IO.Path.Combine(Path(a), "sessions");
}
