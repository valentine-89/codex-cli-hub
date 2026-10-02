#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tool,
    [string]$ArgumentsJson = '{}',
    [string]$Manager = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist/CodexAccountManager/CodexAccountManager.exe'),
    [string]$Root
)
$ErrorActionPreference = 'Stop'
$null = $ArgumentsJson | ConvertFrom-Json
$info = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Manager))
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
if ($Root) { [void]$info.ArgumentList.Add('--root'); [void]$info.ArgumentList.Add([IO.Path]::GetFullPath($Root)) }
foreach ($argument in @('--call', $Tool, '--json', $ArgumentsJson)) { [void]$info.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($info)
try {
    $outputTask = $process.StandardOutput.ReadToEndAsync()
    $errorTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(110000)) {
        $process.Kill() # Only the short-lived caller; never a Manager or session worker.
        throw 'MCP call outcome unknown. Re-read session state before retrying a mutation.'
    }
    $output = $outputTask.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
    $diagnostic = $errorTask.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "MCP call failed: $diagnostic" }
    $output | ConvertFrom-Json
}
finally { $process.Dispose() }
