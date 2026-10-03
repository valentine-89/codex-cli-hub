# Validation — v1.11.2, 2026-10-03

- **65/65 .NET tests passed**; Release build completed with zero warnings/errors.
- Reproduced the reported launch race with 20 hidden PowerShell ShellExecute launches: 15 returned
  a null `MainModule` immediately after reading `StartTime`, matching the former null dereference.
- New deterministic tests create a hidden, suspended child before loader initialization, where module
  enumeration is unavailable (null or Windows error 299). Terminal registration, liveness and verified
  stop succeed through the kernel image query. No real conversation or account request is submitted.
- Managed-worker registration preserves its preallocated registry ID and thread. An exited child is
  rejected without saving a live session. Wrong executable paths and changed creation times still
  reject stopping the process. Existing profile, UI, quoting, MCP and session tests remain passing.
- Physical user interaction with a newly created real chat remains separate from these synthetic tests.

## Previous validation — v1.11.1, 2026-10-03
- 62 regression cases cover stale managed snapshots versus bounded complete rollout records, quota errors, process creation-time ownership, native Unicode/multiline/shell-character prompt arguments, and retained model/effort/approval/sandbox flags.
- MCP inventory now exposes 16 tools, including session_open_terminal. No reset/credits/auth mutation is exposed.
- Live release checks verify the same data root, stdio handshake, session identity and visible native terminal; no new design prompt is submitted to the completed medication-dispenser job.
- Keyboard entry in the interactive terminal and visibility after unlocking are manual acceptance. Locked-desktop operation depends on the computer remaining awake.

## Previous validation — v1.11.0, 2026-10-03

- **60/60 .NET tests passed**, including permission restoration, persistent managed CLI protocol,
  pending approvals, steering, interrupt, stop, schema validation, quota eligibility and process identity.
- Installed official Codex CLI **0.160.0**, PowerShell 7.6.6 and .NET SDK 8.0.424 used for validation.
  Generated CLI schemas verified the separate resume sandbox enum and turn sandbox policy object.
- Live stdio MCP handshake exposed 15 tools. Account projection excluded notes, credential paths and
  tokens; no reset-credit, credential or deletion tool is exposed. MCP EOF exited the client promptly
  while the separate background bridge continued running without an open Manager window.
- A scratch thread completed a real text-only turn after its client disconnected. Switching the idle
  scratch session between two isolated accounts retained its exact Codex thread UUID and working
  directory, restored its saved model/permission context and completed another turn with `OK`.
  The scratch worker was stopped afterward; no test prompt edited project files or used tools.
- The bridge discovered the pre-existing medication-dispenser terminal after its Manager UI had
  closed and read its persisted status without interrupting that job or starting a duplicate.
- Published portable executable version 1.11.0 passed the same live stdio handshake and EOF test.
  Registered `codex_account_manager` in the default Codex MCP configuration; the bridge can also
  be called by `scripts/call-mcp.ps1` before an existing connection reloads that configuration.
- These checks use pipes and independent processes and require no desktop input. Windows lock is
  outside their control path; machine sleep/shutdown prevents execution until the machine runs again.
  Interactive WinForms ownership takeover remains a separate UI acceptance step.

## Previous validation — v1.10.4, 2026-10-01

- **52/52 .NET tests passed** on the final source; Release publish completed without warnings/errors.
- Reproduced the regression on the prior implementation: adding the cached-quota error row moved
  account bounds even though the numeric list scroll position was restored.
- Expanded the real WinForms message-loop test with focused Refresh buttons and both one/two-column
  layouts (DPI-scaled dimensions asserted), at the top, middle and bottom of the list.
- Verified unchanged scroll offsets, card bounds, refreshed-card/button identities, and other controls
  at request start/completion. Scrolling while a request is pending remains effective at completion.
- Covered success, failure with cached quota, and quota growth/shrinkage from one to five rows.
  Overflow rows remain accessible through the inner scrollbar without moving the account list.
- Verified keyboard focus still reveals off-screen accounts; concurrent-account isolation, duplicate
  prevention, saved results and cancellation on close remain covered. No real account refresh was sent.
- Single-file Release publish succeeded; the staged 1.10.4 executable passed its startup smoke test.

## Previous validation — v1.10.3, 2026-09-27

- **52/52 .NET tests passed**; Release build and single-file publish succeeded with zero warnings/errors.
- Actual PowerShell 7 and Windows PowerShell 5.1 subprocesses verified Open, Resume and Login with
  inherited `dumb`/`DUMB`, existing `xterm-256color`/`vt100`, and unset TERM (30 combinations).
  A synthetic CLI observed the corrected terminal type before invocation; valid/unset values and
  the parent environment were preserved. Real interactive Codex rendering remains user acceptance.

## Previous validation — v1.10.2, 2026-09-27

- **51/51 .NET tests and 2/2 projectless-repair tests passed**; Release single-file publish succeeded.
- Inspected installed Desktop 26.924.2738.0: its projectless workspace creator uses Documents/Codex,
  local YYYY-MM-DD, new-chat with numeric collision suffixes, and work/outputs subdirectories.
- Executed the installed Desktop's extracted cwd-classification function against the new format:
  regular and Windows device-prefixed paths are recognized; the former manager-chats path is not.
  The sidebar includes local conversations classified as projectless. No real test model turn was sent.
