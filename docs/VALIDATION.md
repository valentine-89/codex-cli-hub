# Validation — v1.12.4, 2026-10-26

- **93/93 .NET regression tests and 4/4 Python maintenance tests passed.** Release build and
  single-file publish completed without warnings/errors. Packaged MCP keeps the same 16 tools,
  excludes private account fields and exits on EOF. Isolated GUI startup/render/exit passed.
- Transport regressions cover saturation at ten connections, subsequent requests, recovery before
  delivery, application errors and lost replies after delivery. Delivered operations are never replayed.
- Two packaged MCP clients initialize without a host, concurrently start one host, and recover to
  one replacement host after only their isolated fixture host exits. Both clients remain connected.
- Installed **CLI 0.164.0** and **Desktop backend 0.164.0-alpha.11.1** passed all four schema-only
  integration combinations (each binary, plain/compressed rollouts): cross-profile resume, idle
  guards, missing-list/archive reproduction, repair, profile deletion, archive/unarchive, transcript
  hashes and idempotence. No account authentication or model turn was used.
- Read-only live audit found migration **61**, successful migrations and `quick_check=ok`:
  176 threads, 11 profile aliases, six broken aliases and zero other validation issues. The six
  broken references remain pending offline repair while Codex clients are active. Current catalog
  discovery returned 37 projects, 106 visible sessions and five projectless sessions without warning.
- The manual repair helper passed syntax validation and refused to start maintenance while live
  clients were running. No real shared database repair, account deletion or credential change occurred.
- Windows incident logs show low-memory/resource errors followed by desktop heap exhaustion and
  a forced restart. They do not establish Manager as the cause; bridge fixes are not a verified
  remedy for the whole-machine crash. No Windows memory settings were changed.

## Previous validation — v1.12.3, 2026-10-18

- **89/89 .NET regression tests and 4/4 Python maintenance tests passed.** Release build and
  single-file publish completed without warnings/errors. Packaged MCP reports 1.12.3 with the same
  16 tools, excludes private account fields and exits on EOF. Isolated GUI startup/render/exit passed.
- Reproduced the reported failure on the installed Windows CLI: SQLite retained `/home/...` cwd
  while `thread/list` surfaced `D:\home\...`. The old catalog showed 40 projects, including three
  nonexistent local-drive duplicates. The corrected catalog shows 37 projects and the same 98 sessions
  (five projectless), with all eight affected sessions grouped under their existing WSL directories.
- Read-only live validation confirmed those three WSL directories exist. Original SQLite cwd values
  and SHA-256 hashes of all eight affected transcripts remained unchanged across final validation.
  No real model turn, thread resume, account mutation or live database repair was performed.
- New regressions cover exact ID/projection recovery without database writes, genuine Windows cwd
  preservation, concurrent cwd mismatch, Linux path case, ambiguous distros, nested roots, explicit
  project assignments, unresolved paths, pagination, grouping and the UNC path passed to resume.
  The SQLite fixture now includes the real `cwd` column used by read-only discovery.
- Native checks cover the packaged main window and catalog/directory discovery. An interactive
  resume into WSL remains a separate user action; no existing terminal was stopped to test this fix.

## Previous validation — v1.12.2, 2026-10-17

- **84/84 .NET regression tests and 4/4 Python maintenance tests passed.** After adding handling for
  private read-only plugin cache files, all **19/19 session-store tests** passed again. Release build
  and single-file publish completed with zero warnings/errors. Packaged MCP reports 1.12.2 with the
  unchanged 16 tools, excludes private account fields and exits on EOF. Packaged GUI startup/render/exit passed.
- Installed **CLI 0.163.0** and **Desktop backend 0.163.0-alpha.10.2** passed all four schema-only
  integration combinations (each binary, plain/compressed rollouts). Verified cross-profile resume,
  production repair/deletion idle guards, exact deletion-name validation, legacy missing-list/archive
  reproduction, path repair, account metadata removal, shared transcript hashes, archive/unarchive
  and idempotence. No real model turn or account authentication was used.
- Live read-only inspection confirmed state migration **61**, successful migrations and SQLite
  `quick_check=ok`. Migration 60 adds `guardian_review_feedback`; 61 adds `thread_read_receipts`.
  Required thread path columns and existing triggers are unchanged. The former strict migration-59
  check caused the reported deletion error. Current audit reports the actual version and still rejects
  unknown schemas, failed migrations, incompatible columns/triggers and migration changes since audit.
- Current live audit: **163 threads, 12 profile-path aliases, zero validation issues**. The requested
  account has two aliases (one plain, one compressed), with matching shared transcript IDs. Actual
  account deletion and path writes remain deferred until Codex/Desktop/Manager clients close.
- The first current-CLI fixture exposed read-only Git pack files created by plugin initialization.
  Profile deletion now clears only the private file's read-only flag after shared junctions are
  detached. A regression verifies private cache deletion while shared bytes/attributes and other
  accounts/credentials remain intact. Integration fixtures disable plugin discovery to avoid unrelated
  network downloads; cleanup removes explicitly inventoried files and then empty directories.
- The optional deferred deletion requires both exact account ID and expected name, uses the normal
  filesystem/process/schema checks and an exclusive Manager lock, and has no MCP deletion tool.

## Previous validation — v1.12.1, 2026-10-14

