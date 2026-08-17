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
echo [1/4] Building plugin for all available target frameworks...
echo       net48 and net8.0-windows always; net10.0-windows as well when
echo       AutoCAD 2027 is installed - it compiles against the Newtonsoft.Json
echo       that ships with 2027, so it needs 2027 on this machine plus the
echo       .NET SDK 10.0.
echo.
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
echo [2/4] Creating bundle at:
echo       %BUNDLE_DST%
echo.

if exist "%BUNDLE_DST%" (
    echo       Removing old installation...
    rmdir /s /q "%BUNDLE_DST%"
)

:: Create directories
mkdir "%BUNDLE_DST%\Contents\net48" 2>nul
mkdir "%BUNDLE_DST%\Contents\net8.0-windows" 2>nul
if exist "%BUILD_DIR%\net10.0-windows\AutoCADMCPPlugin.dll" mkdir "%BUNDLE_DST%\Contents\net10.0-windows" 2>nul

:: Copy manifest
copy /y "%BUNDLE_SRC%\PackageContents.xml" "%BUNDLE_DST%\PackageContents.xml" >nul

:: Copy net48 build (AutoCAD 2022-2024)
copy /y "%BUILD_DIR%\net48\AutoCADMCPPlugin.dll" "%BUNDLE_DST%\Contents\net48\" >nul
copy /y "%BUILD_DIR%\net48\Newtonsoft.Json.dll" "%BUNDLE_DST%\Contents\net48\" >nul 2>nul

:: Copy net8.0-windows build (AutoCAD 2025-2026)
copy /y "%BUILD_DIR%\net8.0-windows\AutoCADMCPPlugin.dll" "%BUNDLE_DST%\Contents\net8.0-windows\" >nul
copy /y "%BUILD_DIR%\net8.0-windows\Newtonsoft.Json.dll" "%BUNDLE_DST%\Contents\net8.0-windows\" >nul 2>nul
copy /y "%BUILD_DIR%\net8.0-windows\System.Drawing.Common.dll" "%BUNDLE_DST%\Contents\net8.0-windows\" >nul 2>nul

:: Copy net10.0-windows build (AutoCAD 2027) when it was built.
:: Newtonsoft.Json is deliberately NOT copied here - AutoCAD 2027 ships its own
:: copy in the program folder, and a second one in the bundle can shadow it.
if exist "%BUILD_DIR%\net10.0-windows\AutoCADMCPPlugin.dll" (
    copy /y "%BUILD_DIR%\net10.0-windows\AutoCADMCPPlugin.dll" "%BUNDLE_DST%\Contents\net10.0-windows\" >nul
    copy /y "%BUILD_DIR%\net10.0-windows\System.Drawing.Common.dll" "%BUNDLE_DST%\Contents\net10.0-windows\" >nul 2>nul
    copy /y "%BUILD_DIR%\net10.0-windows\Microsoft.Win32.SystemEvents.dll" "%BUNDLE_DST%\Contents\net10.0-windows\" >nul 2>nul
    copy /y "%BUILD_DIR%\net10.0-windows\System.Private.Windows.Core.dll" "%BUNDLE_DST%\Contents\net10.0-windows\" >nul 2>nul
)

:: Verify each framework folder actually got its DLL
if not exist "%BUNDLE_DST%\Contents\net48\AutoCADMCPPlugin.dll" (
    echo       [ERROR] net48\AutoCADMCPPlugin.dll missing after copy.
    pause
    exit /b 1
)
if not exist "%BUNDLE_DST%\Contents\net8.0-windows\AutoCADMCPPlugin.dll" (
    echo       [ERROR] net8.0-windows\AutoCADMCPPlugin.dll missing after copy.
    pause
    exit /b 1
)
if exist "%BUILD_DIR%\net10.0-windows\AutoCADMCPPlugin.dll" (
    if not exist "%BUNDLE_DST%\Contents\net10.0-windows\AutoCADMCPPlugin.dll" (
        echo       [ERROR] net10.0-windows\AutoCADMCPPlugin.dll missing after copy.
        echo       PackageContents.xml points AutoCAD 2027 at that folder, so
        echo       the plugin would not load there.
        pause
        exit /b 1
    )
)

echo       Bundle installed successfully.
echo.

:: Step 3: MCP server
echo [3/4] MCP server...
set "SERVEREXE=%~dp0dist\server\autocad-mcp-server.exe"
if exist "%SERVEREXE%" (
    echo       Found: dist\server\autocad-mcp-server.exe
    echo       It is self-contained - there is nothing else to install.
    echo       Point your MCP client at that path, then run MCPSTART in AutoCAD.
) else (
    echo       Not built yet. The plugin is installed and works either way;
    echo       the MCP server is what your AI client talks to. Build it with:
    echo           build\build-all.ps1
    echo       which publishes it to autocad-plugin\dist\server\.
)
echo.

:: Step 4: Summary
echo [4/4] Installation complete!
echo.
echo ============================================
echo  Installed to:
echo    %BUNDLE_DST%
echo.
echo  Bundle contents:
echo    PackageContents.xml
echo    Contents\net48\AutoCADMCPPlugin.dll           (AutoCAD 2022-2024)
echo    Contents\net8.0-windows\AutoCADMCPPlugin.dll  (AutoCAD 2025-2026)
if exist "%BUNDLE_DST%\Contents\net10.0-windows\AutoCADMCPPlugin.dll" (
    echo    Contents\net10.0-windows\AutoCADMCPPlugin.dll [AutoCAD 2027]
) else (
    echo    Contents\net10.0-windows      not built - AutoCAD 2027 not found
    echo                                  on this machine, so the 2027 target
    echo                                  was skipped. Install AutoCAD 2027 and
    echo                                  the .NET SDK 10.0, then run this again.
)
echo.
echo  Next steps:
echo    1. Start (or restart) AutoCAD
echo    2. The plugin loads automatically
echo    3. Type MCPSTART to start the MCP server
echo    4. Type MCPSTATUS to verify it's running
echo ============================================
echo.
pause
