using CodexAccountManager.Core;
using System.Text.Json;

internal static class DesktopProjectNameTests
{
    public static void Register(List<(string Name, Func<Task> Run)> tests, string root)
    {
        void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
        string Home() { var path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Save(string home, object projects) => File.WriteAllText(Path.Combine(home, ".codex-global-state.json"),
            JsonSerializer.Serialize(new Dictionary<string, object> { ["local-projects"] = projects }));

        Test("Desktop rename labels all exact project roots without changing chats or paths", () =>
        {
            var home = Home(); var path = Path.Combine(home, "krakun"); Directory.CreateDirectory(path);
            var wsl = @"\\wsl$\CodexNameFixture\home\user\krakun";
            var session = new CatalogSession(Guid.NewGuid().ToString(), "Original chat title", path, DateTime.UtcNow);
            Save(home, new { project = new { name = "Krakun", rootPaths = new[] { path, wsl } } });
            Check(WorkspaceCatalogService.Build(home, [session]).Projects.All(p => p.Name == "Krakun"), "Initial name missing");
            Save(home, new { project = new { name = "Bikun 日本語", rootPaths = new[] { path, wsl } } });
            var before = File.ReadAllBytes(Path.Combine(home, ".codex-global-state.json"));
            var catalog = WorkspaceCatalogService.Build(home, [session]);
            Check(catalog.Projects.Count == 2 && catalog.Projects.All(p => p.Name == "Bikun 日本語"), "Rename not applied to all roots");
            Check(catalog.Projects.Any(p => p.Directory == path && p.SessionCount == 1)
                && catalog.Projects.Any(p => p.Directory == @"\\wsl.localhost\CodexNameFixture\home\user\krakun"), "Root identity changed");
            Check(catalog.Sessions.Single() == session, "Project rename changed chat metadata");
            Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(home, ".codex-global-state.json"))), "Discovery wrote Desktop state");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(catalog.Projects[0], AutomationPipe.Json));
            Check(json.RootElement.GetProperty("name").GetString() == "Bikun 日本語"
                && !json.RootElement.TryGetProperty("displayName", out _), "Project response contract changed");
            using var row = JsonDocument.Parse(JsonSerializer.Serialize(new { id = session.Id, name = "Renamed chat", preview = "Old chat title" }));
            Check(CodexThreadReader.Parse(row.RootElement)!.Title == "Renamed chat", "Chat name did not take precedence over preview");
        });

        Test("Desktop project labels do not leak into nested or conflicting roots", () =>
        {
            var home = Home(); var path = Path.Combine(home, "old-name"); var nested = Path.Combine(path, "nested");
            Directory.CreateDirectory(nested);
            var session = new CatalogSession(Guid.NewGuid().ToString(), "Chat", nested, DateTime.UtcNow);
            Save(home, new { first = new { name = "Bikun", rootPaths = new[] { path } } });
            var result = WorkspaceCatalogService.Build(home, [session]);
            Check(result.Projects.Single(p => p.Directory == nested).Name == "nested", "Parent label leaked into a separate folder");
            Save(home, new { first = new { name = "Bikun", rootPaths = new[] { path } }, second = new { name = "Another project", rootPaths = new[] { path } } });
            Check(WorkspaceCatalogService.Build(home, []).Projects.Single().Name == "old-name", "Conflicting project name guessed");
            Save(home, new { first = new { name = "   ", rootPaths = new[] { path } } });
            Check(WorkspaceCatalogService.Build(home, []).Projects.Single().Name == "old-name", "Blank project name hid folder name");
        });

        Test("project picker searches renamed projects and refresh keeps resume identity", () =>
        {
            var home = Home(); var path = Path.Combine(home, "krakun"); Directory.CreateDirectory(path);
            var session = new CatalogSession(Guid.NewGuid().ToString(), "Trading analysis", path, DateTime.UtcNow);
            Save(home, new { project = new { name = "Krakun", rootPaths = new[] { path } } });
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new CodexAccountManager.ProjectPickerForm("fixture", _ => Task.FromResult(WorkspaceCatalogService.Build(home, [session])));
                    form.Opacity = 0; form.ShowInTaskbar = false;
                    IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
                    void Ready()
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(10);
                        while (!form.Ready.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                        Check(form.Ready.IsCompletedSuccessfully, "Picker did not finish loading"); Application.DoEvents();
                    }
                    form.Shown += (_, _) => form.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            Ready();
                            Save(home, new { project = new { name = "Bikun", rootPaths = new[] { path } } });
                            Descendants(form).OfType<Button>().Single(b => b.Text == "Refresh").PerformClick(); Ready();
                            Descendants(form).OfType<TextBox>().Single().Text = "bikun"; Application.DoEvents();
                            var items = Descendants(form).OfType<ListBox>().SelectMany(l => l.Items.Cast<object>()).ToArray();
                            Check(items.OfType<SessionProject>().Single().Name == "Bikun", "Search missed renamed project");
                            Check(items.OfType<CatalogSession>().Single().Id == session.Id, "Project-name search hid its chat");
                            Descendants(form).OfType<Button>().Single(b => b.Text == "Resume chat").PerformClick();
                            Check(form.Selection?.SessionId == session.Id && form.Selection.Directory == path, "Resume target changed with display name");
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { form.Close(); }
                    }));
                    Application.Run(form);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Check(thread.Join(TimeSpan.FromSeconds(30)), "Picker test timed out");
            if (failure is not null) throw failure;
        });
    }
}
