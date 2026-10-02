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
        var root = rootIndex >= 0 && rootIndex + 1 < args.Length ? args[rootIndex + 1] : AppContext.BaseDirectory;
        var background = args.Any(a => a is "--mcp" or "--call" or "--automation-host" or "--session-host");
        try
        {
            var repository = new AccountRepository(root);
            if (background) { RunBackground(repository, args).GetAwaiter().GetResult(); return; }
            ApplicationConfiguration.Initialize();
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
            if (background) Console.Error.WriteLine(AutomationErrors.Safe(ex));
            else MessageBox.Show($"Cannot open Codex Account Manager.\n\n{ex.Message}\n\nCheck folder permissions and whether another manager is open. Existing data was not reset.",
                "Codex Account Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
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
        await EnsureHost(repository.Root);
        if (args.Contains("--mcp"))
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            await ManagerMcp.Run(input, output, (method, value) => AutomationPipe.Call(AutomationPipe.Name(repository.Root), method, value, 90));
            return;
        }
        var callIndex = Array.IndexOf(args, "--call");
        if (callIndex < 0 || callIndex + 1 >= args.Length) throw new IOException("Missing tool name.");
        var tool = args[callIndex + 1];
        var jsonIndex = Array.IndexOf(args, "--json");
        using var arguments = JsonDocument.Parse(jsonIndex >= 0 && jsonIndex + 1 < args.Length ? args[jsonIndex + 1] : "{}");
        ManagerMcp.Validate(tool, arguments.RootElement);
        var response = await AutomationPipe.Call(AutomationPipe.Name(repository.Root), tool, arguments.RootElement, 90);
        using var callOutput = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        await callOutput.WriteLineAsync(response.GetRawText());
    }
    private static async Task EnsureHost(string root)
    {
        try { _ = await AutomationPipe.Call(AutomationPipe.Name(root), "manager_health", new { }); return; }
        catch (IOException) { }
        // ShellExecute detaches all inherited MCP/stdout pipe handles, not just the
        // three redirected standard handles. This is a preinstalled local executable.
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root };
        info.ArgumentList.Add("--root"); info.ArgumentList.Add(root); info.ArgumentList.Add("--automation-host");
        using var process = Process.Start(info) ?? throw new IOException("Could not start the headless Manager bridge.");
        for (var retry = 0; retry < 15; retry++)
        {
            try { _ = await AutomationPipe.Call(AutomationPipe.Name(root), "manager_health", new { }); return; }
            catch (IOException) { if (process.HasExited) throw new IOException("Another Manager owns the data or the bridge could not start. Existing sessions were preserved."); }
            await Task.Delay(200);
        }
        throw new IOException("Manager bridge is still initializing. Retry without starting another job.");
    }
}
