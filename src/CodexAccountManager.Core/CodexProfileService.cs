namespace CodexAccountManager.Core;

public sealed class CodexProfileService(string root, JunctionService junctions)
{
    public string SharedTarget(AppSettings settings)
    {
        var target = PathSafety.Canonical(Path.Combine(settings.DefaultCodexHome, "sessions"));
        PathSafety.OrdinaryDirectory(target);
        if (PathSafety.Same(target, root) || PathSafety.Within(target, root) || PathSafety.Within(root, target))
            throw new IOException("Shared sessions and portable application directories must not overlap.");
        return target;
    }

    public Account Create(string name, string note, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || note.Length > 1000)
            throw new IOException("Enter a name of 1–100 characters and a note up to 1000 characters.");
        var target = SharedTarget(settings);
        var account = new Account { DisplayName = name.Trim(), Note = note.Trim() };
        account.ProfilePath = "profiles/" + account.Id;
        var profile = PathSafety.Profile(root, account);
        if (PathSafety.Exists(profile)) throw new IOException("Generated profile already exists.");
        Directory.CreateDirectory(Path.Combine(root, "profiles"));
        using var parentPin = JunctionService.PinDirectory(Path.Combine(root, "profiles"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "config.toml"), "cli_auth_credentials_store = \"file\"\n");
        junctions.Create(Path.Combine(profile, "sessions"), target);
        return account;
    }

    public string Validate(Account account, AppSettings settings)
    {
        var path = PathSafety.Profile(root, account);
        PathSafety.OrdinaryDirectory(path);
        junctions.Verify(Path.Combine(path, "sessions"), SharedTarget(settings));
        foreach (var name in new[] { "auth.json", "config.toml" })
            PathSafety.NoReparseAncestors(Path.Combine(path, name));
        return path;
    }

    public void Delete(Account account, AppSettings settings)
    {
        var profile = Validate(account, settings);
        var target = SharedTarget(settings);
        // Pin every existing ancestor, so a rename cannot redirect path-based traversal.
        using var pins = PinAncestors(profile);
        using var targetPins = PinAncestors(target);
        ValidateTree(profile, Path.Combine(profile, "sessions"));
        junctions.Verify(Path.Combine(profile, "sessions"), target);
        junctions.Remove(Path.Combine(profile, "sessions"), target);
        DeleteContents(profile);
        pins.Dispose();
        Directory.Delete(profile, false);
        if (PathSafety.Exists(profile) || !Directory.Exists(target)) throw new IOException("Deletion verification failed.");
    }

    private static IDisposable PinAncestors(string path)
    {
        var handles = new HandleGroup();
        try
        {
            var paths = new Stack<string>();
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current)) paths.Push(current);
            foreach (var current in paths) handles.Items.Add(JunctionService.PinDirectory(current));
            return handles;
        }
        catch { handles.Dispose(); throw; }
    }

    private sealed class HandleGroup : IDisposable
    {
        public List<IDisposable> Items { get; } = [];
        public void Dispose() { foreach (var item in Items.AsEnumerable().Reverse()) item.Dispose(); Items.Clear(); }
    }

    private static void ValidateTree(string directory, string permittedJunction)
    {
        using var pin = JunctionService.PinDirectory(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (PathSafety.Same(entry, permittedJunction)) continue;
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Delete aborted: unexpected reparse point: {entry}");
            if ((attributes & FileAttributes.Directory) != 0) ValidateTree(entry, permittedJunction);
        }
    }

    private static void DeleteContents(string directory)
    {
        using var pin = JunctionService.PinDirectory(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Delete stopped: filesystem changed during deletion.");
            if ((attributes & FileAttributes.Directory) != 0)
            { DeleteContents(entry); Directory.Delete(entry, false); }
            else File.Delete(entry);
        }
    }
}
