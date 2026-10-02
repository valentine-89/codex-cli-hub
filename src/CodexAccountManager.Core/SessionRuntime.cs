using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountManager.Core;

public sealed class CliSessionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = "";
    public string? ThreadId { get; set; }
    public string WorkingDirectory { get; set; } = "";
    public string Mode { get; set; } = "managed";
    public int ProcessId { get; set; }
    public long ProcessStartedTicks { get; set; }
    public string? Executable { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SessionRuntime(string root)
{
    public string Root { get; } = PathSafety.Canonical(root);
    public string DirectoryPath => Path.Combine(Root, "runtime", "sessions");
    public string PathFor(string id, string suffix = ".json")
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("Invalid managed session ID.");
        var path = Path.Combine(DirectoryPath, id + suffix);
        PathSafety.NoReparseAncestors(path);
        return path;
    }
    public IReadOnlyList<CliSessionRecord> List()
    {
        PathSafety.NoReparseAncestors(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) return [];
        var records = new List<CliSessionRecord>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) continue;
            PathSafety.NoReparseAncestors(path);
            var record = JsonSerializer.Deserialize<CliSessionRecord>(File.ReadAllText(path), AutomationPipe.Json)
                ?? throw new IOException("Invalid runtime registry. File preserved.");
            if (!PathSafety.Same(PathFor(record.Id), path)) throw new IOException("Runtime registry ID mismatch.");
            records.Add(record);
        }
        return records;
    }
    public CliSessionRecord Get(string id) => List().SingleOrDefault(r => r.Id == id) ?? throw new IOException("Managed session not found.");
    public void Save(CliSessionRecord record) => Write(PathFor(record.Id), record);
    public void Write(string path, object value)
    {
        PathSafety.NoReparseAncestors(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        PathSafety.NoReparseAncestors(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, AutomationPipe.Json), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static bool Alive(CliSessionRecord record)
    {
        if (record.ProcessId <= 0 || record.ProcessStartedTicks <= 0 || record.Executable is null) return false;
        try
        {
            using var process = Process.GetProcessById(record.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.ProcessStartedTicks
                && PathSafety.Same(process.MainModule!.FileName, record.Executable);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    public CliSessionRecord Track(Process process, string accountId, string cwd, string? threadId, string mode)
    {
        var record = new CliSessionRecord { AccountId = accountId, WorkingDirectory = cwd, ThreadId = threadId, Mode = mode,
            ProcessId = process.Id, ProcessStartedTicks = process.StartTime.ToUniversalTime().Ticks, Executable = process.MainModule!.FileName };
        Save(record); return record;
    }
    // Import only launches whose parent is this exact portable Manager executable and whose
    // generated script matches a registered isolated profile. No arbitrary PID adoption tool.
    public async Task DiscoverExisting(Dependencies dependencies, IReadOnlyList<Account> accounts, string sharedHome)
    {
        if (dependencies.PowerShell is null) throw new IOException("PowerShell 7 unavailable.");
        var result = await ShellRunner.RunAsync(dependencies.PowerShell,
            "Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('pwsh.exe','CodexAccountManager.exe') } | Select-Object ProcessId,ParentProcessId,ExecutablePath,CommandLine | ConvertTo-Json -Compress", null);
        if (result.ExitCode != 0) throw new IOException("Could not discover Manager-owned terminals.");
        using var doc = JsonDocument.Parse(result.Output);
        var rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : [doc.RootElement];
        var owners = rows.Where(r => CodexThreadReader.Text(r, "ExecutablePath") is { } path
            && PathSafety.Same(path, Path.Combine(Root, "CodexAccountManager.exe")))
            .Select(r => r.GetProperty("ProcessId").GetInt32()).ToHashSet();
        var known = List().Where(Alive).Select(r => r.ProcessId).ToHashSet();
        foreach (var row in rows)
        {
            var pid = row.GetProperty("ProcessId").GetInt32();
            if (known.Contains(pid)) continue;
            var command = CodexThreadReader.Text(row, "CommandLine") ?? "";
            var match = Regex.Match(command, @"-EncodedCommand\s+([A-Za-z0-9+/=]+)", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            string script;
            try { script = Encoding.Unicode.GetString(Convert.FromBase64String(match.Groups[1].Value)); }
            catch (FormatException) { continue; }
            var homeMatch = Regex.Match(script, @"\$env:CODEX_HOME\s*=\s*'((?:[^']|'')*)'", RegexOptions.IgnoreCase);
            var cwdMatch = Regex.Match(script, @"Set-Location -LiteralPath\s*'((?:[^']|'')*)'", RegexOptions.IgnoreCase);
            if (!homeMatch.Success || !cwdMatch.Success || script.Contains(" login", StringComparison.Ordinal)) continue;
            var home = homeMatch.Groups[1].Value.Replace("''", "'");
            var account = accounts.SingleOrDefault(a => PathSafety.Same(PathSafety.Profile(Root, a), home));
            if (account is null) continue;
            var thread = Regex.Match(script, @"resume --all\s*'([a-fA-F0-9-]{36})'");
            var cwd = cwdMatch.Groups[1].Value.Replace("''", "'");
            var threadId = thread.Success ? thread.Groups[1].Value : null;
            // A Manager may have been closed before this upgrade. In that case require
            // an exact match to its generated script, including executable/home/SQLite/cwd.
            if (!owners.Contains(row.GetProperty("ParentProcessId").GetInt32())
                && script != CodexProcessLauncher.BuildScript(dependencies.Codex!, home, cwd,
                    threadId is null ? CodexAction.Open : CodexAction.Resume, sharedHome, threadId)) continue;
            if (CodexThreadReader.Text(row, "ExecutablePath") is not { } executable
                || !Path.GetFileName(executable).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)) continue;
            using var process = Process.GetProcessById(pid);
            Track(process, account.Id, cwd, threadId, "terminal");
        }
    }
    public object Read(CliSessionRecord record, string sharedHome)
    {
        if (record.Mode == "managed")
        {
            var path = PathFor(record.Id, ".state.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return new { session = record, alive = Alive(record), state = doc.RootElement.Clone() };
            }
        }
        return new { session = record, alive = Alive(record), state = RolloutStatus.Read(sharedHome, record.ThreadId) };
    }
    public static void StopTerminal(CliSessionRecord record)
    {
        if (record.Mode != "terminal" || !Alive(record)) throw new IOException("Terminal process identity changed or is no longer running.");
        using var process = Process.GetProcessById(record.ProcessId);
        // Registry identity and creation time were verified immediately before terminating
        // the Manager-owned wrapper, never the shared Windows Terminal process.
        process.Kill(entireProcessTree: true);
        process.WaitForExit(10000);
        if (!process.HasExited) throw new IOException("Terminal did not stop. No replacement was started.");
    }
}

public static class RolloutStatus
{
    public static string? Find(string sharedHome, string? threadId)
    {
        if (!Guid.TryParse(threadId, out _)) return null;
        var sessions = Path.Combine(PathSafety.Canonical(sharedHome), "sessions");
        PathSafety.NoReparseAncestors(sessions);
        if (!Directory.Exists(sessions)) return null;
        // Traverse only the known year/month/day layout; do not follow directory links.
        foreach (var year in Directory.EnumerateDirectories(sessions).OrderDescending())
        foreach (var month in OrdinaryChildren(year))
        foreach (var day in OrdinaryChildren(month))
        foreach (var file in Directory.EnumerateFiles(day, "*" + threadId + "*.jsonl"))
        { PathSafety.NoReparseAncestors(file); return file; }
        return null;
    }
    private static IEnumerable<string> OrdinaryChildren(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) yield break;
        foreach (var child in Directory.EnumerateDirectories(path))
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) yield return child;
    }
    public static object Read(string sharedHome, string? threadId)
    {
        var path = Find(sharedHome, threadId);
        if (path is null) return new { status = "unknown", lastEventAt = (string?)null };
        var events = Tail(path);
        JsonElement? limits = null;
        string? timestamp = null, final = null, status = null, error = null;
        foreach (var line in events)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var value = doc.RootElement;
                if (!value.TryGetProperty("payload", out var payload)) continue;
                timestamp = CodexThreadReader.Text(value, "timestamp") ?? timestamp;
                if (CodexThreadReader.Text(value, "type") != "event_msg") continue;
                switch (CodexThreadReader.Text(payload, "type"))
                {
                    case "token_count": if (payload.TryGetProperty("rate_limits", out var quota)) limits = quota.Clone(); break;
                    case "task_started": status = "active"; break;
                    case "task_complete": case "task_completed": status = "completed"; final = CodexThreadReader.Text(payload, "last_agent_message"); break;
                    case "turn_aborted": status = "interrupted"; break;
                    case "error": status = "failed"; error = CodexThreadReader.Text(payload, "message"); break;
                }
            }
            catch (JsonException) { }
        }
        return new { status = status ?? "running_or_idle", lastEventAt = timestamp, rateLimits = limits,
            finalMessage = Limit(final), error = Limit(error) };
    }
    public static string? Limit(string? text) => text is null ? null : text.Length <= 4000 ? text : text[..4000];
    public static string[] Tail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var skipped = stream.Length > 1024 * 1024;
        if (skipped) stream.Seek(-1024 * 1024, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        if (skipped) _ = reader.ReadLine();
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
    public static JsonElement? LastContext(string sharedHome, string? threadId)
    {
        var path = Find(sharedHome, threadId);
        if (path is null) return null;
        // Context may precede a large tool output. Stream without retaining the history.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        JsonElement? context = null;
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("\"type\":\"turn_context\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (CodexThreadReader.Text(doc.RootElement, "type") == "turn_context") context = doc.RootElement.GetProperty("payload").Clone();
            }
            catch (JsonException) { }
        }
        return context;
    }
}
