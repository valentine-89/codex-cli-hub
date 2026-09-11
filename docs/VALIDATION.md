# Validation — v1.5, 2026-09-12

- Windows x64, SDK 8.0.424, Codex CLI 0.153.4.
- Release build: zero warnings/errors; **38/38 tests passed**.
- Framework-dependent single executable: **303,251 bytes**. Published executable startup: exit 0.
- Details dialog now edits name/note, saves metadata atomically and rolls back in-memory changes on save failure. Dialog rendering inspected.
- Login success exits the shell; failure keeps it open. Exit behavior verified with synthetic CLI scripts on PowerShell 7 and 5.1; real browser login remains manual acceptance.
- Project catalog sorts existing directories by repo name A–Z then full path, with missing directories last. Ordering tested with duplicate repo names.
- PowerShell 7 remains preferred. An actual Windows PowerShell 5.1 subprocess passed UTF-8 native stdin/stdout testing with Vietnamese/Japanese/Unicode text.
- Earlier v1.2 verification of official app-server `account/rateLimits/read` against a managed weekly-only profile:
  weekly window, credits and reset count returned without an invented 5-hour window.
  Snapshot saved locally. No reset consumed and no thread/model turn created.
- The actual managed profile config was verified to contain `cli_auth_credentials_store = "file"`.
- Quota tests cover weekly-only, multiple buckets, credits, spend limits, reset counts and missing metrics.
- Apply tests use synthetic credentials only: successful replacement, invalid JSON rejection and locked-target preservation.
  Default config and shared session sentinel remain unchanged. No real default login was replaced during implementation.
- Existing junction, path traversal, metadata, config-copy, credential isolation and project catalog tests pass.
- Cards with weekly-only/multiple quotas and Apply buttons rendered and visually inspected.
- Repository cleanup removed generated files from explicit bin/obj/artifacts targets without junction traversal.
  Portable executable, real account/profile, config, auth, shared sessions and Git/source retained.

- English cards and all four dialogs rendered and visually inspected. Cached Vietnamese quota labels render in English without altering account names, notes or stored quota values.

- Equal-height golden/gray cards with inline plan labels rendered and inspected. Reset scheduling tested before/at reset, after persisted retry suppression, and at the next reset. No real reset was consumed.

## Remaining acceptance
Real interactive Apply requires the user to close Codex App/main-account terminals, confirm Apply,
then reopen Codex. It was intentionally not performed on the actual default account during tests.
Public source and release: https://github.com/valentine-89/codex-cli-hub
Release ZIP contains only the executable; local profiles and account metadata are excluded.

## Executable SHA-256
```text
83B988986CA274DAA0DEA011B94C081DC2C661E170053A6F2D8465AA96DE6067
```

Protocol source: https://learn.chatgpt.com/docs/app-server
