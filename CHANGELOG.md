# Changelog

## 1.11.0 — 2026-10-03

- Add 15 stdio MCP tools and a current-user named pipe; auto-start a headless Manager when the GUI is closed. No desktop input or unlock required.
- Persist Manager terminal identities and add independent Codex CLI app-server workers for resume, prompts, steering, explicit approvals, interruption and stop.
- Transfer existing terminals explicitly or switch accounts for the same stored thread after quota/exclusion checks. Preserve saved model, reasoning effort and permission policy; refuse duplicate owners.
- Keep notes/auth outside MCP results. No reset-credit, deletion, login or global-auth replacement tools.

## 1.10.4 — 2026-10-01

- Keep account cards and action buttons in place during manual/automatic Refresh and Warm up. Update content without replacing cards or changing their outer heights.
- Prevent focus-driven scrolling while disabling a busy card and during mouse clicks; retain keyboard navigation and scrolling while requests run.
- Show extra quota/error rows in a scrollable quota area so new results do not move other accounts.

## 1.10.3 — 2026-09-27

- Fix the Codex interactive terminal warning when the manager inherits `TERM=dumb` from its parent. Set `xterm-256color` inside newly opened CLI/login/resume consoles, preserving other terminal settings and the parent environment.

## 1.10.2 — 2026-09-27

- Fix new projectless chats using manager-only folders that Desktop did not recognize. Use Desktop's dated Documents/Codex layout with collision-safe names, work and outputs directories.
- Match Desktop's cwd classification for CLI-created projectless chats without changing its live global state. Preserve explicit project assignments and existing chat workspaces.
- Add an offline repair for 1.10.0/1.10.1 projectless chat membership with backup and Desktop-running checks; preserve working files, cwd and conversation history.

## 1.10.1 — 2026-09-27

- Manual Refresh locks only its account card. Other accounts can refresh concurrently, and the account list remains scrollable.
- Manual/automatic refresh and Warm up update only the affected card, preserving other controls and the scroll position. Card heights remain aligned when quota rows change.
- Prevent overlapping operations on the same account, cancel refresh subprocesses on close, and retain cached quota on failure without blocking the UI with a dialog.

## 1.10.0 — 2026-09-27

- Discover current CLI/Desktop sessions through paginated app-server `thread/list` and merge Desktop workspace roots. Keep a visible legacy JSONL fallback for older/unavailable APIs.
- Split Open CLI into Projects and Chats without project, with title search, refresh, new chat and exact-session resume. Projectless chats no longer appear as project folders.
- Support UNC/WSL shares, normalize WSL host aliases and repair historical mount paths only when the real destination exists. Browse can resume a missing-folder session in its new location.
- Start projectless chats in separate persistent workspaces; resume existing projectless chats even when their former workspace is gone. Preserve account isolation and shared session data.

## 1.9.0 — 2026-09-16

- Share Desktop SQLite session metadata and paginated history across every account window while keeping file credentials private. Apply the setting to existing profiles before launch and pass an explicit CLI override.
- Share archived sessions too; account deletion validates and detaches both junctions without traversing shared data.
- Normalize Windows device prefixes and existing WSL drive mounts in project discovery. Label junction status accurately as session-file linking.
- Add an explicit dry-run maintenance script for importing CLI-only history and repairing nonexistent `C:\mnt\<drive>` working directories with verified Windows destinations. Preserve original databases and conversation files.

## 1.8.0 — 2026-09-12

- Add a per-card Warm up button: send `reply "OK"` with `codex exec --ephemeral` in the background without saving session files.
- Use an empty temporary working directory, ignore user config/rules, and request read-only execution. No automatic retries; cancel on close and time out after 90 seconds.

## 1.7.0 — 2026-09-12

- Compact account cards: name and ID share a row; login/plan and shared sessions share a row; note and update time share a row.
- Reduce row spacing and padding while preserving full quota rows, equal card heights, tooltips and all actions.

## 1.6.0 — 2026-09-12

- Run automatic quota refresh in the background without locking the whole interface, one account at a time, with a two-second delay after startup and between completed attempts.
- Reuse detected CLI dependencies and avoid scanning/writing every profile config on startup.
- Add Use reset credit to each card's menu with account-specific confirmation and persistent idempotency keys for uncertain retries.
- Cancel background quota work when the manager closes.

## 1.5.1 — 2026-09-12

- Wait 60 seconds after the quota reset timestamp before the one-time automatic Refresh to allow for server delay.

## 1.5.0 — 2026-09-12

- Golden cards for weekly-only accounts; gray cards at 10% or less in either five-hour or weekly quota, with pale gold for weekly-only accounts.
- Equal card heights and plan type on the existing login-status line when returned by Codex.
- Rename Open to Open CLI and Resume to Resume in CLI.
- Automatically refresh once per known reset timestamp while the app is open; persist attempts across restarts and retain manual retry on failure.

## 1.4.0 — 2026-09-06

- Switch all app labels, menus, dialogs, errors and quota text to English.
- Render previously cached quota labels in English without changing stored account names or notes.

## 1.3.0 — 2026-09-05

- Edit account name and note through the Details dialog.
- Close login terminals automatically on successful login; retain them after failure.
- Sort project folders by repo name, then full path; missing folders appear last.
- Publish source and a clean framework-dependent Windows x64 portable package.

## 1.2.0 — 2026-09-05

- Display every quota window/bucket returned by the official app-server, including weekly-only accounts.
- Apply a managed login to the main Codex home after confirmation.
- Enforce file credential storage in profile TOML and CLI arguments.
- Prefer PowerShell 7, with UTF-8 setup for the authorized PowerShell 5.1 fallback.
- Add targeted cleanup of generated files without following junctions.

## 1.1.0 — 2026-09-05

- Lightweight portable executable without an embedded .NET runtime.
- Branded account cards, separate account/settings forms and optional default-account import.
- Copy original TOML settings during account creation.
- Select a project folder discovered from original sessions before Open.

## 1.0.0 — 2026-09-05

- Initial profile management with isolated Codex homes and shared-session NTFS junctions.
- Atomic metadata storage, safe account deletion and Windows integration tests.
