using CodexAccountManager.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var rootIndex = Array.IndexOf(args, "--root");
        var root = rootIndex >= 0 && rootIndex + 1 < args.Length ? args[rootIndex + 1] : DefaultRoot();
        var maintenance = args.Any(a => a is "--session-store-audit" or "--session-store-repair");
        var deletion = args.Contains("--delete-account");
        var background = maintenance || deletion || args.Any(a => a is "--mcp" or "--call" or "--automation-host" or "--session-host");
        try
        {
            var repository = new AccountRepository(root);
            if (deletion)
            {
                if (maintenance || args.Any(a => a is "--mcp" or "--call" or "--automation-host" or "--session-host" or "--verify-api"))
                    throw new IOException("Account deletion cannot be combined with another operation.");
                var idIndex = Array.IndexOf(args, "--delete-account");
                var nameIndex = Array.IndexOf(args, "--expected-account-name");
                if (idIndex + 1 >= args.Length || nameIndex < 0 || nameIndex + 1 >= args.Length)
                    throw new IOException("Provide --delete-account <ID> and --expected-account-name <name>.");
                new AccountDeletion(repository).Delete(args[idIndex + 1], args[nameIndex + 1]);
                using var deletionOutput = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                deletionOutput.WriteLine(JsonSerializer.Serialize(new { operation = "delete-account", accountId = args[idIndex + 1], deleted = true }));
                return;
            }
            if (maintenance)
            {
                using var maintenanceOutput = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                if (args.Contains("--verify-api") && !args.Contains("--session-store-repair"))
                    throw new IOException("API verification requires --session-store-repair; audit is read-only.");
                var service = new SessionStoreMaintenance(repository.Root, repository.LoadSettings().DefaultCodexHome);
                using var maintenanceLock = args.Contains("--session-store-repair") ? repository.AcquireLock() : null;
                var audit = service.Audit();
                Dependencies? maintenanceDependencies = null;
                var sharedHome = repository.LoadSettings().DefaultCodexHome;
                if (args.Contains("--session-store-repair") && audit.Issues.Count == 0
                    && (audit.Repairs.Count > 0 || args.Contains("--verify-api")))
                {
                    SessionStoreMaintenance.EnsureCodexClosed();
                    maintenanceDependencies = DependencyDetector.DetectAsync().GetAwaiter().GetResult();
                    // Require the current paginated API before our first database update.
                    _ = new CodexThreadReader(new Logger(repository.Root))
                        .ReadAsync(maintenanceDependencies, sharedHome, sharedHome).GetAwaiter().GetResult();
                }
                var changed = args.Contains("--session-store-repair") ? service.Repair(audit) : 0;
                string[]? visibleSessionIds = null;
                if (args.Contains("--verify-api"))
                {
                    visibleSessionIds = new CodexThreadReader(new Logger(repository.Root))
                        .ReadAsync(maintenanceDependencies!, sharedHome, sharedHome).GetAwaiter().GetResult().Select(s => s.Id).ToArray();
                }
                var result = new { operation = args.Contains("--session-store-repair") ? "repair" : "audit", changed,
                    audit, after = changed > 0 ? service.Audit() : audit, visibleSessionIds };
                maintenanceOutput.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                if (audit.Issues.Count > 0) Environment.ExitCode = 2;
                return;
            }
            if (background) { RunBackground(repository, args).GetAwaiter().GetResult(); return; }
            ApplicationConfiguration.Initialize();
            var sessionsIndex = Array.IndexOf(args, "--sessions");
            if (sessionsIndex >= 0)
            {
                var selected = sessionsIndex + 1 < args.Length && Guid.TryParseExact(args[sessionsIndex + 1], "N", out _)
                    ? args[sessionsIndex + 1] : null;
                using var sessions = new SessionsForm(repository, selected);
                if (args.Contains("--smoke-test"))
                    sessions.Shown += async (_, _) =>
                    {
                        await sessions.Ready;
                        using var bitmap = new Bitmap(sessions.Width, sessions.Height);
                        sessions.DrawToBitmap(bitmap, new Rectangle(0, 0, sessions.Width, sessions.Height));
                        bitmap.Save(Path.Combine(repository.Root, "smoke-sessions.png")); sessions.Close();
                    };
                Application.Run(sessions); return;
            }
            using var appLock = AcquireUiLock(repository);
            using var form = new MainForm(repository);
            if (args.Contains("--smoke-test"))
            {
                form.Shown += async (_, _) =>
                {
                    await form.Ready;
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(Path.Combine(repository.Root, "smoke-test.png"));
                    form.Close();
                };
            }
            Application.Run(form);
        }
        catch (Exception ex)
        {
            try { new Logger(root).Write("startup-failed:" + ex.GetType().Name, Environment.ProcessPath, ex.HResult); }
            catch (Exception) { /* Diagnostics must not hide the original startup error. */ }
            if (background) Console.Error.WriteLine(AutomationErrors.Safe(ex));
            else MessageBox.Show($"Cannot open Codex Account Manager.\n\n{ex.Message}\n\nCheck folder permissions and whether another manager is open. Existing data was not reset.",
                "Codex Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }
    private static string DefaultRoot()
    {
        var versionFolder = new DirectoryInfo(AppContext.BaseDirectory);
        var versions = versionFolder.Parent;
        if (versions?.Name == "versions" && versions.Parent is { } portable
            && File.Exists(Path.Combine(portable.FullName, "settings.json"))) return portable.FullName;
        return AppContext.BaseDirectory;
    }
    private static FileStream AcquireUiLock(AccountRepository repository)
    {
        try { return repository.AcquireLock(); }
        catch (IOException)
        {
            var health = AutomationPipe.Call(AutomationPipe.Name(repository.Root), "manager_health", new { }).GetAwaiter().GetResult();
            if (health.GetProperty("hostMode").GetString() != "background") throw new IOException("Another Manager UI owns this account store.");
            _ = AutomationPipe.Call(AutomationPipe.Name(repository.Root), "host_shutdown", new { }).GetAwaiter().GetResult();
            for (var retry = 0; retry < 30; retry++)
            {
                try { return repository.AcquireLock(); }
                catch (IOException) { Thread.Sleep(100); }
            }
            throw new IOException("Background bridge is still closing. Please try opening Manager again.");
        }
    }
    private static async Task RunBackground(AccountRepository repository, string[] args)
    {
        var workerIndex = Array.IndexOf(args, "--session-host");
        if (workerIndex >= 0)
        {
            if (workerIndex + 1 >= args.Length) throw new IOException("Missing session ID.");
            var runtime = new SessionRuntime(repository.Root);
            var id = args[workerIndex + 1];
            var record = runtime.Get(id);
            for (var retry = 0; record.ProcessId == 0 && retry < 50; retry++) { await Task.Delay(100); record = runtime.Get(id); }
            if (record.Mode != "managed" || record.ProcessId != Environment.ProcessId || !SessionRuntime.Alive(record))
                throw new IOException("Session worker identity is not registered.");
            var account = repository.LoadAccounts().Accounts.Single(a => a.Id == record.AccountId);
            var settings = repository.LoadSettings();
            var home = new CodexProfileService(repository.Root, new JunctionService()).Validate(account, settings);
            var deps = await DependencyDetector.DetectAsync();
            using var host = new ManagedCliHost(runtime, record, deps, home, settings.DefaultCodexHome);
            await host.Run(); return;
        }
        if (args.Contains("--automation-host"))
        {
            using var appLock = repository.AcquireLock();
            var accounts = repository.LoadAccounts(); var settings = repository.LoadSettings();
            var deps = await DependencyDetector.DetectAsync();
            var reader = new AccountRefreshService();
            var controller = new ManagerAutomation(repository, () => accounts, () => settings, async account =>
            {
                var home = new CodexProfileService(repository.Root, new JunctionService()).Validate(account, settings);
                CredentialConfig.EnsureFile(home);
                var result = await reader.ReadAsync(home);
                account.LoginStatus = result.LoginStatus; account.LastCheckedAt = DateTimeOffset.UtcNow;
                if (result.Quota is not null) account.Quota = result.Quota;
                account.QuotaError = result.QuotaError; repository.SaveAccounts(accounts);
            }, () => Task.FromResult(deps), Environment.ProcessPath!, "background");
            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var pipe = new AutomationPipe(AutomationPipe.Name(repository.Root), (method, value) =>
            {
                if (method != "host_shutdown") return controller.Call(method, value);
                _ = Task.Run(async () => { await Task.Delay(300); stop.TrySetResult(); });
                return Task.FromResult<object>(new { stopping = true });
            });
            pipe.Start(); await stop.Task; return;
        }
        var client = new RecoveringAutomationClient(AutomationPipe.Name(repository.Root), () => EnsureHost(repository));
        if (args.Contains("--mcp"))
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            await ManagerMcp.Run(input, output, (method, value) => client.Call(method, value));
            return;
        }
        var callIndex = Array.IndexOf(args, "--call");
        if (callIndex < 0 || callIndex + 1 >= args.Length) throw new IOException("Missing tool name.");
        var tool = args[callIndex + 1];
        var jsonIndex = Array.IndexOf(args, "--json");
        using var arguments = JsonDocument.Parse(jsonIndex >= 0 && jsonIndex + 1 < args.Length ? args[jsonIndex + 1] : "{}");
        ManagerMcp.Validate(tool, arguments.RootElement);
        var response = await client.Call(tool, arguments.RootElement);
        using var callOutput = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        await callOutput.WriteLineAsync(response.GetRawText());
    }
    private static async Task EnsureHost(AccountRepository repository)
    {
        var root = repository.Root;
        var path = Path.Combine(root, "bridge-start.lock");
        PathSafety.NoReparseAncestors(path);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        FileStream? startupLock = null;
        Process? process = null;
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    _ = await AutomationPipe.Call(AutomationPipe.Name(root), "manager_health", new { }, 5, 250);
                    return;
                }
                catch (AutomationHostUnavailableException) { }
                if (startupLock is null)
                {
                    try { startupLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { await Task.Delay(200); continue; }
                }
                // A GUI or another bridge may hold the store while initializing.
                // Wait for its pipe instead of launching competing hosts.
                if (process is null)
                {
                    try { using var storeLock = repository.AcquireLock(); }
                    catch (IOException) { await Task.Delay(200); continue; }
                    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root };
                    info.ArgumentList.Add("--root"); info.ArgumentList.Add(root); info.ArgumentList.Add("--automation-host");
                    process = Process.Start(info) ?? throw new IOException("Could not start the headless Manager bridge.");
                }
                // A racing GUI can win the store lock after the probe. Keep waiting
                // for that owner, but start at most one host per recovery attempt.
                await Task.Delay(200);
            }
            throw new IOException("Manager bridge is unavailable or still initializing. Retry after the current Manager operation finishes.");
        }
        finally { process?.Dispose(); startupLock?.Dispose(); }
    }
}