- Tests cover collisions without overwriting existing files, historical/current folder classification,
  preservation of existing workspaces, nested-directory rejection and reparse ancestor rejection.
- Offline repair dry-run identified the one affected local thread. Synthetic apply tests verify backups,
  idempotence, refusal while Desktop is running and preservation of unrelated state, working files and SQLite.
- After the user closed Desktop, applied the offline repair to thread `01a0df23-399a-75b2-98a3-a6ba5628c31f`.
  Verified only its projectless membership and path hints changed, with an exact pre-change backup.
  All 18 working files, the rollout hash and the complete SQLite thread row remained unchanged;
  a repeated dry-run returned no outstanding repairs. Desktop can now reload the saved membership.
  Actual new-chat appearance in the running Desktop UI remains user acceptance; only
  the installed Desktop classifier and shared session discovery have been verified here.

## Previous validation — v1.10.1, 2026-09-27

- **50/50 .NET tests passed**, including a real WinForms message-loop test with eight synthetic accounts.
- Started two manual refreshes concurrently; only their own cards were disabled, while the list and
  header remained enabled. Repeated clicks did not start duplicate requests for the same account.
- Completing one refresh preserved the other account's in-flight lock, other cards/control identities,
  and a nonzero scroll position. The successful result was persisted to the account document.
- Simulated failure retained cached quota and unlocked only the failed account. Updated quota/error
  rows kept card heights aligned without rebuilding unaffected controls.
- Closing during a pending refresh canceled the injected reader and disposed the form successfully.
- Existing picker, launch quoting, profile isolation, quota, reset queue and Warm up tests remain passing.

## Previous validation — v1.10.0, 2026-09-27

- Release build/publish succeeded with zero warnings/errors; **49/49 .NET tests passed**.
- New coverage: shared-store thread pagination and deduplication, repeated-cursor rejection, visible legacy
  fallback, cancellation, explicit projectless classification and assignment precedence, missing folders,
  metadata with cwd after 300 KiB of instructions, Windows/UNC/WSL path normalization, separate chat
  workspaces, exact resume ID and cwd quoting, and projectless picker selection.
- Live read through installed Codex CLI **0.157.1** found **30 distinct folders, 70 active interactive
  sessions and 2 projectless chats** in 338 ms. `D:\VSYS\codex-cli-hub` was present and accessible.
  WSL share aliases were deduplicated. No model turn or real conversation was started for this check.
- Both picker tabs rendered and inspected; synthetic UI interaction verified projectless resume selection.
  Interactive real-account resume/new-chat remains an acceptance step for the user.

## Previous validation — v1.9.0, 2026-09-16

- Release build: zero warnings/errors; 44/44 .NET tests and 2/2 Python maintenance tests passed.
- Synthetic tests cover existing-profile preparation, preserving account credentials and unrelated TOML,
  archive-junction conflict rejection, safe account deletion, shell override quoting, and project path normalization.
- Maintenance tests cover dry-run, idempotent import of paginated history, preservation of newer Desktop
  records, and metadata cwd repair with unchanged JSONL byte offsets and unchanged conversation messages.
- Two concurrent official CLI 0.154.0 app-server processes, using two real profile configs without a test
  sqlite override, returned the same newest PVoil Desktop task and names through `thread/list`.
- Repaired the old PVoil cwd in SQLite and three rollout metadata fields. Both processes listed the repaired
  task under the real Windows directory and read its four persisted turns through `thread/turns/list`.
- Prepared all seven existing profiles and imported nine CLI-only threads with their paginated history.
  No model turn was sent, no account credentials changed, and original private databases were retained.
- WinForms fixture rendering inspected. CLI acceptance used the picker backend; no interactive model
  conversation was started. Existing terminals must be reopened to load the shared-store configuration.

## Previous validation — v1.8.0

- Windows x64, SDK 8.0.424, Codex CLI 0.153.4.
- Release build: zero warnings/errors; **41/41 tests passed**.
- Framework-dependent single executable: **319,123 bytes**. Published executable startup: exit 0.
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

- Equal-height golden/gray cards with inline plan labels rendered and inspected. Reset scheduling tested at reset +59/+60 seconds, after persisted retry suppression, and at the next reset. No real reset was consumed.

- Queue tests verify startup delay, single-flight execution and two-second spacing after completion. Reset protocol tests use a synthetic app-server and persisted keys, covering every outcome and uncertain responses. No real reset credit was used. Real account redemption requires user acceptance.

- Compact two-column metadata layout visually inspected; three-quota-row cards reduced from 283 to 206 pixels (about 27%). Full quota rows and equal card heights preserved.

- Warm up tested using a synthetic CLI; ephemeral flag, prompt, profile environment and unchanged session directory verified. Button layout inspected. CLI 0.154.0 help verifies required flags. No real model request performed; server quota-window activation remains live acceptance.

## Remaining acceptance
Real interactive Apply requires the user to close Codex App/main-account terminals, confirm Apply,
then reopen Codex. It was intentionally not performed on the actual default account during tests.
Public source and release: https://github.com/valentine-89/codex-cli-hub
Release ZIP contains only the executable; local profiles and account metadata are excluded.

## Current executable SHA-256
```text
979CCCACEE052DF4300CA50B95964735E5EC2B2AE2AB3FD1FE8537B634BB57F4
```

Protocol source: https://learn.chatgpt.com/docs/app-server
