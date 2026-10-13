#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Sha256,
    [string]$Codex = 'codex',
    [ValidateRange(1,168)][int]$WaitHours = 24
)
$ErrorActionPreference = 'Stop'
$portableRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$sourcePath = [IO.Path]::GetFullPath($Source)
$targetPath = Join-Path $portableRoot 'CodexAccountManager.exe'
$statusPath = Join-Path $portableRoot 'logs/update-1.12.0.json'
$repairPath = Join-Path $portableRoot 'logs/session-store-repair-1.12.0.json'
if (-not $sourcePath.StartsWith($portableRoot + '\versions\1.12.0\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($sourcePath) -ne 'CodexAccountManager.exe') { throw 'Invalid staged release path.' }
function Assert-OrdinaryPath([string]$Path) {
    for ($candidate = [IO.Path]::GetFullPath($Path); $candidate; $candidate = [IO.Path]::GetDirectoryName($candidate)) {
        if ((Test-Path -LiteralPath $candidate) -and ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Unexpected reparse point. Update stopped.'
        }
    }
}
foreach ($path in @($sourcePath,$targetPath,$statusPath,$repairPath)) { Assert-OrdinaryPath $path }
New-Item -ItemType Directory -Path (Split-Path $statusPath -Parent) -Force | Out-Null
function Write-JsonAtomic([string]$Path, $Value) {
    Assert-OrdinaryPath $Path
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $Path, $true)
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
function Status([string]$State, [string]$Detail) {
    Write-JsonAtomic $statusPath @{version='1.12.0';state=$State;detail=$Detail;at=[DateTimeOffset]::UtcNow.ToString('O');helperPid=$PID}
}
function Invoke-Maintenance([string[]]$Arguments) {
    Assert-OrdinaryPath $sourcePath
    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $Sha256) { throw 'Staged release hash changed.' }
    $info = [Diagnostics.ProcessStartInfo]::new($sourcePath)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in @('--root',$portableRoot) + $Arguments) { [void]$info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync(); $errorText = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(120000)) { throw 'Maintenance still running; no other update step was attempted.' }
        $result = $output.GetAwaiter().GetResult(); $diagnostic = $errorText.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw ('Maintenance failed: ' + $diagnostic.Trim()) }
        return ($result | ConvertFrom-Json)
    } finally { $process.Dispose() }
}
try {
    Status 'waiting_for_idle' 'Close Codex Desktop, Codex CLI sessions and Account Manager. No process will be stopped.'
    $deadline = [DateTimeOffset]::UtcNow.AddHours($WaitHours)
    while (@(Get-Process -Name codex,ChatGPT,CodexAccountManager -ErrorAction SilentlyContinue).Count -gt 0) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) { Status 'expired' 'Applications remain open. Run this script again after closing them.'; exit 2 }
        Start-Sleep -Seconds 10
    }
    Status 'repairing' 'Validating and repairing shared session paths.'
    $before = Invoke-Maintenance -Arguments @('--session-store-audit')
    if ($before.audit.issues.Count -gt 0) { throw 'Session store audit requires manual review.' }
    $hashes = @{}
    foreach ($repair in $before.audit.repairs) {
        Assert-OrdinaryPath $repair.canonicalPath
        $hashes[$repair.threadId] = (Get-FileHash -LiteralPath $repair.canonicalPath -Algorithm SHA256).Hash
    }
    $result = Invoke-Maintenance -Arguments @('--session-store-repair','--verify-api')
    Write-JsonAtomic $repairPath $result
    if ($result.after.repairs.Count -ne 0 -or $result.after.issues.Count -ne 0) { throw 'Post-repair audit did not pass.' }
    foreach ($repair in $before.audit.repairs) {
        if ((Get-FileHash -LiteralPath $repair.canonicalPath -Algorithm SHA256).Hash -ne $hashes[$repair.threadId]) { throw 'Transcript changed during verification.' }
        if (-not $repair.archived -and $repair.threadId -notin $result.visibleSessionIds) { throw 'A repaired active thread is still missing from the API list.' }
    }
    if (@(Get-Process -Name codex,ChatGPT,CodexAccountManager -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Applications reopened. Data repair finished; executable promotion postponed.' }
    Assert-OrdinaryPath $targetPath
    $stagedTarget = Join-Path $portableRoot ('.update-' + [guid]::NewGuid().ToString('N') + '.exe')
    $lockPath = Join-Path $portableRoot 'manager.lock'
    Assert-OrdinaryPath $lockPath
    $promotionLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Copy-Item -LiteralPath $sourcePath -Destination $stagedTarget
        if ((Get-FileHash -LiteralPath $stagedTarget -Algorithm SHA256).Hash -ne $Sha256) { throw 'Staged executable hash mismatch.' }
        [IO.File]::Move($stagedTarget, $targetPath, $true)
    } finally {
        $promotionLock.Dispose()
        if (Test-Path -LiteralPath $stagedTarget) { Remove-Item -LiteralPath $stagedTarget }
    }
    & $Codex mcp add codex_account_manager -- $targetPath --root $portableRoot --mcp | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Executable updated but MCP registration failed.' }
    Status 'complete' ("Updated portable and MCP; repaired $($result.changed) paths; transcript hashes and API visibility verified.")
} catch {
    Status 'failed' $_.Exception.Message
    throw
}
