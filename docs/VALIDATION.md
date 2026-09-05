# Validation — v1.3, 2026-09-05

- Windows x64, SDK 8.0.424, Codex CLI 0.153.4.
- Release build: zero warnings/errors; **34/34 tests passed**.
- Framework-dependent single executable: **295,059 bytes**. Published executable startup: exit 0.
- Details dialog now edits name/note, saves metadata atomically and rolls back in-memory changes on save failure. Dialog rendering inspected.
- Login success exits the shell; failure keeps it open. Exit behavior verified with synthetic CLI scripts on PowerShell 7 and 5.1; real browser login remains manual acceptance.
- Project catalog sorts existing directories by repo name A–Z then full path, with missing directories last. Ordering tested with duplicate repo names.
- PowerShell 7 remains preferred. An actual Windows PowerShell 5.1 subprocess passed UTF-8 native stdin/stdout testing with Vietnamese/Japanese/Unicode text.
- Earlier v1.2 verification of official app-server `account/rateLimits/read` against the user's managed Codex1 profile:
  weekly remaining 49%, reset 2026-09-12 15:38 Asia/Bangkok; no 5-hour window returned; credits 0; reset count 0.
  Snapshot saved in portable accounts.json. No reset consumed and no thread/model turn created.
- The actual managed profile config was verified to contain `cli_auth_credentials_store = "file"`.
- Quota tests cover weekly-only, multiple buckets, credits, spend limits, reset counts and missing metrics.
- Apply tests use synthetic credentials only: successful replacement, invalid JSON rejection and locked-target preservation.
  Default config and shared session sentinel remain unchanged. No real default login was replaced during implementation.
- Existing junction, path traversal, metadata, config-copy, credential isolation and project catalog tests pass.
- Cards with weekly-only/multiple quotas and Apply buttons rendered and visually inspected.
- Repository cleanup removed generated files from explicit bin/obj/artifacts targets without junction traversal.
  Portable executable, real account/profile, config, auth, shared sessions and Git/source retained.

## Remaining acceptance
Real interactive Apply requires the user to close Codex App/main-account terminals, confirm Apply,
then reopen Codex. It was intentionally not performed on the actual default account during tests.
No Git remote configured; push awaits repository URL. Portable deployment is local.

## Executable SHA-256
```text
53D0157520EB9EBAA4B2E28EA858B532731FFD885504B82839A978A849B541C4
```

Protocol source: https://learn.chatgpt.com/docs/app-server
