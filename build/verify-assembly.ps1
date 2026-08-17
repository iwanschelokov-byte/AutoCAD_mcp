<#
.SYNOPSIS
    Verify the built plugin without AutoCAD installed.

.DESCRIPTION
    Loads the compiled assembly with the AutoCAD reference assemblies resolved
    from the NuGet cache, then actually exercises the parts that do not touch a
    Database:

      * CommandRegistry initialises and every command registers exactly once
      * read/write/destructive classification is sane
      * JSON-RPC framing, error codes, and both safety gates behave

    This is what CI runs. It cannot verify command bodies that call the AutoCAD
    API - those need a live session via tests/runtime_verify.py.

.PARAMETER TargetFramework
    Which built leg to verify. Defaults to net48.

.PARAMETER Configuration
    Release (default) or Debug.
#>
[CmdletBinding()]
param(
    [string]$TargetFramework = 'net48',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $RepoRoot "autocad-plugin\src\AutoCADMCPPlugin\bin\$Configuration\$TargetFramework\AutoCADMCPPlugin.dll"

if (-not (Test-Path $dll)) { throw "Built assembly not found: $dll" }

# Reference assemblies differ per leg; pick the matching NuGet major.
$nuget = Join-Path $env:USERPROFILE '.nuget\packages'
switch -Wildcard ($TargetFramework) {
    'net48'            { $refVer = '24.0.0'; $refLib = 'net47' }
    'net8.0-windows'   { $refVer = '25.0.*'; $refLib = 'net8.0' }
    'net10.0-windows'  { $refVer = '26.0.*'; $refLib = 'net8.0' }
    default            { throw "Unknown target framework: $TargetFramework" }
}

$refDirs = @()
foreach ($pkg in 'autocad.net.model', 'autocad.net.core', 'autocad.net') {
    $base = Join-Path $nuget $pkg
    if (-not (Test-Path $base)) { continue }
    $verDir = Get-ChildItem $base -Directory |
              Where-Object { $_.Name -like $refVer } |
              Sort-Object Name -Descending | Select-Object -First 1
    if (-not $verDir) { continue }
    $libDir = Get-ChildItem (Join-Path $verDir.FullName 'lib') -Directory -ErrorAction SilentlyContinue |
              Sort-Object Name -Descending | Select-Object -First 1
    if ($libDir) { $refDirs += $libDir.FullName }
}

$script:probeDirs = $refDirs + @([System.IO.Path]::GetDirectoryName($dll))

$resolver = [System.ResolveEventHandler]{
    param($sender, $e)
    $name = ($e.Name -split ',')[0]
    foreach ($d in $script:probeDirs) {
        $p = Join-Path $d "$name.dll"
        if (Test-Path $p) { return [System.Reflection.Assembly]::LoadFrom($p) }
    }
    return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

$asm = [System.Reflection.Assembly]::LoadFrom($dll)

$failures = New-Object System.Collections.Generic.List[string]
function Check($name, $condition, $detail) {
    if ($condition) {
        Write-Host ("  PASS  {0,-46} {1}" -f $name, $detail) -ForegroundColor Green
    } else {
        Write-Host ("  FAIL  {0,-46} {1}" -f $name, $detail) -ForegroundColor Red
        $failures.Add($name)
    }
}

Write-Host "`nVerifying $TargetFramework ($Configuration)" -ForegroundColor Cyan
Write-Host ("  assembly: {0} v{1}" -f $asm.GetName().Name, $asm.GetName().Version)

# --- Registry ---------------------------------------------------------------
$reg = $asm.GetType('AutoCADMCPPlugin.Core.CommandRegistry')
$methods = @($reg.GetMethod('GetAllMethods').Invoke($null, @()))
$unique = ($methods | Sort-Object -Unique).Count

Check 'registry initialises' ($methods.Count -gt 0) "$($methods.Count) methods"
Check 'no duplicate method names' ($unique -eq $methods.Count) "$unique unique"

# Every concrete ICommand in the assembly must be registered, or it is dead code.
$icmd = $asm.GetType('AutoCADMCPPlugin.Models.ICommand')
try { $types = $asm.GetTypes() }
catch [System.Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ } }
$concrete = @($types | Where-Object { $_ -and -not $_.IsAbstract -and $icmd.IsAssignableFrom($_) })
Check 'every command class is registered' ($concrete.Count -eq $methods.Count) `
      "$($concrete.Count) classes / $($methods.Count) registered"

# --- Classification ---------------------------------------------------------
$get = $reg.GetMethod('GetCommand')
function Get-Command-Info([string]$name) { $get.Invoke($null, @([string]$name)) }

$write = 0; $direct = 0; $destructive = @()
foreach ($m in $methods) {
    $c = Get-Command-Info $m
    if ($c.IsWrite) { $write++ }
    if ($c.RunDirect) { $direct++ }
    if ($c.IsDestructive) { $destructive += [string]$m }
}
Check 'write/read split is plausible' (($write -gt 0) -and ($write -lt $methods.Count)) `
      "$write write / $($methods.Count - $write) read"
Check 'introspection tools run direct' ($direct -ge 2) "$direct direct"
Check 'destructive set is tight' (($destructive.Count -gt 0) -and ($destructive.Count -lt 20)) `
      "$($destructive.Count): $($destructive -join ', ')"

# A destructive command that is not also a write would bypass read-only mode.
$badDestructive = @($destructive | Where-Object { -not (Get-Command-Info $_).IsWrite })
Check 'destructive commands are also writes' ($badDestructive.Count -eq 0) `
      $(if ($badDestructive) { "offenders: $($badDestructive -join ', ')" } else { 'consistent' })

# --- JSON-RPC contract and safety gates -------------------------------------
$h = $asm.GetType('AutoCADMCPPlugin.Core.JsonRpcHandler').GetMethod('ProcessRequest')
function Rpc([string]$json) { $h.Invoke($null, @($json)) | ConvertFrom-Json }

$r = Rpc '{"jsonrpc":"2.0","method":"list_methods","params":{},"id":1}'
Check 'list_methods over JSON-RPC' ($r.result.count -eq $methods.Count) "count=$($r.result.count)"

$r = Rpc '{"jsonrpc":"2.0","method":"get_capabilities","params":{},"id":2}'
Check 'get_capabilities reports build' ($r.result.target_framework -eq $TargetFramework) `
      "$($r.result.target_framework) | $($r.result.supports)"

$r = Rpc '{"jsonrpc":"2.0","method":"no_such_tool","params":{},"id":3}'
Check 'unknown method -> NotFound' `
      (($r.error.code -eq -32601) -and ($r.error.data.errorCode -eq 'NotFound')) `
      "code=$($r.error.code)"

$r = Rpc 'definitely not json'
Check 'malformed JSON -> -32700' ($r.error.code -eq -32700) "code=$($r.error.code)"

$firstDestructive = $destructive | Select-Object -First 1
$r = Rpc ('{"jsonrpc":"2.0","method":"' + $firstDestructive + '","params":{"id":"1"},"id":4}')
Check 'destructive gate blocks unconfirmed' ($r.error.data.errorCode -eq 'NeedsConfirm') `
      "$firstDestructive -> $($r.error.data.errorCode)"

# Read-only mode. Set the property directly so nothing is persisted to disk.
$settings = $asm.GetType('AutoCADMCPPlugin.Core.Settings')
$settings.GetProperty('ReadOnly').SetValue($null, $true)
try {
    $r = Rpc '{"jsonrpc":"2.0","method":"create_line","params":{"start":[0,0],"end":[1,1]},"id":5}'
    Check 'read-only blocks a write tool' ($r.error.data.errorCode -eq 'ReadOnly') `
          "errorCode=$($r.error.data.errorCode)"

    $r = Rpc '{"jsonrpc":"2.0","method":"get_server_options","params":{},"id":6}'
    Check 'read-only still serves read tools' ($r.result.read_only -eq $true) 'served'
}
finally {
    $settings.GetProperty('ReadOnly').SetValue($null, $false)
}

# --- Result -----------------------------------------------------------------
Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "  $($failures.Count) check(s) FAILED: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host '  All assembly checks passed.' -ForegroundColor Green
Write-Host '  (Command bodies that call the AutoCAD API need tests/runtime_verify.py.)' -ForegroundColor Gray
Write-Host ''
