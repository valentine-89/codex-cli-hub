namespace CodexAccountManager.Core;

internal static class WorkspacePaths
{
    internal static bool IsPosix(string path) => path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl)
        && !path.Split('/').Any(part => part is "." or "..");

    internal static IReadOnlyList<CatalogSession> RestorePosixDirectories(string sharedHome,
        IReadOnlyList<CatalogSession> sessions, CancellationToken cancellationToken = default)
    {
        var database = Path.Combine(sharedHome, "state_5.sqlite");
        if (sessions.Count == 0 || !File.Exists(database)) return sessions;
        // Windows app-server serializes /home/... as <its current drive>:\home\...
        // Keep RPC membership/titles/order, consulting only the original cwd for those same IDs.
        // Never infer a Linux origin from a Windows-looking path alone or rewrite the live database.
        using var db = new NativeSqlite(database);
        var original = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in db.Query("SELECT id,cwd FROM threads WHERE substr(cwd,1,1)='/'"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row[0] is { } id && row[1] is { } cwd && IsPosix(cwd)) original[id] = cwd;
        }
        return sessions.Select(session => original.TryGetValue(session.Id, out var cwd)
            && IsWindowsProjection(session.WorkingDirectory, cwd) ? session with { WorkingDirectory = cwd } : session).ToArray();
    }

    private static bool IsWindowsProjection(string? current, string posix)
    {
        if (current is null) return false;
        if (current.StartsWith(@"\\?\", StringComparison.Ordinal)) current = current[4..];
        return current.Length > 2 && char.IsAsciiLetter(current[0]) && current[1] == ':'
            && current[2..].Replace('\\', '/').TrimEnd('/').Equals(posix.TrimEnd('/'), StringComparison.Ordinal);
    }

    internal static string? Resolve(string? cwd, IEnumerable<string> knownRoots)
    {
        if (WorkspaceCatalogService.TryNormalize(cwd) is { } normalized) return normalized;
        if (cwd is null || !IsPosix(cwd)) return null;
        var linuxPath = cwd.TrimEnd('/');
        var matches = new List<(int Length, string Path)>();
        foreach (var root in knownRoots)
        {
            if (!root.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase)) continue;
            var separator = root.IndexOf('\\', 16);
            if (separator < 0) continue;
            var linuxRoot = root[separator..].Replace('\\', '/').TrimEnd('/');
            if (linuxPath.Equals(linuxRoot, StringComparison.Ordinal) || linuxPath.StartsWith(linuxRoot + "/", StringComparison.Ordinal))
                matches.Add((linuxRoot.Length, root[..separator] + linuxPath.Replace('/', '\\')));
        }
        if (matches.Count == 0) return cwd;
        var longest = matches.Max(match => match.Length);
        var candidates = matches.Where(match => match.Length == longest).Select(match => match.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // A Linux path does not identify its distro. Preserve ambiguous/offline paths for display;
        // never guess the default distro or turn an unresolved path into a local drive path.
        return candidates.Length == 1 ? candidates[0] : cwd;
    }

    internal static bool Exists(string path) => !IsPosix(path) && Directory.Exists(path);
}
