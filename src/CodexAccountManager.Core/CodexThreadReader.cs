using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager.Core;

// A short-lived, local stdio connection. Listing does not load a thread or start a turn.
public sealed class CodexThreadReader
{
    public async Task<IReadOnlyList<CatalogSession>> ReadAsync(Dependencies dependencies, string profileHome,
        string sharedHome, CancellationToken cancellationToken = default)
    {
        if (dependencies.NativeCodex is null) throw new IOException("Codex executable unavailable for session discovery.");
        PathSafety.OrdinaryDirectory(profileHome);
        PathSafety.OrdinaryDirectory(sharedHome);
        var info = new ProcessStartInfo(dependencies.NativeCodex)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = profileHome
        };
        info.ArgumentList.Add("-c"); info.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        info.ArgumentList.Add("-c"); info.ArgumentList.Add("sqlite_home=" + JsonSerializer.Serialize(PathSafety.Canonical(sharedHome)));
        info.ArgumentList.Add("app-server"); info.ArgumentList.Add("--listen"); info.ArgumentList.Add("stdio://");
        foreach (var key in ShellRunner.IsolatedEnvironmentVariables) info.Environment.Remove(key);
        info.Environment["CODEX_HOME"] = profileHome;
        info.Environment["CODEX_SQLITE_HOME"] = sharedHome;
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new IOException("Could not start session discovery.");
        var diagnostics = Drain(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "codex_account_manager", version = "1.10.0" } } });
            _ = await Receive(1);
            await Send(new { method = "initialized", @params = new { } });
            var sessions = new Dictionary<string, CatalogSession>(StringComparer.OrdinalIgnoreCase);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var requestId = 2;
            do
            {
                await Send(new { id = requestId, method = "thread/list", @params = new {
                    cursor, limit = 100, archived = false, sortKey = "updated_at", modelProviders = Array.Empty<string>(),
                    sourceKinds = new[] { "cli", "vscode", "appServer" }, useStateDbOnly = true
                } });
                var result = await Receive(requestId++);
                if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new IOException("Invalid Codex session list.");
                foreach (var row in data.EnumerateArray())
                {
                    var session = Parse(row);
                    if (session is not null) sessions[session.Id] = session;
                }
                cursor = Text(result, "nextCursor");
                if (cursor is not null && !cursors.Add(cursor)) throw new IOException("Codex repeated a session page.");
            } while (cursor is not null);
            return sessions.Values.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Session discovery timed out."); }
        finally
        {
            process.StandardInput.Close();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(shutdown.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await diagnostics;
        }

        async Task Send<T>(T request)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
        }
        async Task<JsonElement> Receive(int id)
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) throw new IOException("Codex closed session discovery before returning data.");
                using var message = JsonDocument.Parse(line);
                var value = message.RootElement;
                if (!value.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number
                    || !responseId.TryGetInt32(out var number) || number != id) continue;
                if (value.TryGetProperty("error", out _)) throw new IOException("Codex could not list sessions.");
                if (!value.TryGetProperty("result", out var result)) throw new IOException("Invalid Codex response.");
                return result.Clone();
            }
        }
    }

    public static CatalogSession? Parse(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        var id = Text(row, "id");
        if (!Guid.TryParse(id, out _) || Text(row, "parentThreadId") is not null) return null;
        var name = Text(row, "name") ?? Text(row, "preview") ?? id!;
        name = string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 160) name = name[..160] + "…";
        var updated = DateTime.UnixEpoch;
        if (row.TryGetProperty("updatedAt", out var time) && time.ValueKind == JsonValueKind.Number && time.TryGetInt64(out var seconds))
            try { updated = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; } catch (ArgumentOutOfRangeException) { }
        return new(id!, name, Text(row, "cwd"), updated, Text(row, "projectId"));
    }
    public static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var child) && child.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(child.GetString()) ? child.GetString() : null;
    private static async Task Drain(StreamReader reader)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer) != 0) { }
    }
}
