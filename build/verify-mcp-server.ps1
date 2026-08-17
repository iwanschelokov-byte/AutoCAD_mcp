<#
.SYNOPSIS
    Drive the C# MCP server over stdio and assert the protocol contract.

.DESCRIPTION
    Feeds a real MCP handshake into the server's stdin and checks the responses:

      * initialize negotiates a protocol version and identifies the server
      * notifications/initialized produces NO response (it is a notification)
      * ping answers
      * tools/list returns the full tool surface with usable input schemas
      * an unknown tool is rejected at the protocol level
      * a tool call with the plugin down degrades to isError, not a crash

    Needs no AutoCAD: the last check deliberately exercises the unreachable path.

.EXAMPLE
    ./build/verify-mcp-server.ps1
    ./build/verify-mcp-server.ps1 -ServerExe dist/server/autocad-mcp-server.exe
#>
[CmdletBinding()]
param(
    [string]$ServerExe
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ServerExe) {
    $candidates = @(
        (Join-Path $RepoRoot 'autocad-plugin\src\AutoCADMCP.Server\publish\autocad-mcp-server.exe'),
        (Join-Path $RepoRoot 'autocad-plugin\dist\server\autocad-mcp-server.exe'),
        (Join-Path $RepoRoot 'autocad-plugin\src\AutoCADMCP.Server\bin\Release\net8.0\win-x64\autocad-mcp-server.exe')
    )
    $ServerExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $ServerExe -or -not (Test-Path $ServerExe)) {
    throw "MCP server executable not found. Build it first: dotnet publish autocad-plugin/src/AutoCADMCP.Server -c Release"
}

Write-Host "`nVerifying MCP server" -ForegroundColor Cyan
Write-Host "  exe: $ServerExe"

# Deliberately point at a port nothing is listening on, so the tool-call path
# exercises graceful degradation rather than depending on a live AutoCAD.
$deadPort = 59999

$requests = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"verify","version":"1"}}}'
    '{"jsonrpc":"2.0","method":"notifications/initialized"}'
    '{"jsonrpc":"2.0","id":2,"method":"ping"}'
    '{"jsonrpc":"2.0","id":3,"method":"tools/list"}'
    '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"definitely_not_a_tool","arguments":{}}}'
    '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"list_layers","arguments":{}}}'
    '{"jsonrpc":"2.0","id":6,"method":"totally/unknown"}'
)

$inFile  = New-TemporaryFile
$outFile = New-TemporaryFile
$errFile = New-TemporaryFile

try {
    Set-Content -Path $inFile -Value $requests -Encoding utf8

    $proc = Start-Process -FilePath $ServerExe `
        -ArgumentList @('--port', $deadPort) `
        -RedirectStandardInput $inFile `
        -RedirectStandardOutput $outFile `
        -RedirectStandardError $errFile `
        -NoNewWindow -PassThru

    if (-not $proc.WaitForExit(60000)) {
        $proc.Kill()
        throw 'Server did not exit within 60s after stdin closed.'
    }

    $responses = @(
        Get-Content $outFile |
        Where-Object { $_.Trim() } |
        ForEach-Object { $_ | ConvertFrom-Json }
    )
}
finally {
    Remove-Item $inFile, $outFile, $errFile -Force -ErrorAction SilentlyContinue
}

$failures = New-Object System.Collections.Generic.List[string]
function Check($name, $condition, $detail) {
    if ($condition) {
        Write-Host ("  PASS  {0,-44} {1}" -f $name, $detail) -ForegroundColor Green
    } else {
        Write-Host ("  FAIL  {0,-44} {1}" -f $name, $detail) -ForegroundColor Red
        $failures.Add($name)
    }
}

function ById($id) { $responses | Where-Object { $_.id -eq $id } | Select-Object -First 1 }

# Seven requests were sent but one is a notification, so six replies are correct.
Check 'notification produces no response' ($responses.Count -eq 6) `
      "$($responses.Count) responses for 7 requests"

$init = ById 1
Check 'initialize negotiates protocol' ($null -ne $init.result.protocolVersion) `
      "protocol=$($init.result.protocolVersion) server=$($init.result.serverInfo.name)"

Check 'initialize advertises tools capability' ($null -ne $init.result.capabilities.tools) 'tools capability present'

$ping = ById 2
Check 'ping answers' ($null -ne $ping.result) 'ok'

$list = ById 3
$tools = $list.result.tools
Check 'tools/list returns tools' ($tools.Count -gt 100) "$($tools.Count) tools"

$named = $tools | Where-Object { $_.name -eq 'create_viewport' } | Select-Object -First 1
Check 'tools carry real input schemas' `
      ($named -and $named.inputSchema.required -contains 'center') `
      "create_viewport required=$($named.inputSchema.required -join ',')"

$noEmptyNames = -not ($tools | Where-Object { [string]::IsNullOrWhiteSpace($_.name) })
Check 'every tool has a name' $noEmptyNames "$($tools.Count) checked"

$noEmptyDesc = -not ($tools | Where-Object { [string]::IsNullOrWhiteSpace($_.description) })
Check 'every tool has a description' $noEmptyDesc "$($tools.Count) checked"

$unknownTool = ById 4
Check 'unknown tool rejected' ($unknownTool.error.code -eq -32602) `
      "code=$($unknownTool.error.code)"

$deadCall = ById 5
Check 'plugin down degrades to isError' `
      (($deadCall.result.isError -eq $true) -and ($deadCall.result.content[0].text -match 'plugin')) `
      'graceful, with an actionable message'

$unknownMethod = ById 6
Check 'unknown method -> -32601' ($unknownMethod.error.code -eq -32601) `
      "code=$($unknownMethod.error.code)"

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "  $($failures.Count) check(s) FAILED: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host '  All MCP protocol checks passed.' -ForegroundColor Green
Write-Host ''
