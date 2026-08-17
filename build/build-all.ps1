<#
.SYNOPSIS
    Build every AutoCAD MCP plugin target and stage a ready-to-install bundle.

.DESCRIPTION
    One source tree produces up to three binaries:
        net48           -> AutoCAD 2021-2024 (R24.0-R24.3, .NET Framework 4.8)
        net8.0-windows  -> AutoCAD 2025-2026 (R25.0-R25.1, .NET 8)
        net10.0-windows -> AutoCAD 2027      (R26.0,       .NET 10)

    The net10 leg is opt-in because it needs the .NET 10 SDK. This script
    detects whether that SDK is present and includes the leg automatically
    unless told otherwise.

    Output is staged into dist/ as a complete .bundle folder that can be copied
    straight to %APPDATA%\Autodesk\ApplicationPlugins\.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER IncludeNet10
    Force the AutoCAD 2027 leg on or off. Omit to auto-detect the .NET 10 SDK.

.PARAMETER SkipBundle
    Compile only; do not stage the bundle folder.

.PARAMETER Sign
    Sign the built DLLs using build/sign.ps1.

.EXAMPLE
    .\build\build-all.ps1
    .\build\build-all.ps1 -IncludeNet10 -Sign

.NOTES
    Invoke directly (.\build\build-all.ps1), not via `powershell -File`, so the
    switch parameters bind correctly.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$IncludeNet10,

    [switch]$SkipBundle,

    [switch]$SkipServer,

    [switch]$Sign
)

$ErrorActionPreference = 'Stop'

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$ProjectDir = Join-Path $RepoRoot 'autocad-plugin\src\AutoCADMCPPlugin'
$Project    = Join-Path $ProjectDir 'AutoCADMCPPlugin.csproj'
$ServerProj = Join-Path $RepoRoot 'autocad-plugin\src\AutoCADMCP.Server\AutoCADMCP.Server.csproj'
$BundleSrc  = Join-Path $RepoRoot 'autocad-plugin\config\AutoCADMCPPlugin.bundle'
$DistDir    = Join-Path $RepoRoot 'autocad-plugin\dist'

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

if (-not (Test-Path $Project)) {
    throw "Project not found: $Project"
}

# ---------------------------------------------------------------------------
# Decide whether the AutoCAD 2027 (.NET 10) leg is in scope
# ---------------------------------------------------------------------------
Write-Step 'Checking toolchain'

$sdks = @(& dotnet --list-sdks 2>$null)
if (-not $sdks) { throw 'dotnet SDK not found on PATH.' }

$hasNet10 = [bool]($sdks | Where-Object { $_ -match '^\s*10\.' })
Write-Ok "Installed SDKs: $((($sdks | ForEach-Object { ($_ -split ' ')[0] }) -join ', '))"

if ($PSBoundParameters.ContainsKey('IncludeNet10')) {
    $buildNet10 = [bool]$IncludeNet10
    if ($buildNet10 -and -not $hasNet10) {
        throw 'IncludeNet10 was requested but no .NET 10 SDK is installed. Install it or drop the switch.'
    }
} else {
    $buildNet10 = $hasNet10
    if ($buildNet10) { Write-Ok '.NET 10 SDK found - AutoCAD 2027 leg will be built.' }
    else { Write-Warn '.NET 10 SDK not found - skipping the AutoCAD 2027 leg.' }
}

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
Write-Step "Building ($Configuration)"

$buildArgs = @('build', $Project, '-c', $Configuration, '--nologo')
if ($buildNet10) { $buildArgs += '-p:IncludeNet10=true' }

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
Write-Ok 'Build succeeded.'

# ---------------------------------------------------------------------------
# Collect outputs
# ---------------------------------------------------------------------------
$targets = @('net48', 'net8.0-windows')
if ($buildNet10) { $targets += 'net10.0-windows' }

$binRoot = Join-Path $ProjectDir "bin\$Configuration"
$built   = @()

foreach ($t in $targets) {
    $dll = Join-Path $binRoot "$t\AutoCADMCPPlugin.dll"
    if (Test-Path $dll) {
        $size = [math]::Round((Get-Item $dll).Length / 1KB, 1)
        Write-Ok "$t -> AutoCADMCPPlugin.dll ($size KB)"
        $built += $t
    } else {
        Write-Warn "$t -> MISSING (expected $dll)"
    }
}

if ($built.Count -eq 0) { throw 'No build outputs were produced.' }

