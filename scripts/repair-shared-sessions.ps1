#Requires -Version 7.0
[CmdletBinding()]
param([string]$Root = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist/CodexAccountManager'))
$ErrorActionPreference = 'Stop'
$portableRoot = [IO.Path]::GetFullPath($Root)
$manager = Join-Path $portableRoot 'CodexAccountManager.exe'
if (@(Get-Process -Name codex,ChatGPT,CodexAccountManager -ErrorAction SilentlyContinue).Count) {
    throw 'Close Codex Desktop, Codex CLI and Account Manager, then run this script again. No process was stopped.'
}
function Invoke-Maintenance([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($manager)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in @('--root',$portableRoot) + $Arguments) { [void]$info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync(); $errorText = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(120000)) { throw 'Maintenance is still running. Do not start a second repair.' }
        $result = $output.GetAwaiter().GetResult(); $diagnostic = $errorText.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw ('Maintenance stopped: ' + $diagnostic.Trim()) }
        return ($result | ConvertFrom-Json)
    } finally { $process.Dispose() }
}
$before = Invoke-Maintenance -Arguments @('--session-store-audit')
if ($before.audit.issues.Count) { throw 'Session audit requires manual review. No repair started.' }
$hashes = @{}
foreach ($repair in $before.audit.repairs) { $hashes[$repair.threadId] = (Get-FileHash -LiteralPath $repair.physicalPath).Hash }
$result = Invoke-Maintenance -Arguments @('--session-store-repair','--verify-api')
if ($result.after.repairs.Count -or $result.after.issues.Count) { throw 'Post-repair audit requires review.' }
foreach ($repair in $before.audit.repairs) {
    if ((Get-FileHash -LiteralPath $repair.physicalPath).Hash -ne $hashes[$repair.threadId]) { throw 'Transcript changed during verification.' }
    if (-not $repair.archived -and $repair.threadId -notin $result.visibleSessionIds) { throw 'Repaired session is still missing from API.' }
}
[pscustomobject]@{changed=$result.changed;remainingIssues=$result.after.issues.Count;transcriptsPreserved=$true;apiVisibilityVerified=$true}
