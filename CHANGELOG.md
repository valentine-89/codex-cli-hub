# Changelog

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
