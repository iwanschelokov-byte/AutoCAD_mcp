@echo off
setlocal

echo ============================================
echo  AutoCAD MCP Plugin - Installer
echo ============================================
echo.

:: Paths
set "PROJECT_DIR=%~dp0src\AutoCADMCPPlugin"
set "BUNDLE_SRC=%~dp0config\AutoCADMCPPlugin.bundle"
set "BUNDLE_DST=%APPDATA%\Autodesk\ApplicationPlugins\AutoCADMCPPlugin.bundle"
set "BUILD_DIR=%PROJECT_DIR%\bin\Release"

:: Step 1: Build
echo [1/3] Building plugin for both frameworks...
dotnet build "%PROJECT_DIR%\AutoCADMCPPlugin.csproj" -c Release
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo BUILD FAILED. Please check errors above.
    pause
    exit /b 1
)
echo       Build succeeded.
echo.

:: Step 2: Create bundle folder structure
echo [2/3] Creating bundle at:
echo       %BUNDLE_DST%
echo.

if exist "%BUNDLE_DST%" (
    echo       Removing old installation...
    rmdir /s /q "%BUNDLE_DST%"
)

:: Create directories
mkdir "%BUNDLE_DST%\Contents\net48" 2>nul
mkdir "%BUNDLE_DST%\Contents\net8.0-windows" 2>nul

:: Copy manifest
copy /y "%BUNDLE_SRC%\PackageContents.xml" "%BUNDLE_DST%\PackageContents.xml" >nul

:: Copy net48 build (AutoCAD 2022-2024)
copy /y "%BUILD_DIR%\net48\AutoCADMCPPlugin.dll" "%BUNDLE_DST%\Contents\net48\" >nul
copy /y "%BUILD_DIR%\net48\Newtonsoft.Json.dll" "%BUNDLE_DST%\Contents\net48\" >nul 2>nul

:: Copy net8.0-windows build (AutoCAD 2025-2026)
copy /y "%BUILD_DIR%\net8.0-windows\AutoCADMCPPlugin.dll" "%BUNDLE_DST%\Contents\net8.0-windows\" >nul
copy /y "%BUILD_DIR%\net8.0-windows\Newtonsoft.Json.dll" "%BUNDLE_DST%\Contents\net8.0-windows\" >nul 2>nul
copy /y "%BUILD_DIR%\net8.0-windows\System.Drawing.Common.dll" "%BUNDLE_DST%\Contents\net8.0-windows\" >nul 2>nul

echo       Bundle installed successfully.
echo.

:: Step 3: Summary
echo [3/3] Installation complete!
echo.
echo ============================================
echo  Installed to:
echo    %BUNDLE_DST%
echo.
echo  Bundle contents:
echo    PackageContents.xml
echo    Contents\net48\AutoCADMCPPlugin.dll          (AutoCAD 2022-2024)
echo    Contents\net8.0-windows\AutoCADMCPPlugin.dll (AutoCAD 2025-2026)
echo.
echo  Next steps:
echo    1. Start (or restart) AutoCAD
echo    2. The plugin loads automatically
echo    3. Type MCPSTART to start the MCP server
echo    4. Type MCPSTATUS to verify it's running
echo ============================================
echo.
pause
