using CodexAccountManager.Core;

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
Test("WinForms renders populated and empty account lists", () =>
{
    var f = Fixture(); var a = f.Create("Work account — thử nghiệm"); var b = f.Create("Personal account");
    a.LoginStatus = "Logged in (local credentials)"; a.LastCheckedAt = DateTimeOffset.Now;
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
    File.WriteAllText(fake, "[Console]::Out.WriteLine($env:CODEX_HOME); [Console]::Out.WriteLine(($args -join '|')); if ($env:OPENAI_API_KEY) { exit 23 }");
    var original = Environment.GetEnvironmentVariable("CODEX_HOME");
    var originalKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    try
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-only");
        var script = CodexProcessLauncher.BuildScript(fake, special, special, CodexAction.Resume);
        var result = await ShellRunner.RunAsync(deps.PowerShell!, script, special);
        Assert(result.ExitCode == 0 && result.Output.Contains(special) && result.Output.Contains("resume|--all"), "Quoting or isolation failed");
        Assert(Environment.GetEnvironmentVariable("CODEX_HOME") == original, "Parent environment changed");
    }
    finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalKey); }
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