- **80/80 .NET tests and 4/4 Python maintenance tests passed.** Release single-file publish completed
  without warnings/errors. Packaged MCP handshake reports 1.12.1 with the same 16 tools, excludes
  private fields and exits on EOF. Only the test-owned synthetic bridge was stopped.
- Both installed binaries, **CLI 0.162.1** and **Desktop backend 0.162.0-alpha.17.2**, passed the full
  schema-only integration scenario with plain and compressed rollouts (four combinations). This
  covers cross-profile read/resume, missing-list/archive-error reproduction, production idle guard,
  repair, safe profile deletion, Manager discovery, archive/unarchive and idempotence. No model turn
  or account authentication was used. Compressed transcript hashes remained unchanged by repair.
- Added regression coverage for compressed active/archived files, incorrect metadata IDs, invalid
  frames, compression between audit and repair, preserved context/status, preference for a live plain
  file and a bounded tail that discards partial records. Existing rollback/schema/path checks still pass.
- Read-only live inspection found **156 threads, 129 compressed transcripts and 27 plain transcripts**.
  SQLite still stores logical `.jsonl` paths for compressed `.jsonl.zst` files. Version 1.12.0 incorrectly
  reported those 129 files missing. Version 1.12.1 audit reports **12 profile aliases, 6 broken aliases,
  zero validation issues**; all 12 canonical compressed files have matching metadata IDs. A user resume
  normalized one of the previous 13 aliases. State migration remains 59 and history migration 8;
  both SQLite `quick_check` results are `ok`, with no failed migration found.
- The installed 1.11.2 GUI opened successfully. The user confirmed closing the window manually after
  Resume, so an automatic exit/startup crash has **not** been reproduced. Isolated 1.11.2/1.12.0 startup
  and headless-host handoff checks passed. The packaged 1.12.1 GUI also passed isolated startup,
  rendering and automatic close; its startup log identifies version 1.12.1. Sanitized startup failure
  logging is available for further diagnosis.
- **Live path repair remains pending while Codex/Manager clients run.** The prior 1.12.0 helper expired
  after its 24-hour wait without modifying the database. The 1.12.1 helper verifies physical compressed
  file hashes and waits for clients to close before repair. Portable executable promotion can proceed
  separately once the old GUI executable is released. No real conversation was archived for testing.

Run the integration command below once without and once with `--compressed` (Python 3.14 is required
for the compressed fixture generator). Decompression in the shipped Manager uses managed C# code,
does not require Python and does not expand or replace transcript files on disk.

## Previous validation — v1.12.0, 2026-10-13

- **75/75 .NET tests and 4/4 existing Python maintenance tests passed.** Release build and
  single-file publish completed without warnings/errors. Packaged MCP handshake exposes the same
  16 tools, reports 1.12.0, excludes private fields and exits on EOF; only its synthetic bridge was stopped.
- Synthetic NTFS/SQLite tests cover deleted profiles, archive paths, matching metadata IDs, missing
  files, incorrect IDs, paths outside the shared store, nested links, device-prefixed aliases, unchanged
  canonical paths, busy databases, concurrent changes, rollback, unknown schema/triggers and idempotence.
  Non-path columns, JSONL bytes and history sentinels remain unchanged. Audit does not write the database.
- Installed **CLI 0.162.1** and **Desktop backend 0.162.0-alpha.17.2** pass schema-only fixture tests:
  profile B reads/resumes A's synthetic thread without a model turn; the production command blocks
  repair while a client runs. A restored legacy alias plus old-style profile deletion reproduces a
  missing list entry and archive error -32600. Repair restores listing and archive/unarchive, with
  unchanged transcript hash/title and no changes on the second repair. Current resume can canonicalize
  the path itself, so the harness explicitly restores the legacy alias to reproduce never-resumed chats.
- These tests copy schema/migration definitions only, never real thread data or credentials. The
  production process guard stays enabled; an internal test-only hook is constrained to the named
  temporary fixture tree. Fixtures are removed by named-file deletion and empty-directory removal.
- Read-only live audit: **153 threads, 13 profile aliases, 6 broken aliases, zero validation issues**;
  all 13 canonical transcript IDs match. SQLite quick_check was `ok`; no failed migration was found.
  This identifies broken profile references, not evidence of schema corruption.
- **Live repair remains pending while Codex/Manager are running.** The offline update helper waits
  for those clients to exit, then records repair results, unchanged transcript hashes and recovered
  API visibility. Root EXE promotion and old-process replacement are likewise deferred. No real chat
  was archived as a test; archive acceptance above uses a dedicated synthetic thread.

Reproduce binary acceptance with `python -B tests/test-session-store-integration.py --schema-home
<home> --manager <staged-exe> --test-host <Release-test-exe> --codex <CLI-exe> --codex <Desktop-exe>`.
Run it from PowerShell 7 on NTFS; it starts no model turn and does not use account authentication.

## Previous validation — v1.11.2, 2026-10-03

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
- Initial local deployment opened the new GUI and preserved the existing CLI terminal and MCP client.
  All 15 checked account/settings/auth/config files retained their pre-deployment SHA-256 hashes.

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
FA48B33029FE4471655B0314A1245D8F3F288560AFDEC110E0F528B1340F8173
```

Protocol source: https://learn.chatgpt.com/docs/app-server
