#Requires -Version 7.0
[CmdletBinding()]
param([string]$DotNet = 'dotnet', [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    & $DotNet build src/CodexAccountManager/CodexAccountManager.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $DotNet run --project tests/CodexAccountManager.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if (-not $SkipPublish) {
        $publishPath = Join-Path $projectRoot 'dist/CodexAccountManager'
        & $DotNet publish src/CodexAccountManager/CodexAccountManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $publishPath --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishPath
        New-Item -ItemType Directory -Path (Join-Path $publishPath 'docs') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/IMPLEMENTATION_PLAN.md') -Destination (Join-Path $publishPath 'docs')
        $exe = Join-Path $publishPath 'CodexAccountManager.exe'
        Get-FileHash -LiteralPath $exe -Algorithm SHA256
        Write-Host "Portable folder: $publishPath"
    }
}
finally { Pop-Location }
