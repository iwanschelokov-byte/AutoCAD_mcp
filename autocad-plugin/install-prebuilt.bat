@echo off
setlocal enabledelayedexpansion

echo ============================================
echo  AutoCAD MCP Plugin - Pre-built Installer
echo ============================================
echo.

:: Paths
set "SCRIPT_DIR=%~dp0"
set "BUILD_DIR=%SCRIPT_DIR%dist"
set "BUNDLE_SRC=%SCRIPT_DIR%config\AutoCADMCPPlugin.bundle"
set "APPLUGINS=%APPDATA%\Autodesk\ApplicationPlugins"
set "BUNDLE_DST=%APPLUGINS%\AutoCADMCPPlugin.bundle"

:: Check if pre-built DLL exists
if not exist "%BUILD_DIR%\net48\AutoCADMCPPlugin.dll" (
    echo  [ERROR] Pre-built plugin not found at:
    echo    %BUILD_DIR%\net48\AutoCADMCPPlugin.dll
    echo.
    echo  Make sure you cloned the full repo including the dist folder.
    pause
    exit /b 1
)

:: Check if PackageContents.xml exists
if not exist "%BUNDLE_SRC%\PackageContents.xml" (
    echo  [ERROR] PackageContents.xml not found at:
    echo    %BUNDLE_SRC%\PackageContents.xml
    echo.
    echo  Make sure the config folder is present in the repo.
    pause
    exit /b 1
)

:: Check if AutoCAD is running
tasklist /FI "IMAGENAME eq acad.exe" 2>nul | find /I "acad.exe" >nul
if !ERRORLEVEL! EQU 0 (
    echo  [WARNING] AutoCAD is currently running.
    echo  Please close AutoCAD first, then run this script again.
    echo.
    pause
    exit /b 1
)

:: Create ApplicationPlugins folder if it doesn't exist
if not exist "%APPLUGINS%" (
    echo  Creating ApplicationPlugins folder...
    mkdir "%APPLUGINS%"
    if !ERRORLEVEL! NEQ 0 (
        echo  [ERROR] Failed to create: %APPLUGINS%
        pause
        exit /b 1
    )
)

:: Remove old installation
echo [1/3] Preparing installation folder...
if exist "%BUNDLE_DST%" (
    echo       Removing old installation...
    rmdir /s /q "%BUNDLE_DST%"
)

:: Create bundle folder structure
mkdir "%BUNDLE_DST%\Contents\net48"
mkdir "%BUNDLE_DST%\Contents\net8.0-windows" 2>nul

:: Copy manifest
echo [2/3] Copying files...
copy /y "%BUNDLE_SRC%\PackageContents.xml" "%BUNDLE_DST%\PackageContents.xml" >nul
echo       PackageContents.xml ............ OK

:: Copy net48 build (AutoCAD 2022-2024)
copy /y "%BUILD_DIR%\net48\AutoCADMCPPlugin.dll" "%BUNDLE_DST%\Contents\net48\" >nul
echo       net48\AutoCADMCPPlugin.dll ..... OK
if exist "%BUILD_DIR%\net48\Newtonsoft.Json.dll" (
    copy /y "%BUILD_DIR%\net48\Newtonsoft.Json.dll" "%BUNDLE_DST%\Contents\net48\" >nul
    echo       net48\Newtonsoft.Json.dll ..... OK
)

:: Copy net8.0-windows build (AutoCAD 2025-2026) if available
if exist "%BUILD_DIR%\net8.0-windows\AutoCADMCPPlugin.dll" (
    copy /y "%BUILD_DIR%\net8.0-windows\AutoCADMCPPlugin.dll" "%BUNDLE_DST%\Contents\net8.0-windows\" >nul
    echo       net8.0\AutoCADMCPPlugin.dll ... OK
    if exist "%BUILD_DIR%\net8.0-windows\Newtonsoft.Json.dll" (
        copy /y "%BUILD_DIR%\net8.0-windows\Newtonsoft.Json.dll" "%BUNDLE_DST%\Contents\net8.0-windows\" >nul
        echo       net8.0\Newtonsoft.Json.dll ... OK
    )
    if exist "%BUILD_DIR%\net8.0-windows\System.Drawing.Common.dll" (
        copy /y "%BUILD_DIR%\net8.0-windows\System.Drawing.Common.dll" "%BUNDLE_DST%\Contents\net8.0-windows\" >nul
        echo       net8.0\System.Drawing.Common  OK
    )
)

echo.

:: Verify installation
echo [3/3] Verifying installation...
if exist "%BUNDLE_DST%\PackageContents.xml" (
    if exist "%BUNDLE_DST%\Contents\net48\AutoCADMCPPlugin.dll" (
        echo       Verification PASSED
    ) else (
        echo       [ERROR] DLL not found after copy!
        pause
        exit /b 1
    )
) else (
    echo       [ERROR] PackageContents.xml not found after copy!
    pause
    exit /b 1
)

echo.
echo ============================================
echo  Installation successful!
echo.
echo  Installed to:
echo    %BUNDLE_DST%
echo.
echo  Supports:
echo    AutoCAD 2022-2024 (net48)
echo    AutoCAD 2025-2026 (net8.0)
echo.
echo  Next steps:
echo    1. Open AutoCAD
echo    2. The plugin loads automatically on startup
echo    3. Type MCPSTART to start the MCP server
echo    4. Type MCPSTATUS to check it's running
echo ============================================
echo.
pause
