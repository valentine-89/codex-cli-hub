using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager.Core;

// A separate, noninteractive Codex CLI app-server process per managed session. The
// worker outlives MCP clients and the Manager UI, including while Windows is locked.
public sealed class ManagedCliHost : IDisposable
{
    private readonly SessionRuntime runtime;
    private readonly CliSessionRecord record;
    private readonly string profileHome;
    private readonly string sharedHome;
    private readonly Dependencies dependencies;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> replies = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly SemaphoreSlim actions = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<string, JsonElement> requests = [];
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? process;
    private AutomationPipe? pipe;
    private int sequence;
    private string status = "starting";
    private string? turnId, lastMessage, error;
    private DateTimeOffset lastEventAt = DateTimeOffset.UtcNow, lastSaved = DateTimeOffset.MinValue;
    private JsonElement? rateLimits;
    private JsonElement? savedSandboxPolicy, savedApprovalPolicy;
    private readonly Queue<object> events = new();
    public ManagedCliHost(SessionRuntime runtime, CliSessionRecord record, Dependencies dependencies, string profileHome, string sharedHome)
    { this.runtime = runtime; this.record = record; this.dependencies = dependencies; this.profileHome = profileHome; this.sharedHome = sharedHome; }
    public static ProcessStartInfo StartInfo(string nativeCodex, string home, string shared, string cwd)
    {
        var info = new ProcessStartInfo(nativeCodex) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = cwd, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var key in ShellRunner.IsolatedEnvironmentVariables) info.Environment.Remove(key);
        info.Environment["CODEX_HOME"] = home;
        info.Environment["CODEX_SQLITE_HOME"] = shared;
        info.ArgumentList.Add("-c"); info.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        info.ArgumentList.Add("-c"); info.ArgumentList.Add("sqlite_home=" + JsonSerializer.Serialize(shared));
        info.ArgumentList.Add("app-server"); info.ArgumentList.Add("--listen"); info.ArgumentList.Add("stdio://");
        return info;
    }
    public async Task Run()
    {
        try
        {
            if (dependencies.NativeCodex is null) throw new IOException("Native Codex CLI unavailable.");
            process = Process.Start(StartInfo(dependencies.NativeCodex, profileHome, sharedHome, record.WorkingDirectory))
                ?? throw new IOException("Could not start the managed Codex CLI.");
            _ = Drain(process.StandardError);
            _ = Receive();
            await Request("initialize", new { clientInfo = new { name = "codex_account_manager", title = "Codex Account Manager", version = "1.11.1" },
                capabilities = new { experimentalApi = true } });
            await Send(new { method = "initialized", @params = new { } });
            var resume = new Dictionary<string, object?> { ["cwd"] = record.WorkingDirectory };
            if (record.ThreadId is { } thread)
            {
                resume["threadId"] = thread;
                // Preserve the latest saved model and permission policy across account changes.
                if (RolloutStatus.LastContext(sharedHome, thread) is { } context)
                {
                    if (CodexThreadReader.Text(context, "model") is { } model) resume["model"] = model;
                    if (context.TryGetProperty("approval_policy", out var approval)) { resume["approvalPolicy"] = approval; savedApprovalPolicy = approval; }
                    if (CodexThreadReader.Text(context, "effort") is { } effort)
                        resume["config"] = new Dictionary<string, object> { ["model_reasoning_effort"] = effort };
                    if (context.TryGetProperty("sandbox_policy", out var sandbox))
                    {
                        savedSandboxPolicy = SandboxOverride(sandbox);
                        resume["sandbox"] = CodexThreadReader.Text(sandbox, "type") switch
                        { "danger-full-access" => "danger-full-access", "read-only" => "read-only", "workspace-write" => "workspace-write", _ => throw new IOException("Saved sandbox policy cannot be represented; no turn was started.") };
                    }
                }
            }
            var result = await Request(record.ThreadId is null ? "thread/start" : "thread/resume", resume);
            var returnedThread = result.GetProperty("thread").GetProperty("id").GetString()!;
            if (record.ThreadId is not null && returnedThread != record.ThreadId) throw new IOException("Codex resumed a different session. No turn was started.");
            record.ThreadId = returnedThread;
            runtime.Save(record);
            lock (sync) { status = "idle"; Save(true); }
            pipe = new(AutomationPipe.Name(runtime.Root, "session-" + record.Id), Handle);
            pipe.Start();
            await stopped.Task;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or TimeoutException or ArgumentException or System.ComponentModel.Win32Exception)
        { lock (sync) { status = "failed"; error = AutomationErrors.Safe(ex); Save(true); } }
        finally
        {
            pipe?.Dispose();
            if (process is not null)
            {
                if (!process.HasExited) { process.StandardInput.Close(); if (!process.WaitForExit(2000)) process.Kill(true); }
                process.Dispose();
            }
        }
    }
    public object Snapshot()
    {
        lock (sync) return new { status, threadId = record.ThreadId, turnId, cliProcessId = process?.Id, lastEventAt,
            lastMessage, error, rateLimits, pendingRequests = requests.Select(r => new { requestId = r.Key, request = r.Value }).ToArray(), events = events.ToArray() };
    }
    private void Save(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - lastSaved < TimeSpan.FromSeconds(1)) return;
        runtime.Write(runtime.PathFor(record.Id, ".state.json"), Snapshot()); lastSaved = DateTimeOffset.UtcNow;
    }
    public async Task<object> Handle(string method, JsonElement args)
    {
        await actions.WaitAsync();
        try
        {
            if (method == "read") return Snapshot();
            if (method == "send" || method == "steer")
            {
                var text = args.GetProperty("text").GetString();
                if (string.IsNullOrWhiteSpace(text) || text.Length > 100000) throw new IOException("Prompt must contain 1–100000 characters.");
                string? active; lock (sync) active = status == "active" ? turnId : null;
                if (method == "steer" && active is null) throw new IOException("No active turn to steer.");
                if (method == "send" && active is not null) throw new IOException("Turn already active. Use session_steer or interrupt first.");
                lock (sync) { if (requests.Count > 0) throw new IOException("Resolve pending requests before sending a new turn."); error = null; }
                var parameters = new Dictionary<string, object?> { ["threadId"] = record.ThreadId,
                    ["input"] = new[] { new { type = "text", text } } };
                if (active is not null) parameters["expectedTurnId"] = active;
                else
                {
                    parameters["cwd"] = record.WorkingDirectory;
                    if (savedSandboxPolicy is { } sandbox) parameters["sandboxPolicy"] = sandbox;
                    if (savedApprovalPolicy is { } approval) parameters["approvalPolicy"] = approval;
                    lock (sync) { turnId = null; status = "starting_turn"; Save(true); }
                }
                var response = await Request(active is not null ? "turn/steer" : "turn/start", parameters);
                lock (sync)
                {
                    if (active is null)
                    {
                        var accepted = response.GetProperty("turn").GetProperty("id").GetString();
                        // Events can overtake this response continuation; never overwrite
                        // an already observed completion with a stale active status.
                        if (turnId != accepted) { turnId = accepted; status = "active"; }
                    }
                    Save(true);
                }
                return Snapshot();
            }
            if (method == "interrupt" || method == "stop")
            {
                string? active; lock (sync) active = status == "active" ? turnId : null;
                if (active is not null)
                {
                    await Request("turn/interrupt", new { threadId = record.ThreadId, turnId = active });
                    var until = DateTimeOffset.UtcNow.AddSeconds(30);
                    while (DateTimeOffset.UtcNow < until)
                    {
                        lock (sync) { if (status != "active") break; }
                        await Task.Delay(100);
                    }
                    lock (sync) { if (status == "active") throw new IOException("Interrupt still pending. Re-read status; no replacement started."); }
                }
                if (method == "stop")
                {
                    lock (sync) { status = "stopped"; Save(true); }
                    _ = Task.Run(async () => { await Task.Delay(300); stopped.TrySetResult(); });
                }
                return Snapshot();
            }
            if (method == "reply")
            {
                var id = args.GetProperty("requestId").GetString()!;
                JsonElement request;
                lock (sync) { if (!requests.TryGetValue(id, out request)) throw new IOException("Pending request no longer exists."); }
                var result = args.GetProperty("result");
                ValidateReply(request.GetProperty("method").GetString()!, result);
                await Send(new { id = request.GetProperty("id"), result });
                lock (sync) { requests.Remove(id); Save(true); }
                return Snapshot();
            }
            throw new IOException("Unknown session operation.");
        }
        finally { actions.Release(); }
    }
    public static void ValidateReply(string method, JsonElement result)
    {
        if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
        {
            var decision = CodexThreadReader.Text(result, "decision");
            if (decision is not ("accept" or "acceptForSession" or "decline" or "cancel")) throw new IOException("Unsupported approval decision.");
        }
        else if (method == "item/tool/requestUserInput")
        { if (!result.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object) throw new IOException("Expected user input answers."); }
        else throw new IOException("Request type requires an explicit supported handler. No approval was sent.");
    }
    public static JsonElement SandboxOverride(JsonElement saved)
    {
        var type = CodexThreadReader.Text(saved, "type") switch
        { "danger-full-access" => "dangerFullAccess", "read-only" => "readOnly", "workspace-write" => "workspaceWrite",
            _ => throw new IOException("Unsupported saved sandbox policy. No turn was started.") };
        var result = new Dictionary<string, object> { ["type"] = type };
        foreach (var (from, to) in new[] { ("network_access", "networkAccess"), ("writable_roots", "writableRoots"),
            ("exclude_tmpdir_env_var", "excludeTmpdirEnvVar"), ("exclude_slash_tmp", "excludeSlashTmp") })
            if (saved.TryGetProperty(from, out var value)) result[to] = value.Clone();
        return JsonSerializer.SerializeToElement(result);
    }
    private async Task<JsonElement> Request(string method, object? parameters)
    {
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        replies[id] = completion;
        try
        {
            await Send(new { id, method, @params = parameters });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally { replies.TryRemove(id, out _); }
    }
    private async Task Send(object message)
    {
        await writes.WaitAsync();
        try
        {
            if (process is null || process.HasExited) throw new IOException("Codex CLI host exited.");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
            await process.StandardInput.FlushAsync();
        }
        finally { writes.Release(); }
    }
    private async Task Receive()
    {
        try
        {
            while (await process!.StandardOutput.ReadLineAsync() is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var value = doc.RootElement;
                if (value.TryGetProperty("id", out var id))
                {
                    if (value.TryGetProperty("method", out _))
                    { lock (sync) { requests[id.ToString()] = value.Clone(); lastEventAt = DateTimeOffset.UtcNow; Save(true); } continue; }
                    if (!id.TryGetInt32(out var number) || !replies.TryGetValue(number, out var reply)) continue;
                    if (value.TryGetProperty("error", out var rpcError))
                        reply.TrySetException(new IOException("Codex RPC failed (" + rpcError.GetProperty("code") + "). Check session status; no retry was submitted."));
                    else reply.TrySetResult(value.GetProperty("result").Clone());
                    continue;
                }
                var method = CodexThreadReader.Text(value, "method");
                if (!value.TryGetProperty("params", out var parameters)) continue;
                lock (sync)
                {
                    lastEventAt = DateTimeOffset.UtcNow;
                    var important = false;
                    switch (method)
                    {
                        case "turn/started": status = "active"; turnId = parameters.GetProperty("turn").GetProperty("id").GetString(); important = true; break;
                        case "turn/completed": status = parameters.GetProperty("turn").GetProperty("status").GetString() ?? "unknown"; important = true; break;
                        case "account/rateLimits/updated": rateLimits = parameters.Clone(); important = true; break;
                        case "error": error = RolloutStatus.Limit(CodexThreadReader.Text(parameters.GetProperty("error"), "message")); important = true; break;
                        case "item/completed":
                            if (parameters.TryGetProperty("item", out var item) && CodexThreadReader.Text(item, "type") == "agentMessage")
                            { lastMessage = RolloutStatus.Limit(CodexThreadReader.Text(item, "text")); important = true; }
                            break;
                        case "serverRequest/resolved": if (parameters.TryGetProperty("requestId", out var rid)) requests.Remove(rid.ToString()); important = true; break;
                    }
                    if (important)
                    {
                        events.Enqueue(new { method, at = lastEventAt }); while (events.Count > 64) events.Dequeue();
                    }
                    Save(important);
                }
            }
            throw new IOException("Codex CLI connection closed.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            foreach (var reply in replies.Values) reply.TrySetException(new IOException("Codex CLI connection failed."));
            lock (sync) { if (status != "stopped") { status = "failed"; error ??= AutomationErrors.Safe(ex); Save(true); } }
            stopped.TrySetResult();
        }
    }
    private static async Task Drain(StreamReader reader)
    { var buffer = new char[4096]; while (await reader.ReadAsync(buffer) > 0) { } }
    public void Dispose() { pipe?.Dispose(); }
}
