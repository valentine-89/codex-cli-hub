using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Deterministically hold a test-owned child before loader initialization. No
// window, credentials, CLI request, elevation or timing-dependent sleep needed.
sealed class SuspendedProcess : IDisposable
{
    public Process Process { get; }
    public string Executable { get; } = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public SuspendedProcess(string cwd)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(Executable, new StringBuilder($"\"{Executable}\" /c exit 0"),
            IntPtr.Zero, IntPtr.Zero, false, 0x08000004, IntPtr.Zero, cwd, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try { Process = Process.GetProcessById(info.ProcessId); }
        finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
    }
    public void Dispose()
    {
        try
        {
            if (!Process.HasExited) Process.Kill();
            if (!Process.WaitForExit(10000)) throw new IOException("Test child did not exit.");
        }
        finally { Process.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
