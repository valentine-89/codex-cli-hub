# Architecture

Codex Account Manager is a portable Windows x64 WinForms application targeting .NET 8.
It uses the official Codex CLI for authentication and quota queries. It is an independent
project, not an OpenAI product. The executable does not bundle the .NET runtime or Codex CLI.

## Components

- `src/CodexAccountManager`: account cards, add/details/settings/project dialogs.
- `src/CodexAccountManager.Core`: metadata persistence, profile lifecycle, NTFS junctions,
  isolated CLI processes, quota parsing and session project discovery.
- `tests/CodexAccountManager.Tests`: executable Windows integration tests with synthetic profiles.
- `scripts`: build, publish, package and targeted cleanup.

## Storage and isolation

The executable directory is the portable root. `accounts.json` contains account metadata
and quota snapshots; `settings.json` contains local paths. Both use atomic replacement.
A file lock prevents two managers from writing the same portable root.

Each account has a GUID directory under `profiles`. Creation copies the default TOML settings
and enforces root `cli_auth_credentials_store = "file"`. Other settings remain intact.
Authentication is separate unless the user explicitly copies the default login during Add.
The CLI receives a profile-specific `CODEX_HOME` and a file credential override. Known inherited
credential and remote-attachment environment variables are removed from the child process.

Only `sessions` is shared through an NTFS directory junction. Database/index/history state is
not shared; identical session files do not guarantee identical resume pickers across clients.
Deletion verifies paths, junction tag and target, rejects unexpected reparse points, detaches
the link without recursion and deletes only the private tree. It never recursively traverses
the shared sessions directory.

## User operations

- Add: new official CLI login or explicit copy of default `auth.json`, plus name/note.
- Open: discover metadata `cwd` values from original sessions, sort repo names A–Z and select
  a working directory. Missing directories are listed last and cannot be opened.
- Login: prefer PowerShell 7; authorized Windows PowerShell 5.1 fallback uses UTF-8 setup.
  Successful login exits its terminal; failed login leaves the terminal available.
- Refresh: a transient native Codex app-server subprocess reads `account/rateLimits/read`
  over stdio. Every returned quota bucket/window is represented. Errors preserve a stale
  snapshot instead of inventing zero usage. No model turn or reset is requested.
- Apply: after confirmation to close the main Codex clients, atomically replace only the
  configured default home's `auth.json`. The manager does not terminate Codex processes.
- Details: edit name/note without changing the account ID, profile or authentication.
- Automatic Refresh: one background request at a time, two seconds after startup and after each
  completion. Only the active card is disabled; dependency detection is reused. Closing cancels the request.
- Use reset credit: explicit per-account confirmation invokes the official consume method in that
  profile's isolated app-server. Persist the idempotency key before sending; uncertain retries reuse
  it across restarts. This operation never purchases credits or runs from the automatic timer.

## Distribution boundary

Source control excludes all runtime account data. Release ZIPs use an explicit allowlist:
exactly `CodexAccountManager.exe`. Public documentation remains on GitHub. Never zip a used portable directory wholesale.
The .NET apphost provides a runtime download dialog when the required desktop runtime is absent.
