# Validation — v1.1, 2026-09-05

- Windows x64; .NET SDK 8.0.424; Codex CLI 0.153.4; PowerShell 7.
- Release build: passed, zero warnings/errors. Integration harness: **24/24 passed**.
- Published single executable: **264,339 bytes** (~258 KiB), framework-dependent .NET 8 Windows Desktop.
- Old .NET native/managed/satellite runtime files removed from portable output. Data files retained.
- Published executable startup on installed .NET 8: exit 0.
- Logo embedded in executable/title bar/header. Cards, Add Account, Advanced Settings and Project Picker rendered and visually inspected.
- Project catalog against original sessions: **19 unique project directories**, 35 skipped entries;
  newest examples included codex-cli-hub, SightCount AI, codex-quota-guard-mcp, PVoil-LED and UlabGuard.
  No original session content modified. Only metadata cwd is parsed; unsupported/unreadable entries are counted.
- Tests verify original TOML copied exactly, auth copying requires explicit opt-in, auth copies are independent,
  source auth survives profile deletion, and missing/locked source auth aborts before profile creation.
- Existing NTFS safety tests pass: correct tag/target, shared sentinel preservation, wrong/missing/broken links,
  normal/nested/ancestor directories, path traversal and directory pinning.
- Session project tests cover deduplication, existence, unrelated record filtering, large metadata and cancellation.
- Official status on empty isolated profile, quoting/environment isolation, metadata locks and malformed JSON pass.
- No actual source auth copied during testing. Auth/config copy tests used test-owned fake files only.

## Limits
Real browser login and simultaneous authenticated account use remain user acceptance steps.
The missing-runtime simulation command was rejected by automatic approval review with `blocked by policy`.
The app uses the standard .NET GUI apphost prerequisite/download behavior and provides an explicit download
link in Advanced Settings; that missing-runtime branch was not executed here. No .NET installation was changed.
No Git remote configured; push awaits repository URL. Portable deployment is local.

## Published executable SHA-256
```text
AD46F79988DC2EFAA730F3A0574A9A14318EEC7AEC318A1E45A9C819E235ED0B
```

Runtime installation documentation: https://learn.microsoft.com/en-us/dotnet/core/runtime-discovery/troubleshoot-app-launch
