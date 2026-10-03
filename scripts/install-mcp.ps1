#Requires -Version 7.0
[CmdletBinding()]
param([string]$Codex = 'codex',
    [string]$Manager = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist/CodexAccountManager/CodexAccountManager.exe'),
    [string]$Root)
$ErrorActionPreference = 'Stop'
$manager = [IO.Path]::GetFullPath($Manager)
if (-not (Test-Path -LiteralPath $manager -PathType Leaf)) { throw 'Publish Account Manager first.' }
# Register only in the invoking Codex home, never in supervised worker profiles.
if ($Root) { & $Codex mcp add codex_account_manager -- $manager --root ([IO.Path]::GetFullPath($Root)) --mcp }
else { & $Codex mcp add codex_account_manager -- $manager --mcp }
if ($LASTEXITCODE -ne 0) { throw 'MCP registration failed.' }
Write-Output 'Registered codex_account_manager. New Codex connections discover its tools.'
