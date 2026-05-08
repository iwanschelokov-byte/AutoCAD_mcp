@echo off
setlocal

echo ============================================
echo  AutoCAD MCP Plugin - Pre-built Installer
echo ============================================
echo.

:: Paths
set "SCRIPT_DIR=%~dp0"
set "BUILD_DIR=%SCRIPT_DIR%dist"
set "BUNDLE_SRC=%SCRIPT_DIR%config\AutoCADMCPPlugin.bundle"
set "BUNDLE_DST=%APPDATA%\Autodesk\ApplicationPlugins\AutoCADMCPPlugin.bundle"

:: Check if pre-built DLL exists
if not exist "%BUILD_DIR%\net48\AutoCADMCPPlugin.dll" (
    echo  ERROR: Pre-built plugin not found at:
    echo    %BUILD_DIR%\net48\AutoCADMCPPlugin.dll
    echo.
    echo  Please make sure the Release build files are included.
    pause
    exit /b 1
)

:: Check if AutoCAD is running
tasklist /FI "IMAGENAME eq acad.exe" 2>nul | find /I "acad.exe" >nul
if %ERRORLEVEL% EQU 0 (
    echo  WARNING: AutoCAD is currently running.
    echo  Please close AutoCAD before installing.
    echo.
    pause
    exit /b 1
)

:: Step 1: Create bundle folder structure
echo [1/2] Installing plugin to:
echo       %BUNDLE_DST%
echo.

if exist "%BUNDLE_DST%" (
    echo       Removing old installation...
    rmdir /s /q "%BUNDLE_DST%"
)

mkdir "%BUNDLE_DST%\Contents\net48" 2>nul
mkdir "%BUNDLE_DST%\Contents\net8.0-windows" 2>nul

:: Copy manifest
copy /y "%BUNDLE_SRC%\PackageContents.xml" "%BUNDLE_DST%\PackageContents.xml" >nul

:: Copy net48 build (AutoCAD 2022-2024)
copy /y "%BUILD_DIR%\net48\AutoCADMCPPlugin.dll" "%BUNDLE_DST%\Contents\net48\" >nul
if exist "%BUILD_DIR%\net48\Newtonsoft.Json.dll" (
    copy /y "%BUILD_DIR%\net48\Newtonsoft.Json.dll" "%BUNDLE_DST%\Contents\net48\" >nul
)

:: Copy net8.0-windows build (AutoCAD 2025-2026) if available
if exist "%BUILD_DIR%\net8.0-windows\AutoCADMCPPlugin.dll" (
    copy /y "%BUILD_DIR%\net8.0-windows\AutoCADMCPPlugin.dll" "%BUNDLE_DST%\Contents\net8.0-windows\" >nul
    if exist "%BUILD_DIR%\net8.0-windows\Newtonsoft.Json.dll" (
        copy /y "%BUILD_DIR%\net8.0-windows\Newtonsoft.Json.dll" "%BUNDLE_DST%\Contents\net8.0-windows\" >nul
    )
    if exist "%BUILD_DIR%\net8.0-windows\System.Drawing.Common.dll" (
        copy /y "%BUILD_DIR%\net8.0-windows\System.Drawing.Common.dll" "%BUNDLE_DST%\Contents\net8.0-windows\" >nul
    )
)

echo       Files copied successfully.
echo.

:: Step 2: Summary
echo [2/2] Installation complete!
echo.
echo ============================================
echo  Installed to:
echo    %BUNDLE_DST%
echo.
echo  Next steps:
echo    1. Open AutoCAD
echo    2. The plugin loads automatically
echo    3. Type MCPSTART to start the MCP server
echo    4. Type MCPSTATUS to verify it's running
echo ============================================
echo.
pause
