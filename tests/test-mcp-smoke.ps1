#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Manager, [Parameter(Mandatory)][string]$Root)
$ErrorActionPreference = 'Stop'
$info = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Manager))
$info.UseShellExecute = $false; $info.CreateNoWindow = $true
$info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
$info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
$info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
foreach ($argument in @('--root', [IO.Path]::GetFullPath($Root), '--mcp')) { [void]$info.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($info)
try {
    $diagnostics = $process.StandardError.ReadToEndAsync()
    function Request($value) {
        $process.StandardInput.WriteLine(($value | ConvertTo-Json -Depth 10 -Compress)); $process.StandardInput.Flush()
        $line = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult()
        if (-not $line) { throw 'MCP closed before responding.' }
        $response = $line | ConvertFrom-Json
        if ($response.id -ne $value.id -or $response.jsonrpc -ne '2.0' -or $response.error) { throw 'Invalid JSON-RPC response.' }
        return $response.result
    }
    $hello = Request @{jsonrpc='2.0';id=1;method='initialize';params=@{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='manager-smoke';version='1'}}}
    if ($hello.serverInfo.version -ne '1.11.1') { throw 'Wrong Manager release.' }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}'); $process.StandardInput.Flush()
    $tools = Request @{jsonrpc='2.0';id=2;method='tools/list';params=@{}}
    if ($tools.tools.Count -ne 16 -or @($tools.tools.name | Where-Object { $_ -match 'reset|credit|delete|auth' }).Count -gt 0) { throw 'Unexpected exposed tools.' }
    $health = Request @{jsonrpc='2.0';id=3;method='tools/call';params=@{name='manager_health';arguments=@{}}}
    if ($health.isError -or -not $health.structuredContent.supportsLockedDesktop) { throw 'Bridge health failed.' }
    $accounts = Request @{jsonrpc='2.0';id=4;method='tools/call';params=@{name='accounts_list';arguments=@{}}}
    if ($accounts.isError -or ($accounts.structuredContent | ConvertTo-Json -Depth 10 -Compress) -match '"note"|"profilePath"|"access_token"|"refresh_token"') { throw 'Private account fields escaped.' }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) { throw 'MCP caller did not exit after EOF.' }
    $trailing = $process.StandardOutput.ReadToEndAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
    if ($trailing.Length -ne 0 -or $process.ExitCode -ne 0) { throw 'Unexpected output/exit after EOF.' }
    $null = $diagnostics.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
    [pscustomobject]@{version=$hello.serverInfo.version;tools=$tools.tools.Count;accounts=$accounts.structuredContent.accounts.Count;hostPid=$health.structuredContent.processId;clientExited=$true;privateFieldsExcluded=$true}
}
finally { if (-not $process.HasExited) { $process.Kill() }; $process.Dispose() }
