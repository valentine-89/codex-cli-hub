using CodexAccountManager.Core;
using System.Text.Json;

internal static class CurrentWorkspaceTests
{
    public static void Register(List<(string Name, Func<Task> Run)> tests, string root)
    {
        string Home() { var path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        tests.Add(("project API paginates, refreshes names and preserves the whole catalog on failure", async () =>
        {
            var home = Home(); var mode = Path.Combine(home, "catalog-mode.txt");
            var service = new WorkspaceCatalogService(new Logger(home)); var deps = new Dependencies(null, null, false, Environment.ProcessPath!);
            var first = await service.ReadAsync(deps, home, home);
            Check(first.Projects.Count == 1 && first.Projects[0].Name == "Current project", "Project API pagination/deduplication failed");
            File.WriteAllText(mode, "renamed");
            var renamed = await service.ReadAsync(deps, home, home);
            Check(!renamed.IsStale && renamed.Projects[0].Name == "Renamed project" && renamed.Sessions.SequenceEqual(first.Sessions), "Rename did not refresh independently of chats");
            foreach (var failure in new[] { "project-error", "project-invalid", "project-repeat" })
            {
                File.WriteAllText(mode, failure);
                var stale = await service.ReadAsync(deps, home, home);
                Check(stale.IsStale && stale.Projects.SequenceEqual(renamed.Projects) && stale.Sessions.SequenceEqual(renamed.Sessions), "Partial or stale project data replaced the catalog");
            }
            File.WriteAllText(mode, "project-error");
            try { await new WorkspaceCatalogService().ReadAsync(deps, home, home); throw new Exception("Unsupported API silently fell back"); }
            catch (IOException ex) when (ex.InnerException is CodexRpcException { Code: -32601 }) { }
            Check(File.ReadAllText(Path.Combine(home, "logs", "app.log")).Contains("project/list"), "Project RPC missing from diagnostic");
        }));

        tests.Add(("current projects replace stale Desktop JSON while pending assignments retain their distro", () =>
        {
            var home = Home(); var a = @"\\wsl.localhost\NameFixture-A\home\user\project";
            var b = @"\\wsl.localhost\NameFixture-B\home\user\project";
            var staleRoot = Path.Combine(home, "deleted-project");
            var state = new Dictionary<string, object> {
                ["local-projects"] = new { old = new { name = "Obsolete name", rootPaths = new[] { staleRoot } } },
                ["electron-saved-workspace-roots"] = new[] { staleRoot },
                ["thread-project-assignments"] = new { pending = new { projectId = "old" }, cleared = new { projectId = "old" } },
                ["projectless-thread-ids"] = new[] { "pending", "cleared" },
                ["app-server-project-id-by-legacy-project-id-by-host"] = new Dictionary<string, object> { ["local:" + home] = new { old = "b" } },
                ["electron-owner-value:app-server-projects-migration-by-host:fixture"] = new Dictionary<string, object> {
                    ["local:" + home] = new { projectsMigrated = true, pendingThreadAssignmentIds = new[] { "pending" } } }
            };
            var file = Path.Combine(home, ".codex-global-state.json"); File.WriteAllText(file, JsonSerializer.Serialize(state));
            var before = File.ReadAllBytes(file);
            var sessions = new CatalogSession[] { new("pending", "Pending", "/home/user/project", DateTime.UnixEpoch),
                new("server", "Server", "/home/user/project", DateTime.UnixEpoch, "a"), new("cleared", "Cleared", home, DateTime.UnixEpoch) };
            var catalog = WorkspaceCatalogService.Build(home, sessions, currentProjects: [new("a", "Current A", [a]), new("b", "Current B", [b])]);
            Check(catalog.Projects.Count == 2 && catalog.Projects.All(p => p.Name.StartsWith("Current ")), "Stale/deleted Desktop project resurrected");
            Check(catalog.Sessions[0].WorkingDirectory == b && !catalog.Sessions[0].IsProjectless
                && catalog.Sessions[1].WorkingDirectory == a && catalog.Sessions[2].IsProjectless, "Migrated or pending assignment handled incorrectly");
            Check(before.SequenceEqual(File.ReadAllBytes(file)), "Desktop metadata written during read");
            Check(WorkspaceCatalogService.Build(home, [], currentProjects: []).Projects.Count == 0, "Empty current project list reused old JSON");
            return Task.CompletedTask;
        }));

        tests.Add(("owner-scoped projectless metadata is read without mixing different owners", () =>
        {
            var home = Home(); var path = Path.Combine(home, "chat"); Directory.CreateDirectory(path);
            var file = Path.Combine(home, ".codex-global-state.json");
            var state = new Dictionary<string, object> { ["electron-owner-value:thread-projectless-output-directories:one"] = new { chat = Path.Combine(path, "outputs") } };
            var session = new CatalogSession("chat", "Chat", path, DateTime.UnixEpoch);
            File.WriteAllText(file, JsonSerializer.Serialize(state));
            Check(WorkspaceCatalogService.Build(home, [session], currentProjects: []).Sessions.Single().IsProjectless, "Scoped output directory ignored");
            state["electron-owner-value:thread-projectless-output-directories:two"] = new { other = Path.Combine(home, "outputs") };
            File.WriteAllText(file, JsonSerializer.Serialize(state));
            Check(!WorkspaceCatalogService.Build(home, [session], currentProjects: []).Sessions.Single().IsProjectless, "Ambiguous owner metadata combined");
            state["electron-owner-value:app-server-projects-migration-by-host:fixture"] = new Dictionary<string, object> { ["local:" + home] = "unexpected shape" };
            File.WriteAllText(file, JsonSerializer.Serialize(state));
            Check(WorkspaceCatalogService.Build(home, [session], currentProjects: []).Sessions.Count == 1, "Malformed migration metadata lost the chat");
            File.WriteAllText(file, "[]");
            var malformed = WorkspaceCatalogService.Build(home, [session], currentProjects: []);
            Check(malformed.Warning is not null && malformed.Sessions.Count == 1, "Malformed Desktop state lost API sessions");
            return Task.CompletedTask;
        }));
    }
}