# ---------------------------------------------------------------------------
# MCP server (version-agnostic; one self-contained exe, no Python required)
# ---------------------------------------------------------------------------
$serverExe = $null
if (-not $SkipServer -and (Test-Path $ServerProj)) {
    Write-Step 'Building MCP server'

    # Schemas are generated from the Python server's typed signatures, so
    # regenerate before publishing or the two surfaces can drift.
    $gen = Join-Path $PSScriptRoot 'generate_tool_schemas.py'
    if (Test-Path $gen) {
        $py = Get-Command python -ErrorAction SilentlyContinue
        if ($py) {
            & python $gen | ForEach-Object { Write-Ok $_.Trim() }
            if ($LASTEXITCODE -ne 0) { throw 'Tool schema generation failed.' }
        } else {
            Write-Warn 'python not found - reusing the committed tools.json.'
        }
    }

    $serverOut = Join-Path $DistDir 'server'
    & dotnet publish $ServerProj -c $Configuration -o $serverOut --nologo
    if ($LASTEXITCODE -ne 0) { throw "MCP server publish failed ($LASTEXITCODE)." }

    $serverExe = Join-Path $serverOut 'autocad-mcp-server.exe'
    if (Test-Path $serverExe) {
        $size = [math]::Round((Get-Item $serverExe).Length / 1MB, 1)
        Write-Ok "autocad-mcp-server.exe ($size MB, self-contained)"
    }
}

# ---------------------------------------------------------------------------
# Optional signing
# ---------------------------------------------------------------------------
if ($Sign) {
    Write-Step 'Signing'
    $signScript = Join-Path $PSScriptRoot 'sign.ps1'
    if (-not (Test-Path $signScript)) {
        Write-Warn "sign.ps1 not found - skipping."
    } else {
        foreach ($t in $built) {
            & $signScript -Path (Join-Path $binRoot "$t\AutoCADMCPPlugin.dll")
        }
        if ($serverExe -and (Test-Path $serverExe)) { & $signScript -Path $serverExe }
        Write-Ok 'Signing complete.'
    }
}

# ---------------------------------------------------------------------------
# Stage dist/ and the installable bundle
# ---------------------------------------------------------------------------
if (-not $SkipBundle) {
    Write-Step 'Staging dist/'

    foreach ($t in $built) {
        $srcDir = Join-Path $binRoot $t
        $dstDir = Join-Path $DistDir $t
        New-Item -ItemType Directory -Force -Path $dstDir | Out-Null

        # Ship the plugin plus its managed dependencies, never the AutoCAD API
        # assemblies (those must resolve from AutoCAD's own install directory).
        Get-ChildItem $srcDir -Filter *.dll |
            Where-Object { $_.Name -notmatch '^(acdbmgd|acmgd|accoremgd|AcCui|AcWindows)' } |
            ForEach-Object { Copy-Item $_.FullName $dstDir -Force }

        Write-Ok "$t -> $dstDir"
    }

    Write-Step 'Staging bundle'

    $bundleOut = Join-Path $DistDir 'AutoCADMCPPlugin.bundle'
    if (Test-Path $bundleOut) { Remove-Item $bundleOut -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $bundleOut | Out-Null

    foreach ($t in $built) {
        $contentsDir = Join-Path $bundleOut "Contents\$t"
        New-Item -ItemType Directory -Force -Path $contentsDir | Out-Null
        Copy-Item (Join-Path $DistDir "$t\*.dll") $contentsDir -Force
    }

    # The source manifest declares all three legs. A shipped bundle must only
    # declare the ones actually present, or AutoCAD reports a load error for a
    # missing DLL instead of quietly ignoring the entry.
    [xml]$manifest = Get-Content (Join-Path $BundleSrc 'PackageContents.xml')
    $components = $manifest.ApplicationPackage.Components
    $dropped = @()

    foreach ($entry in @($components.ComponentEntry)) {
        $module = [string]$entry.ModuleName
        $target = ($module -split '/')[-2]          # ./Contents/<target>/x.dll
        if ($built -notcontains $target) {
            [void]$components.RemoveChild($entry)
            $dropped += $target
        }
    }

    $manifest.Save((Join-Path $bundleOut 'PackageContents.xml'))

    Write-Ok "Bundle staged at $bundleOut"
    foreach ($d in $dropped) {
        Write-Warn "Dropped '$d' from the staged manifest (not built in this run)."
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
$ranges = @{
    'net48'           = 'AutoCAD 2021-2024'
    'net8.0-windows'  = 'AutoCAD 2025-2026'
    'net10.0-windows' = 'AutoCAD 2027'
}

Write-Step 'Summary'
foreach ($t in $built) { Write-Ok ("{0,-16} {1}" -f $t, $ranges[$t]) }
if ($serverExe -and (Test-Path $serverExe)) {
    Write-Ok ("{0,-16} {1}" -f 'mcp server', 'self-contained exe (no Python needed)')
}

if (-not $SkipBundle) {
    Write-Host ''
    Write-Host '    Install with:' -ForegroundColor Gray
    Write-Host '      autocad-plugin\install-prebuilt.bat' -ForegroundColor Gray
    Write-Host '    or copy the staged bundle to:' -ForegroundColor Gray
    Write-Host '      %APPDATA%\Autodesk\ApplicationPlugins\' -ForegroundColor Gray
}

Write-Host ''
