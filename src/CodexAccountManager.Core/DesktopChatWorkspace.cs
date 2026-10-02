using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CodexAccountManager.Core;

public static class DesktopChatWorkspace
{
    // Codex Desktop 26.924 recognizes both these historical and current cwd layouts
    // even when the CLI-created thread has no entry in projectless-thread-ids.
    private static readonly Regex DesktopLayout = new(
        @"^(.*(?:^|[\\/])Documents[\\/]+Codex)[\\/]+(?:\d{4}-\d{2}-\d{2}-[a-z0-9][a-z0-9-]*|\d{4}-\d{2}-\d{2}[\\/]+[a-z0-9][a-z0-9-]*)[\\/]*$",
        RegexOptions.CultureInvariant);

    public static string? RecognizedRoot(string? cwd)
    {
        if (cwd is null) return null;
        var match = DesktopLayout.Match(cwd.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string Root(string? userHome = null) => Path.Combine(
        userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Codex");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    public static string Create(string? userHome = null, DateTime? localDate = null)
    {
        var root = PathSafety.Canonical(Root(userHome));
        var dateDirectory = Path.Combine(root, (localDate ?? DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        PathSafety.NoReparseAncestors(dateDirectory);
        Directory.CreateDirectory(dateDirectory);
        using var pin = JunctionService.PinDirectory(dateDirectory, allowWrites: true);
        for (var i = 0; i < 105; i++)
        {
            var name = i == 0 ? "new-chat" : i < 100 ? "new-chat-" + (i + 1).ToString(CultureInfo.InvariantCulture)
                : "new-chat-" + Guid.NewGuid().ToString("D");
            var workspace = Path.Combine(dateDirectory, name);
            // Unlike Directory.CreateDirectory, this atomically fails if Desktop/another launch took the name.
            if (!CreateDirectoryW(workspace, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 183 or 80) continue;
                throw new IOException("Cannot create the Codex chat workspace.", new Win32Exception(error));
            }
            using var workspacePin = JunctionService.PinDirectory(workspace, allowWrites: true);
            Directory.CreateDirectory(Path.Combine(workspace, "work"));
            Directory.CreateDirectory(Path.Combine(workspace, "outputs"));
            return workspace;
        }
        throw new IOException("Cannot create a unique Codex chat workspace.");
    }
}
