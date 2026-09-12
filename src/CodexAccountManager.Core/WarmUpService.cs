using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager.Core;

public sealed class WarmUpService
{
    public static ProcessStartInfo StartInfo(string codex, string home, string directory)
    {
        var info = new ProcessStartInfo(codex) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "exec", "--ephemeral", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check",
            "--sandbox", "read-only", "--json", "-c", "cli_auth_credentials_store=\"file\"",
            "-c", "approval_policy=\"never\"", "-c", "project_doc_max_bytes=0", "reply \"OK\"" }) info.ArgumentList.Add(arg);
        foreach (var key in ShellRunner.IsolatedEnvironmentVariables) info.Environment.Remove(key);
        info.Environment["CODEX_HOME"] = home;
        return info;
    }

    public async Task RunAsync(Dependencies dependencies, string home, CancellationToken cancellationToken)
    {
        if (dependencies.NativeCodex is null) throw new IOException("Codex executable not found. Install/update Codex CLI and retry.");
        PathSafety.OrdinaryDirectory(home);
        var directory = Path.Combine(Path.GetTempPath(), "CodexWarmUp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var process = Process.Start(StartInfo(dependencies.NativeCodex, home, directory)) ?? throw new IOException("Could not start warm up.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var receivedOk = false;
            var output = ReadOutput(); var errors = DrainErrors();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(output, errors);
                if (process.ExitCode != 0 || !receivedOk)
                    throw new IOException("Warm up did not return OK. Check login, quota and CLI support for --ephemeral/--ignore-user-config before retrying.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("Warm up timed out. It may have used quota; it was not retried."); }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
            }
            async Task ReadOutput()
            {
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(line);
                        var root = json.RootElement;
                        if (root.TryGetProperty("type", out var type) && type.GetString() == "item.completed"
                            && root.TryGetProperty("item", out var item) && item.TryGetProperty("type", out var itemType)
                            && itemType.GetString() == "agent_message" && item.TryGetProperty("text", out var text))
                            receivedOk = text.GetString()?.Trim() == "OK";
                    }
                    catch (JsonException) { }
                }
            }
            async Task DrainErrors()
            {
                var buffer = new char[2048];
                while (await process.StandardError.ReadAsync(buffer.AsMemory(), timeout.Token) != 0) { }
            }
        }
        finally
        {
            // Never recursively delete a directory after a model process has used it.
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        }
    }
}
