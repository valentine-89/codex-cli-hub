using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("CodexAccountManager.Tests")]

namespace CodexAccountManager.Core;

// Windows supplies SQLite. No external executable, NuGet runtime or database copy.
internal sealed class NativeSqlite : IDisposable
{
    private IntPtr db;
    internal NativeSqlite(string path, bool write = false, bool create = false)
    {
        PathSafety.NoReparseAncestors(path);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) PathSafety.NoReparseAncestors(path + suffix);
        var code = sqlite3_open_v2(path, out db, (write ? 2 : 1) | (create ? 4 : 0) | 0x10000, IntPtr.Zero);
        if (code != 0) { Dispose(); throw Failure(code); }
        Check(sqlite3_busy_timeout(db, 1000));
    }

    internal List<string?[]> Query(string sql, params string[] values)
    {
        Check(sqlite3_prepare_v2(db, sql, -1, out var statement, IntPtr.Zero));
        try
        {
            for (var i = 0; i < values.Length; i++)
                Check(sqlite3_bind_text(statement, i + 1, values[i], -1, new IntPtr(-1)));
            var rows = new List<string?[]>();
            int result;
            while ((result = sqlite3_step(statement)) == 100)
            {
                var row = new string?[sqlite3_column_count(statement)];
                for (var i = 0; i < row.Length; i++) row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i));
                rows.Add(row);
            }
            if (result != 101) throw Failure(result);
            return rows;
        }
        finally { sqlite3_finalize(statement); }
    }
    internal int Changes => sqlite3_changes(db);
    private static void Check(int code) { if (code != 0) throw Failure(code); }
    // Never include SQL, database values, credentials or raw native error messages.
    private static IOException Failure(int code) => new($"Session store SQLite error {code}.");
    public void Dispose() { if (db != IntPtr.Zero) { sqlite3_close_v2(db); db = IntPtr.Zero; } }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_changes(IntPtr db);
}
