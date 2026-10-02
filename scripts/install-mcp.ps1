#Requires -Version 7.0
[CmdletBinding()]
param([string]$Codex = 'codex')
$ErrorActionPreference = 'Stop'
$manager = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'dist/CodexAccountManager/CodexAccountManager.exe'))
if (-not (Test-Path -LiteralPath $manager -PathType Leaf)) { throw 'Publish Account Manager first.' }
# Register only in the invoking Codex home, never in supervised worker profiles.
& $Codex mcp add codex_account_manager -- $manager --mcp
if ($LASTEXITCODE -ne 0) { throw 'MCP registration failed.' }
Write-Output 'Registered codex_account_manager. New Codex connections discover its tools.'
