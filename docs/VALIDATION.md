# Validation — 2026-09-05

- Host: Windows x64; .NET SDK 8.0.424; Codex CLI 0.153.4; PowerShell 7.
- Release build: passed, zero warnings/errors.
- Executable integration harness: **18/18 passed**.
- Real NTFS junction creation/tag/target verified. Shared fixture sentinel content survived deletion.
- Rejected normal/missing/broken/wrong sessions links, nested/ancestor links and traversal.
- A native handle with GENERIC_READ and no delete/write sharing blocked directory rename in the test.
- Official `codex login status` on a newly created test profile: Not logged in. No credentials created.
- Unicode/shell metacharacter quoting and child environment isolation passed.
- Invalid JSON preservation, metadata roundtrip and exclusive manager lock passed.
- WinForms populated/empty layouts rendered and visually inspected. Test records use no real credentials.
- Self-contained win-x64 folder published to `dist/CodexAccountManager` (~144 MiB).
- Published executable startup smoke: exit 0. It detects installed CLI/Pwsh and existing default sessions.
- No real login, logout, default auth/config modification, or shared-session deletion performed.
- No remote configured: local commit only; push awaits repository URL.

## Remaining user acceptance
Browser login for two actual accounts, simultaneous interactive terminals with their separate identities,
and successful resume of a representative Codex App session. Sharing filesystem sessions does not
establish synchronization of each Codex version's private session index/database.

## Published application SHA-256
```text
CodexAccountManager.dll
0EBEED3BA1DAFEC3B93C97755D16408627861021DC87BA92D0EA66F7A0D9AA93
CodexAccountManager.Core.dll
1C48FD305EEF494EAD53F924377CCF3657BF141B870401B2B6AE31BC9B562C68
```

The .exe is the .NET apphost; the DLL hashes identify the application implementation.
