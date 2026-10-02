using System.Diagnostics;
using System.Text.Json;

namespace CodexAccountManager.Core;

public sealed class ManagerAutomation
{
    private readonly AccountRepository repository;
    private readonly Func<AccountDocument> accounts;
    private readonly Func<AppSettings> settings;
    private readonly Func<Account, Task> refresh;
    private readonly Func<Task<Dependencies>> dependencies;
    private readonly string executable;
    private readonly string hostMode;
    private readonly SessionRuntime runtime;
    private readonly SemaphoreSlim operations = new(1, 1);
    public ManagerAutomation(AccountRepository repository, Func<AccountDocument> accounts, Func<AppSettings> settings,
        Func<Account, Task> refresh, Func<Task<Dependencies>> dependencies, string executable, string hostMode = "desktop")
    {
        this.repository = repository; this.accounts = accounts; this.settings = settings; this.refresh = refresh;
        this.dependencies = dependencies; this.executable = executable; this.hostMode = hostMode; runtime = new(repository.Root);
    }
    public async Task<object> Call(string tool, JsonElement args)
    {
        await operations.WaitAsync();
        try
        {
            if (tool == "manager_health") return new { version = "1.11.0", processId = Environment.ProcessId,
                transport = "stdio + current-user named pipe", supportsLockedDesktop = true, root = repository.Root, hostMode };
            if (tool == "accounts_list") return new { accounts = accounts().Accounts.Select((a, i) => AccountView(a, i)).ToArray() };
            if (tool == "account_refresh")
            {
                var account = Account(Required(args, "accountId")); await refresh(account);
                return AccountView(account, accounts().Accounts.IndexOf(account));
            }
            if (tool == "projects_list")
            {
                var account = Account(Required(args, "accountId"));
                var deps = await dependencies();
                var home = new CodexProfileService(repository.Root, new JunctionService()).Validate(account, settings());
                var sessions = await new CodexThreadReader().ReadAsync(deps, home, settings().DefaultCodexHome);
                return WorkspaceCatalogService.Build(settings().DefaultCodexHome, sessions);
            }
            if (tool == "sessions_list")
            {
                await runtime.DiscoverExisting(await dependencies(), accounts().Accounts, settings().DefaultCodexHome);
                return new { sessions = runtime.List().Select(r => new { session = r, alive = SessionRuntime.Alive(r) }).ToArray() };
            }
            if (tool == "session_read")
            { var record = runtime.Get(Required(args, "sessionId")); return runtime.Read(record, settings().DefaultCodexHome); }
            if (tool is "session_start" or "session_resume")
            {
                var account = Account(Required(args, "accountId"));
                var cwd = SessionProjectCatalog.NormalizeWorkingDirectory(Required(args, "cwd"));
                var threadId = tool == "session_resume" ? Required(args, "threadId") : null;
                if (threadId is not null && !Guid.TryParse(threadId, out _)) throw new IOException("Invalid Codex thread ID.");
                if (threadId is not null && RolloutStatus.Find(settings().DefaultCodexHome, threadId) is null)
                    throw new IOException("Stored thread not found. No new session was created.");
                await runtime.DiscoverExisting(await dependencies(), accounts().Accounts, settings().DefaultCodexHome);
                EnsureNotRunning(threadId, cwd);
                var record = await Start(account, cwd, threadId);
                if (Optional(args, "text") is { } text)
                    await AutomationPipe.Call(AutomationPipe.Name(repository.Root, "session-" + record.Id), "send", new { text });
                return runtime.Read(runtime.Get(record.Id), settings().DefaultCodexHome);
            }
            if (tool == "session_take_control" || tool == "session_switch_account")
            {
                var old = runtime.Get(Required(args, "sessionId"));
                if (old.ThreadId is null) throw new IOException("Terminal has no known thread ID. Resume an explicitly selected stored thread instead.");
                var target = tool == "session_take_control" ? Account(old.AccountId) : Account(Required(args, "accountId"));
                if (tool == "session_take_control" && old.Mode == "managed") return runtime.Read(old, settings().DefaultCodexHome);
                if (tool == "session_switch_account")
                {
                    if (target.Id == old.AccountId) throw new IOException("Target account is already in use.");
                    var excluded = args.TryGetProperty("excludedAccountIds", out var excludes)
                        ? excludes.EnumerateArray().Select(e => e.GetString()).ToHashSet() : [];
                    if (excluded.Contains(target.Id)) throw new IOException("Target account is excluded by caller policy.");
                    if (args.TryGetProperty("excludeLastAccount", out var last) && last.GetBoolean()
                        && target == accounts().Accounts.Last()) throw new IOException("Last account is excluded by caller policy.");
                    await refresh(target);
                    var minimum = args.TryGetProperty("minimumRemainingPercent", out var min) ? min.GetDouble() : 20;
                    if (!double.IsFinite(minimum) || minimum is < 0 or > 100) throw new IOException("Invalid quota threshold.");
                    if (!Eligible(target, minimum)) throw new IOException("Target account has insufficient verified quota. Original session was not stopped.");
                    if (!args.TryGetProperty("onlyIfQuotaExhausted", out var exhausted) || exhausted.GetBoolean())
                    {
                        var source = Account(old.AccountId); await refresh(source);
                        if (source.QuotaError is not null || source.Quota?.Lines.Any(l => l.Title.StartsWith("codex ·", StringComparison.OrdinalIgnoreCase)
                            && l.Duration is 300 or 10080 && l.RemainingPercent == 0) != true)
                            throw new IOException("Source account is not confirmed exhausted. Original session was not stopped.");
                    }
                }
                // Prepare target and validate stored thread before stopping the current owner.
                new CodexProfileService(repository.Root, new JunctionService()).PrepareSharedStore(target, settings());
                if (RolloutStatus.Find(settings().DefaultCodexHome, old.ThreadId) is null) throw new IOException("Stored thread unavailable. Original session was not stopped.");
                if (SessionRuntime.Alive(old)) await Stop(old);
                EnsureNotRunning(old.ThreadId, old.WorkingDirectory);
                var record = await Start(target, old.WorkingDirectory, old.ThreadId);
                if (Optional(args, "text") is { } text)
                    await AutomationPipe.Call(AutomationPipe.Name(repository.Root, "session-" + record.Id), "send", new { text });
                return runtime.Read(runtime.Get(record.Id), settings().DefaultCodexHome);
            }
            var session = runtime.Get(Required(args, "sessionId"));
            if (tool == "session_stop") { await Stop(session); return runtime.Read(session, settings().DefaultCodexHome); }
            if (session.Mode != "managed") throw new IOException("Use session_take_control to transfer this terminal into the managed CLI protocol before sending or interrupting turns.");
            if (!SessionRuntime.Alive(session)) throw new IOException("Session host exited. Use session_resume with its saved thread ID.");
            var method = tool switch { "session_send" => "send", "session_steer" => "steer", "session_interrupt" => "interrupt",
                "session_reply" => "reply", _ => throw new IOException("Unknown tool.") };
            return await AutomationPipe.Call(AutomationPipe.Name(repository.Root, "session-" + session.Id), method, args);
        }
        finally { operations.Release(); }
    }
    private Account Account(string id) => accounts().Accounts.SingleOrDefault(a => a.Id == id) ?? throw new IOException("Account ID not found.");
    public static object AccountView(Account account, int index) => new { account.Id, account.DisplayName, index,
        account.LoginStatus, account.LastCheckedAt, account.Quota, account.QuotaError };
    public static bool Eligible(Account account, double minimum)
    {
        if (account.QuotaError is not null || account.Quota is null
            || DateTimeOffset.UtcNow - account.Quota.FetchedAt > TimeSpan.FromMinutes(2)) return false;
        var lines = account.Quota.Lines.Where(l => l.Title.StartsWith("codex ·", StringComparison.OrdinalIgnoreCase)).ToArray();
        return lines.Any(l => l.Duration == 300 && l.RemainingPercent is { } percent && percent > 0 && percent >= minimum)
            && lines.Any(l => l.Duration == 10080 && l.RemainingPercent is > 0);
    }
    private void EnsureNotRunning(string? threadId, string cwd)
    {
        if (runtime.List().Any(r => SessionRuntime.Alive(r) && (threadId is not null ? r.ThreadId == threadId : PathSafety.Same(r.WorkingDirectory, cwd))))
            throw new IOException("Session is already owned by a running CLI. Read, stop, or take control of it first.");
    }
    private async Task<CliSessionRecord> Start(Account account, string cwd, string? threadId)
    {
        if (!Directory.Exists(cwd)) throw new IOException("Working directory unavailable.");
        var deps = await dependencies();
        if (deps.NativeCodex is null) throw new IOException("Native Codex CLI unavailable.");
        new CodexProfileService(repository.Root, new JunctionService()).PrepareSharedStore(account, settings());
        var record = new CliSessionRecord { AccountId = account.Id, WorkingDirectory = cwd, ThreadId = threadId };
        runtime.Save(record);
        var info = new ProcessStartInfo(executable) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = repository.Root };
        info.ArgumentList.Add("--root"); info.ArgumentList.Add(repository.Root);
        info.ArgumentList.Add("--session-host"); info.ArgumentList.Add(record.Id);
        using var process = Process.Start(info) ?? throw new IOException("Could not start session worker.");
        record.ProcessId = process.Id; record.ProcessStartedTicks = process.StartTime.ToUniversalTime().Ticks; record.Executable = process.MainModule!.FileName;
        runtime.Save(record);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(65);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited) throw new IOException("Session worker exited during initialization. Read its state; no turn was started.");
            var path = runtime.PathFor(record.Id, ".state.json");
            if (File.Exists(path))
            {
                using var state = JsonDocument.Parse(File.ReadAllText(path));
                var status = CodexThreadReader.Text(state.RootElement, "status");
                if (status == "idle") return runtime.Get(record.Id);
                if (status == "failed") throw new IOException("Session initialization failed. Read its state; no turn was started.");
            }
            await Task.Delay(100);
        }
        throw new IOException("Session initialization still pending. Read status; do not start a duplicate worker.");
    }
    private async Task Stop(CliSessionRecord record)
    {
        if (!SessionRuntime.Alive(record)) return;
        if (record.Mode == "terminal") SessionRuntime.StopTerminal(record);
        else
        {
            await AutomationPipe.Call(AutomationPipe.Name(repository.Root, "session-" + record.Id), "stop", new { });
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (SessionRuntime.Alive(record) && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
            if (SessionRuntime.Alive(record)) throw new IOException("Host shutdown still pending. No replacement was started.");
        }
    }
    private static string Required(JsonElement args, string key) => Optional(args, key) ?? throw new IOException("Missing argument: " + key);
    private static string? Optional(JsonElement args, string key) => CodexThreadReader.Text(args, key);
}
