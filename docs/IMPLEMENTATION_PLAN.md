# Implementation plan — 2026-09-05

Historical v1.0 plan. The user-requested v1.1 revision supersedes self-contained packaging,
single-form/grid UI and the prohibition on explicitly requested auth copying. Current behavior:
framework-dependent single executable with native missing-runtime download prompt; branded cards;
Add Account and Advanced Settings dialogs; copy source TOML on creation; optionally copy only
the default auth.json via locked streams when the user selects that mode. No source modification.
The path/junction safety and delete rules below remain in force.

## Verified environment
- Empty workspace, no Git remote. SDK 8.0.424 available in D:/VSYS/.dotnet-sdk.
- `codex` resolves through npm codex.ps1; CLI 0.153.4. PowerShell 7 available.
- Inspected `--help`, `login --help`, `login status --help`, `resume --help`.
- Official login: `codex login`; check: `codex login status` (0 = credentials present).
- No standalone quota command in inspected CLI. Provider returns Not available; no private endpoints.
- Default sessions exists, with year/month hierarchy. No credentials or session contents read.
- CODEX_HOME controls local state; file credential store explicitly selected for each managed command.

## Architecture and project structure
`src/CodexAccountManager.Core`: models, atomic JSON repository, path guards, native junctions,
profile service, dependency detector, process launcher, status provider, portable logger.
`src/CodexAccountManager`: one WinForms MainForm with compact grid, inline add/settings fields,
toolbar actions and status area. Native confirmation dialogs only.
`tests/CodexAccountManager.Tests`: dependency-free executable integration test harness on Windows.
`scripts`: build/test/publish via PowerShell 7. `dist/CodexAccountManager`: deployed self-contained folder.

## Data model
accounts.json: version=1, accounts with GUID id, displayName, relative profilePath, sharedSessions,
createdAt, lastCheckedAt, loginStatus, note. No credentials/output stored.
settings.json: version=1, defaultCodexHome, workingDirectory. App base always executable directory.
JSON updates use same-directory temporary file then atomic rename; invalid/version-mismatched JSON
fails closed without overwrite. A file lock excludes concurrent manager writers for each portable copy.

## Flows
1. Add: validate name, dependencies and shared target; generate GUID; create profile; write new
   file-storage config; create and verify junction; persist account; launch official login terminal.
   Launch failure leaves registered profile available for retry. No automatic deletion on uncertainty.
2. Login: verify profile and junction, launch pwsh -NoProfile -NoExit with encoded fixed script,
   invoke resolved codex path, isolated CODEX_HOME and file credential override. No OAuth implementation.
3. Open/Resume: same isolated process, optional user-selected working directory; resume --all.
   Strip inherited API-key/access-token/remote attachment variables that could bypass profile identity.
4. Check: async bounded hidden one-shot official login status; capture only in memory and classify
   known messages, never expose raw output. Credentials present is not online token validity.
5. Delete: user confirms exact profile and closing terminals; refuse known live launched terminals;
   validate canonical containment, ancestors and all reparse entries before deletion; verify session
   junction and exact configured target; detach junction without recursion; verify shared target;
   delete remaining entries with no-follow traversal; remove JSON record last.
6. Settings: choose existing local default home and working directory; do not retarget existing
   profiles automatically. Shared home changes blocked while accounts remain.

## Junction strategy and risks
Use CreateFile OPEN_REPARSE_POINT/BACKUP_SEMANTICS and DeviceIoControl SET/GET_REPARSE_POINT,
requiring IO_REPARSE_TAG_MOUNT_POINT. Direct API avoids cmd quoting and elevation.
Canonical local drive paths only; disallow reparse ancestors and shared target inside app tree.
Directory handles deny delete-sharing during traversal to prevent ancestor renames.
No Directory.Delete(path,true), no recursive operation on a junction. Unexpected nested links abort.
Missing/broken/wrong junction, locks, access errors, invalid JSON or changed settings fail closed.
Interrupted deletion retains account record; recovery is explicit/manual, never guessed.
Manager-owned terminal processes survive manager close; tracked live terminals prevent deletion;
after restart user confirmation covers untracked terminals. Concurrent external mutation can cause
partial profile cleanup; shared target is never enumerated for deletion.
Moving the portable folder preserves relative profile locations but junction targets are absolute.
Different machines require removing profiles safely before moving or manual verified relinking.
Shared sessions are writable by CLI; CLI actions can alter them. Private state databases/history
stay isolated, so shared session files do not promise identical App/CLI session indexing.

## Tests and acceptance
Real NTFS fixtures entirely under test-owned temp root: two independent profiles, junction tag/target,
shared sentinel survives deletion, normal sessions directory aborts without changes, wrong target aborts,
traversal/invalid IDs rejected, nested reparse/ancestor links rejected, broken links rejected,
atomic metadata/version handling, quoting and child env isolation, official status on empty profile.
Build Release, run tests, publish self-contained win-x64, run UI smoke without login, verify executable.
Manual acceptance remains real browser login, simultaneous authenticated terminals and App session resume.

## References
- https://learn.chatgpt.com/docs/config-file/config-advanced
- https://learn.chatgpt.com/docs/auth
- https://learn.chatgpt.com/docs/developer-commands?surface=cli
- https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-point-operations
- https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_set_reparse_point
