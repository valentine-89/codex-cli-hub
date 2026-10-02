using System.Text.Json;

namespace CodexAccountManager.Core;

public sealed record SessionProject(string Directory, DateTime LastUsedUtc, int SessionCount, bool Exists)
{
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory)) is { Length: > 0 } name ? name : Directory;
    public override string ToString() => Name + "  —  " + Directory + (Exists ? "" : "  — no longer exists");
}
public sealed record ProjectCatalog(IReadOnlyList<SessionProject> Projects, int SkippedFiles)
{
    public IReadOnlyList<CatalogSession> Sessions { get; init; } = [];
}

public sealed class SessionProjectCatalog
{
    public ProjectCatalog Read(string sessionsDirectory, CancellationToken cancellationToken = default)
    {
        PathSafety.OrdinaryDirectory(sessionsDirectory);
        var projects = new Dictionary<string, SessionProject>(StringComparer.OrdinalIgnoreCase);
        var sessions = new List<CatalogSession>();
        var pending = new Stack<string>(); pending.Push(sessionsDirectory);
        var skipped = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PathSafety.OrdinaryDirectory(directory);
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                        if (!entry.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                        var metadata = ReadMetadata(entry, cancellationToken);
                        if (metadata is null) { skipped++; continue; }
                        var lastUsed = File.GetLastWriteTimeUtc(entry);
                        sessions.Add(metadata with { UpdatedUtc = lastUsed });
                        if (string.IsNullOrWhiteSpace(metadata.WorkingDirectory)) continue;
                        var path = NormalizeWorkingDirectory(metadata.WorkingDirectory);
                        projects.TryGetValue(path, out var previous);
                        projects[path] = new(path, previous is not null && previous.LastUsedUtc > lastUsed ? previous.LastUsedUtc : lastUsed,
                            (previous?.SessionCount ?? 0) + 1, Directory.Exists(path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                    { skipped++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(projects.Values.OrderByDescending(p => p.Exists)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Directory, StringComparer.OrdinalIgnoreCase).ToArray(), skipped) { Sessions = sessions };
    }

    // Historical metadata may use a Windows device prefix or a WSL drive mount.
    // Only map a WSL drive when its Windows directory actually exists.
    public static string NormalizeWorkingDirectory(string cwd)
    {
        if (cwd.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) cwd = @"\\" + cwd[8..];
        if (cwd.StartsWith(@"\\?\", StringComparison.Ordinal) && cwd.Length > 6 && cwd[5] == ':') cwd = cwd[4..];
        if (cwd.StartsWith("/mnt/", StringComparison.Ordinal) && cwd.Length > 7
            && char.IsAsciiLetter(cwd[5]) && cwd[6] == '/')
        {
            var candidate = char.ToUpperInvariant(cwd[5]) + @":\" + cwd[7..].Replace('/', '\\');
            if (Directory.Exists(candidate)) cwd = candidate;
        }
        // Older Windows imports sometimes saved /mnt/d/... as C:\mnt\d\... .
        var legacy = cwd.Replace('/', '\\');
        if (legacy.Length > 9 && legacy[1] == ':' && legacy[2..7].Equals(@"\mnt\", StringComparison.OrdinalIgnoreCase)
            && char.IsAsciiLetter(legacy[7]) && legacy[8] == '\\' && !Directory.Exists(cwd))
        {
            var candidate = char.ToUpperInvariant(legacy[7]) + @":\" + legacy[9..];
            if (Directory.Exists(candidate)) cwd = candidate;
        }
        // Project paths are read/open targets, unlike the strictly local private profile paths.
        // Accept normal UNC shares and WSL shares; never relax PathSafety for profile deletion.
        if (cwd.StartsWith(@"\\") && !cwd.StartsWith(@"\\?\") && !cwd.StartsWith(@"\\.\"))
        {
            if (cwd.StartsWith(@"\\wsl$\", StringComparison.OrdinalIgnoreCase)) cwd = @"\\wsl.localhost\" + cwd[7..];
            var parts = cwd[2..].Split(['\\', '/']);
            if (parts.Length < 2 || parts.Take(2).Any(string.IsNullOrWhiteSpace) || parts.Any(p => p is "." or ".." || p.Contains(':')))
                throw new IOException("Invalid project share path.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        }
        return PathSafety.Canonical(cwd);
    }

    private static CatalogSession? ReadMetadata(string file, CancellationToken cancellationToken)
    {
        // Read only the first record; metadata property order and instruction size vary by version.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var header = new System.Text.StringBuilder();
        while (reader.Read() is var next && next >= 0 && next != '\n')
        {
            if (header.Length % 4096 == 0) cancellationToken.ThrowIfCancellationRequested();
            if (header.Length >= 16 * 1024 * 1024) throw new IOException("Session metadata is too large.");
            header.Append((char)next);
        }
        using var document = JsonDocument.Parse(header.ToString());
        var json = document.RootElement;
        if (CodexThreadReader.Text(json, "type") != "session_meta" || !json.TryGetProperty("payload", out var payload)) return null;
        if (payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object) return null;
        var id = CodexThreadReader.Text(payload, "id") ?? file;
        return new(id, CodexThreadReader.Text(payload, "name") ?? id, CodexThreadReader.Text(payload, "cwd"), DateTime.UnixEpoch);
    }
}
