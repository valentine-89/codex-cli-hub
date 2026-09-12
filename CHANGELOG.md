# Changelog

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
