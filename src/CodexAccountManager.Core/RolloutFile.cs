using ZstdSharp;

namespace CodexAccountManager.Core;

// Codex keeps the logical .jsonl path in SQLite after compressing idle rollouts.
// Decode in memory; never expand or replace a user's transcript on disk.
internal static class RolloutFile
{
    internal static string? Resolve(string logicalPath)
    {
        if (File.Exists(logicalPath)) return logicalPath;
        if (logicalPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) && File.Exists(logicalPath + ".zst"))
            return logicalPath + ".zst";
        return null;
    }

    internal static Stream OpenRead(string path, bool lockFile = false)
    {
        var physical = Resolve(path) ?? path;
        PathSafety.NoReparseAncestors(physical);
        var file = new FileStream(physical, FileMode.Open, FileAccess.Read,
            lockFile ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete);
        try { return physical.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ? new CheckedZstdStream(file) : file; }
        catch { file.Dispose(); throw; }
    }

    private sealed class CheckedZstdStream : DecompressionStream
    {
        internal CheckedZstdStream(Stream file) : base(file, leaveOpen: false)
            => SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, 27);
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            try { return base.Read(buffer); }
            catch (ZstdException) { throw new IOException("Compressed session data is invalid or unsupported."); }
        }
    }
}
