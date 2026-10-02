# Architecture

Codex Account Manager is a portable Windows x64 WinForms application targeting .NET 8.
It uses the official Codex CLI for authentication and quota queries. It is an independent
project, not an OpenAI product. The executable does not bundle the .NET runtime or Codex CLI.

## Components

### MCP and locked-desktop session control (1.11.0)

`--mcp` implements newline-delimited JSON-RPC stdio; `--call` invokes the same validated tool handlers.
Both proxy to a root/user-specific named pipe with `PipeOptions.CurrentUserOnly`, without HTTP listeners
or desktop input. The GUI marshals operations to its message loop; `--automation-host` owns `manager.lock`
when the GUI is closed. GUI reopening transfers this lock without stopping independent session workers.

Each `--session-host` worker maintains an isolated native `codex app-server --listen stdio://` process,
initializes/resumes a stored thread, starts/steers turns and waits for interruption acknowledgement.
Workers survive MCP/UI disconnection. Pending server requests require explicit replies; diagnostics are
drained without retaining raw output. Bounded state stores recent event kinds and latest assistant text.

Atomic session records validate IDs/paths and PID creation time/executable. Prior interactive launches
are imported only by verified portable-Manager parent ownership or an exact generated launch script.
Taking control stops that wrapper tree and resumes the same thread. Duplicate active owners are refused.
Switching prepares/checks the target before stopping the source; stale/error quota, missing 5h/weekly
allowance and caller exclusions reject the switch. Account notes and credentials are never projected.
Saved model, effort, approval policy and sandbox mode are restored; unsupported policies fail before
starting a turn. RPC errors are surfaced without a legacy execution fallback. See the current
[Codex App Server protocol](https://learn.chatgpt.com/docs/app-server) and
[MCP stdio transport](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports).

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

`sessions` and `archived_sessions` are shared through NTFS directory junctions. Before each
launch, `sqlite_home` is set to the default Codex home in the profile config and CLI override.
Current Codex metadata, names, recency and paginated history therefore use the same SQLite
databases as Desktop across all windows. Other SQLite stores under this root are shared too.
Authentication, config, input history and the legacy JSONL name index remain profile-local.
Existing private SQLite files are preserved; the explicit maintenance script imports CLI-only
threads and their history without overwriting Desktop records. No whole-home linking or copying.
Deletion verifies paths, junction tag and target, rejects unexpected reparse points, detaches
both links without recursion and deletes only the private tree. It never recursively traverses
the shared sessions directory.

## User operations

- Add: new official CLI login or explicit copy of default `auth.json`, plus name/note.
- Open: a transient `CodexThreadReader` app-server connection lists all pages of interactive,
  non-archived threads from shared SQLite (`useStateDbOnly`, no provider filter). Merge saved Desktop
  workspace roots and normalize Windows device paths, UNC/WSL aliases and verified historical mount paths.
  `WorkspaceCatalogService` separates explicitly projectless Desktop chats and manager chat workspaces;
  a missing folder or null project ID alone never makes a project chat projectless. Desktop state is read only.
  The picker has project/session and projectless tabs, search, refresh, new-chat and exact-ID resume actions.
  Missing projects remain visible; Browse can relocate a selected session for launch without rewriting history.
  CLI receives an explicit `--cd` for exact-ID resume. New projectless chats use the Desktop default layout
  `<UserProfile>/Documents/Codex/YYYY-MM-DD/new-chat[-N]` with `work` and `outputs`. Atomic directory
  creation avoids collisions with Desktop or other launches. The Desktop 26.924 cwd classifier recognizes
  these paths without a projectless-ID entry or modifying its in-memory/global state. Its historical dated
  layout is recognized too. Explicit project assignments take precedence. Existing chat workspaces are reused.
  Legacy `manager-chats` remains discoverable; the explicit offline Python repair registers old chat IDs/path
  hints after checking Desktop is closed, with a backup and concurrent-change checks. No history or file moves.
  API failure falls back visibly to bounded first-record JSONL metadata, without following nested links.
  Opening a project permits ordinary UNC paths and directory links; destructive profile operations retain
  their original strict local-drive and no-reparse validation.
- Login: prefer PowerShell 7; authorized Windows PowerShell 5.1 fallback uses UTF-8 setup.
  Successful login exits its terminal; failed login leaves the terminal available.
- Refresh: a transient native Codex app-server subprocess reads `account/rateLimits/read`
  over stdio. Every returned quota bucket/window is represented. Errors preserve a stale
  snapshot instead of inventing zero usage. No model turn or reset is requested. Manual requests
  run dependency detection, profile validation and status/quota reads off the UI thread. A per-account
  in-flight set permits concurrent requests for different accounts while excluding automatic refresh
  and Warm up for the same account. Results are committed and saved on the UI thread.
  Only affected card content is updated; the card and its action buttons retain their identities.
  Card heights are chosen when building the list and stay fixed during refresh; extra quota/error
  rows scroll inside a bounded quota viewport. Focus leaves a busy card for the list before disabling
  it. Scoped ScrollToControl suppression preserves the current viewport during synchronous updates,
  and pointer focus cannot scroll buttons under the mouse. Keyboard focus still reveals off-screen
  controls, and user scrolling during a request is preserved at completion. Closing cancels the
  bounded refresh subprocesses; failures update the card without a modal error dialog.
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
