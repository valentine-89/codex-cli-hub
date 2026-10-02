using System.Text.Json;

namespace CodexAccountManager.Core;

public sealed record CatalogSession(string Id, string Title, string? WorkingDirectory, DateTime UpdatedUtc,
    string? ProjectId = null, bool IsProjectless = false)
{
    public override string ToString() => $"{UpdatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}  —  {Title}";
}

public sealed record WorkspaceCatalog(IReadOnlyList<SessionProject> Projects, IReadOnlyList<CatalogSession> Sessions,
    string? Warning = null);

public sealed record CliSelection(string? Directory, string? SessionId = null, bool IsProjectless = false);

public sealed class WorkspaceCatalogService
{
    public async Task<WorkspaceCatalog> ReadAsync(Dependencies dependencies, string profileHome, string sharedHome,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CatalogSession> sessions;
        string? warning = null;
        try { sessions = await new CodexThreadReader().ReadAsync(dependencies, profileHome, sharedHome, cancellationToken); }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or System.ComponentModel.Win32Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fallback = new SessionProjectCatalog().Read(Path.Combine(sharedHome, "sessions"), cancellationToken);
            sessions = fallback.Sessions;
            warning = "Codex session list unavailable; showing legacy session metadata.";
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Build(sharedHome, sessions, warning, cancellationToken);
    }

    public static string ChatRoot(string sharedHome) => Path.Combine(PathSafety.Canonical(sharedHome), "manager-chats");

    public static WorkspaceCatalog Build(string sharedHome, IReadOnlyList<CatalogSession> sessions, string? warning = null,
        CancellationToken cancellationToken = default)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectless = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectlessDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = Path.Combine(sharedHome, ".codex-global-state.json");
        if (File.Exists(state))
        {
            try
            {
                PathSafety.NoReparseAncestors(state);
                using var stream = new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream);
                var json = document.RootElement;
                AddPaths(json, "electron-saved-workspace-roots");
                AddPaths(json, "active-workspace-roots");
                if (json.TryGetProperty("local-projects", out var local) && local.ValueKind == JsonValueKind.Object)
                    foreach (var entry in local.EnumerateObject()) AddPaths(entry.Value, "rootPaths");
                if (json.TryGetProperty("projectless-thread-ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                    foreach (var id in ids.EnumerateArray()) if (id.ValueKind == JsonValueKind.String) projectless.Add(id.GetString()!);
                if (json.TryGetProperty("thread-project-assignments", out var assignments) && assignments.ValueKind == JsonValueKind.Object)
                    foreach (var entry in assignments.EnumerateObject())
                        if (CodexThreadReader.Text(entry.Value, "projectId") is not null) assigned.Add(entry.Name);
                if (json.TryGetProperty("thread-projectless-output-directories", out var outputs) && outputs.ValueKind == JsonValueKind.Object)
                    foreach (var entry in outputs.EnumerateObject())
                    {
                        projectless.Add(entry.Name);
                        if (entry.Value.ValueKind == JsonValueKind.String && TryNormalize(entry.Value.GetString()) is { } output)
                            projectlessDirectories.Add(Path.GetDirectoryName(output)!);
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { warning = (warning is null ? "" : warning + " ") + "Desktop workspace metadata unavailable."; }
        }

        var normalized = new List<CatalogSession>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chatRoot = ChatRoot(sharedHome);
        foreach (var session in sessions.OrderByDescending(s => s.UpdatedUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(session.Id)) continue;
            var path = TryNormalize(session.WorkingDirectory);
            // A null projectId alone does not mean projectless: CLI projects also have no projectId.
            var noProject = session.ProjectId is null && !assigned.Contains(session.Id)
                && (session.IsProjectless || projectless.Contains(session.Id) || string.IsNullOrWhiteSpace(session.WorkingDirectory)
                    || path is not null && (Within(path, chatRoot) || DesktopChatWorkspace.RecognizedRoot(path) is not null
                        || projectlessDirectories.Contains(path)));
            normalized.Add(session with { WorkingDirectory = path ?? session.WorkingDirectory, IsProjectless = noProject });
        }
        var folders = new Dictionary<string, SessionProject>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots) folders[root] = new(root, DateTime.UnixEpoch, 0, Directory.Exists(root));
        foreach (var session in normalized.Where(s => !s.IsProjectless))
        {
            if (TryNormalize(session.WorkingDirectory) is not { } path) continue;
            folders.TryGetValue(path, out var old);
            folders[path] = new(path, old is not null && old.LastUsedUtc > session.UpdatedUtc ? old.LastUsedUtc : session.UpdatedUtc,
                (old?.SessionCount ?? 0) + 1, Directory.Exists(path));
        }
        return new(folders.Values.OrderByDescending(p => p.Exists).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Directory, StringComparer.OrdinalIgnoreCase).ToArray(), normalized, warning);

        void AddPaths(JsonElement value, string key)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var paths) || paths.ValueKind != JsonValueKind.Array) return;
            foreach (var item in paths.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && TryNormalize(item.GetString()) is { } path) roots.Add(path);
        }
    }

    public static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return SessionProjectCatalog.NormalizeWorkingDirectory(path); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return null; }
    }

    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static string ResolveDirectory(CliSelection selection, string sharedHome, string? userHome = null)
    {
        if (selection.Directory is { } directory && TryNormalize(directory) is { } path && Directory.Exists(path)) return path;
        if (!selection.IsProjectless) throw new IOException("Project folder is unavailable. Use Browse to select its current location.");
        // Desktop also recognizes this cwd layout without modifying its live global-state file.
        // Keep ChatRoot solely for discovering sessions made by manager 1.10.0/1.10.1.
        return DesktopChatWorkspace.Create(userHome);
    }
}
