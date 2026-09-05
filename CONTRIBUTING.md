# Contributing

Use Windows x64 on local NTFS, PowerShell 7, .NET 8 SDK and the official Codex CLI.
The integration suite was verified with CLI 0.153.4. No Visual Studio installation is required.

```powershell
pwsh -File ./scripts/build.ps1 -SkipPublish
```

Tests create synthetic profiles under the temporary directory and clean them without following
junctions. They check CLI status on an empty profile; they do not perform a real browser login
or replace your default authentication. Never use real credentials in tests, screenshots or fixtures.

Keep changes focused. Include reproduction steps and relevant test results in pull requests.
Changes to path validation, credential copying, process isolation, Apply or deletion must preserve
the existing safety checks and include regression coverage. UI should remain compact.

Before submitting, run `git diff --check` and the build/test script. To remove generated files:

```powershell
pwsh -File ./scripts/clean.ps1 -WhatIf
pwsh -File ./scripts/clean.ps1
```

Do not attach `auth.json`, `accounts.json`, `settings.json`, session files, profiles, raw CLI output,
tokens or a ZIP of an already-used portable folder. Report OS/app/CLI/PowerShell versions and
sanitized error messages instead. Licensing terms, if present, are in `LICENSE`.
