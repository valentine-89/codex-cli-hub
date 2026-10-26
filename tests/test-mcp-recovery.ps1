#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Manager)
$ErrorActionPreference = 'Stop'
$executable = [IO.Path]::GetFullPath($Manager)
$root = Join-Path ([IO.Path]::GetTempPath()) ('ManagerBridgeAcceptance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$clients = @()
$hosts = [Collections.Generic.List[int]]::new()
function Start-Client {
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    foreach ($argument in @('--root',$root,'--mcp')) { [void]$info.ArgumentList.Add($argument) }
    $client = [Diagnostics.Process]::Start($info)
    Send $client @{jsonrpc='2.0';id=1;method='initialize';params=@{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='recovery-test';version='1'}}}
    $hello = Receive $client 1
    if ($hello.serverInfo.version -ne '1.12.4') { throw 'Wrong MCP version.' }
    Send $client @{jsonrpc='2.0';method='notifications/initialized'}
    return $client
}
function Send($Client, $Value) {
    $Client.StandardInput.WriteLine(($Value | ConvertTo-Json -Depth 12 -Compress))
    $Client.StandardInput.Flush()
}
function Receive($Client, [int]$Id) {
    $line = $Client.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(40)).GetAwaiter().GetResult()
    if (-not $line) { throw ('Client closed: ' + $Client.StandardError.ReadToEnd()) }
    $response = $line | ConvertFrom-Json
    if ($response.id -ne $Id -or $response.error -or $response.result.isError) { throw $line }
    return $response.result
}
function HealthRequest([int]$Id) { return @{jsonrpc='2.0';id=$Id;method='tools/call';params=@{name='manager_health';arguments=@{}}} }
function Stop-FixtureHost([int]$HostProcessId) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$HostProcessId"
    if (-not $process) { return }
    if ($process.ExecutablePath -ine $executable -or -not $process.CommandLine.Contains($root) -or
        -not $process.CommandLine.Contains('--automation-host')) { throw 'Fixture host identity mismatch.' }
    $owned = Get-Process -Id $HostProcessId
    $owned.Kill(); $owned.WaitForExit()
}
try {
    $clients = @((Start-Client),(Start-Client))
    $unexpected = @(Get-CimInstance Win32_Process -Filter "Name='CodexAccountManager.exe'" | Where-Object {
        $_.CommandLine.Contains($root) -and $_.CommandLine.Contains('--automation-host')
    })
    if ($unexpected.Count) { throw 'Handshake eagerly started a bridge.' }
    # Both clients request a missing host concurrently. Only one bridge should win.
    foreach ($client in $clients) { Send $client (HealthRequest 2) }
    $health = @($clients | ForEach-Object { (Receive $_ 2).structuredContent })
    foreach ($item in $health) { if (-not $hosts.Contains($item.processId)) { $hosts.Add($item.processId) } }
    if ($hosts.Count -ne 1 -or $health[0].version -ne '1.12.4') { throw 'Concurrent startup created competing bridges.' }
    Stop-FixtureHost $hosts[0]
    # Keep the original MCP clients alive while simulating a bridge crash/GUI exit.
    foreach ($client in $clients) { Send $client (HealthRequest 3) }
    $recovered = @($clients | ForEach-Object { (Receive $_ 3).structuredContent })
    foreach ($item in $recovered) { if (-not $hosts.Contains($item.processId)) { $hosts.Add($item.processId) } }
    if ($recovered[0].processId -eq $health[0].processId -or $recovered[0].processId -ne $recovered[1].processId) {
        throw 'Clients did not recover to one new bridge.'
    }
    foreach ($client in $clients) { $client.StandardInput.Close(); if (-not $client.WaitForExit(5000)) { throw 'Client did not exit on EOF.' } }
    [pscustomobject]@{version='1.12.4';clients=2;lazyStartup=$true;singleHost=$true;recoveredWithoutClientRestart=$true}
} finally {
    foreach ($client in $clients) { if (-not $client.HasExited) { $client.Kill(); $client.WaitForExit() }; $client.Dispose() }
    # Also find any fixture-only host whose startup failed before a response.
    Get-CimInstance Win32_Process -Filter "Name='CodexAccountManager.exe'" | Where-Object {
        $_.ExecutablePath -ieq $executable -and $_.CommandLine.Contains($root) -and $_.CommandLine.Contains('--automation-host')
    } | ForEach-Object { Stop-FixtureHost $_.ProcessId }
    # This fixture must contain only top-level lock files; no account/profile writes.
    foreach ($file in Get-ChildItem -LiteralPath $root -Force) {
        if ($file.PSIsContainer -or $file.Name -notin @('manager.lock','bridge-start.lock')) { throw 'Unexpected fixture artifact; preserved for review.' }
        Remove-Item -LiteralPath $file.FullName
    }
    Remove-Item -LiteralPath $root
}
