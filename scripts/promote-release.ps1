#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Sha256)
$ErrorActionPreference = 'Stop'
$portableRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
$sourcePath = [IO.Path]::GetFullPath($Source)
$targetPath = Join-Path $portableRoot 'CodexAccountManager.exe'
if (-not $sourcePath.StartsWith(($portableRoot + '\versions\'), [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($sourcePath) -ne 'CodexAccountManager.exe') { throw 'Release must be inside the portable versions directory.' }
function Assert-OrdinaryPath([string]$Path) {
    for ($candidate = $Path; $candidate; $candidate = [IO.Path]::GetDirectoryName($candidate)) {
        if ((Test-Path -LiteralPath $candidate) -and ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Unexpected reparse point. Executable preserved.'
        }
    }
}
Assert-OrdinaryPath $sourcePath
Assert-OrdinaryPath $targetPath
if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $Sha256) { throw 'Release hash mismatch.' }
$until = [DateTimeOffset]::UtcNow.AddHours(24)
while ([DateTimeOffset]::UtcNow -lt $until) {
    # Never stop an app or worker. Promotion waits until the user releases the old executable.
    $owners = @(Get-CimInstance Win32_Process -Filter "Name='CodexAccountManager.exe'" | Where-Object {
        [string]::Equals($_.ExecutablePath, $targetPath, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($owners.Count -eq 0) {
        Assert-OrdinaryPath $sourcePath
        Assert-OrdinaryPath $targetPath
        if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $Sha256) { throw 'Release changed. Executable preserved.' }
        try { Copy-Item -LiteralPath $sourcePath -Destination $targetPath -ErrorAction Stop }
        catch [IO.IOException] { Start-Sleep -Seconds 5; continue }
        if ((Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash -ne $Sha256) { throw 'Published executable hash mismatch.' }
        Write-Output 'Release promoted after the old executable was closed.'
        exit 0
    }
    Start-Sleep -Seconds 5
}
throw 'Old executable is still in use. Versioned release remains available.'
