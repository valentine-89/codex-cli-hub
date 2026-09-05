# Changelog

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
