using System.Diagnostics;
using System.Text;

namespace CodexAccountManager.Core;

public sealed record Dependencies(string? PowerShell, string? Codex, bool LoginStatusSupported)
{
    public void Require()
    {
        if (PowerShell is null) throw new IOException("PowerShell 7 (pwsh.exe) was not found. Install it or add it to PATH, then Refresh.");
        if (Codex is null) throw new IOException("Codex CLI was not found. Install the official CLI or add it to PATH, then Refresh.");
    }
}

public static class DependencyDetector
{
    private static string? FindPowerShell()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(p => p.Trim('"'))
            .Concat([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7")]);
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, "pwsh.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    public static async Task<Dependencies> DetectAsync()
    {
        var pwsh = FindPowerShell();
        if (pwsh is null) return new(null, null, false);
        var result = await ShellRunner.RunAsync(pwsh,
            "if ($PSVersionTable.PSVersion.Major -lt 7) { exit 9 }; $c = Get-Command codex -CommandType Application,ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1; if ($c) { [Console]::Out.Write($c.Source) }", null);
        if (result.ExitCode != 0) return new(null, null, false);
        var codex = result.Output.Trim();
        if (!Path.IsPathFullyQualified(codex) || !File.Exists(codex)) return new(pwsh, null, false);
        var help = await ShellRunner.RunAsync(pwsh, "& " + ShellRunner.Quote(codex) + " login --help; exit $LASTEXITCODE", null);
        return new(pwsh, codex, help.ExitCode == 0 && help.Output.Contains("status", StringComparison.Ordinal));
    }
}

public sealed record ShellResult(int ExitCode, string Output);

public static class ShellRunner
{
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    public static async Task<ShellResult> RunAsync(string pwsh, string script, string? home, int timeoutSeconds = 20)
    {
        var info = new ProcessStartInfo(pwsh) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8 };
        info.ArgumentList.Add("-NoLogo"); info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand"); info.ArgumentList.Add(Encode("[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " + script));
        if (home is not null)
        {
            info.Environment["CODEX_HOME"] = home;
            info.WorkingDirectory = home;
            foreach (var key in IsolatedEnvironmentVariables) info.Environment.Remove(key);
        }
        using var process = Process.Start(info) ?? throw new IOException("Cannot start PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            // This is only our bounded diagnostic subprocess, never an interactive terminal.
            process.Kill(true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            throw new TimeoutException("Codex check timed out. Interactive terminals were not affected.");
        }
        return new(process.ExitCode, await stdout + "\n" + await stderr);
    }

    public static readonly string[] IsolatedEnvironmentVariables =
    ["OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_ACCESS_TOKEN",
     "CODEX_THREAD_ID", "CODEX_INTERNAL_ORIGINATOR_OVERRIDE", "CODEX_REMOTE", "CODEX_REMOTE_AUTH_TOKEN",
     "CODEX_APP_SERVER_URL", "CODEX_APP_SERVER_AUTH_TOKEN", "CODEX_SESSION_ID", "CODEX_APP_TOOLS_PIPE_PATH",
     "CODEX_CI", "CODEX_PERMISSION_PROFILE", "CODEX_SHELL", "CODEX_MCP_NODE_PATH", "CODEX_SAGE_BACKFILL_TRACKER_TAB_REUSE"];
}

public enum CodexAction { Open, Login, Resume }

public sealed class CodexProcessLauncher
{
    private readonly Dictionary<string, List<Process>> terminals = [];
    public static string BuildScript(string codex, string home, string workingDirectory, CodexAction action)
    {
        var args = action switch { CodexAction.Login => " login", CodexAction.Resume => " resume --all", _ => "" };
        var clear = string.Join("; ", ShellRunner.IsolatedEnvironmentVariables.Select(k => "Remove-Item Env:" + k + " -ErrorAction SilentlyContinue"));
        return "$ErrorActionPreference = 'Stop'; $env:CODEX_HOME = " + ShellRunner.Quote(PathSafety.Canonical(home))
            + "; " + clear + "; [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); "
            + "Set-Location -LiteralPath " + ShellRunner.Quote(PathSafety.Canonical(workingDirectory))
            + "; & " + ShellRunner.Quote(codex) + " -c 'cli_auth_credentials_store=\"file\"'" + args;
    }

    public void Launch(Dependencies dependencies, string id, string home, string workingDirectory, CodexAction action)
    {
        dependencies.Require();
        PathSafety.OrdinaryDirectory(workingDirectory);
        var info = new ProcessStartInfo(dependencies.PowerShell!) { UseShellExecute = true,
            WorkingDirectory = workingDirectory, WindowStyle = ProcessWindowStyle.Normal };
        info.ArgumentList.Add("-NoLogo"); info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NoExit");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(ShellRunner.Encode(BuildScript(dependencies.Codex!, home, workingDirectory, action)));
        var process = Process.Start(info) ?? throw new IOException("Unable to open the terminal.");
        if (!terminals.TryGetValue(id, out var list)) terminals[id] = list = [];
        list.Add(process);
    }

    public bool IsRunning(string id)
    {
        if (!terminals.TryGetValue(id, out var list)) return false;
        for (var i = list.Count - 1; i >= 0; i--)
            if (list[i].HasExited) { list[i].Dispose(); list.RemoveAt(i); }
        return list.Count > 0;
    }
}

public sealed class CodexStatusService
{
    public async Task<string> CheckAsync(Dependencies dependencies, string home)
    {
        dependencies.Require();
        if (!dependencies.LoginStatusSupported) return "Not available";
        var script = "& " + ShellRunner.Quote(dependencies.Codex!)
            + " -c 'cli_auth_credentials_store=\"file\"' login status; exit $LASTEXITCODE";
        var result = await ShellRunner.RunAsync(dependencies.PowerShell!, script, home);
        return Classify(result);
    }

    public static string Classify(ShellResult result)
    {
        if (result.ExitCode == 0 && result.Output.Contains("Logged in", StringComparison.OrdinalIgnoreCase))
            return "Logged in (local credentials)";
        if (result.ExitCode == 1 && result.Output.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))
            return "Not logged in";
        return $"Check failed (exit {result.ExitCode})";
    }
}
