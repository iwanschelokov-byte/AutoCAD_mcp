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
echo [1/4] Preparing installation folder...
if exist "%BUNDLE_DST%" (
    echo       Removing old installation...
    rmdir /s /q "%BUNDLE_DST%"
)

:: Create bundle folder structure
mkdir "%BUNDLE_DST%\Contents\net48"
mkdir "%BUNDLE_DST%\Contents\net8.0-windows" 2>nul
mkdir "%BUNDLE_DST%\Contents\net10.0-windows" 2>nul

:: Copy manifest
echo [2/4] Copying files...
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

:: Copy net10.0-windows build (AutoCAD 2027) if available.
:: Newtonsoft.Json is deliberately not copied - AutoCAD 2027 ships its own copy
:: in the program folder and a second one in the bundle can shadow it.
set "NET10=0"
if exist "%BUILD_DIR%\net10.0-windows\AutoCADMCPPlugin.dll" (
    set "NET10=1"
    copy /y "%BUILD_DIR%\net10.0-windows\AutoCADMCPPlugin.dll" "%BUNDLE_DST%\Contents\net10.0-windows\" >nul
    echo       net10.0\AutoCADMCPPlugin.dll .. OK
    if exist "%BUILD_DIR%\net10.0-windows\System.Drawing.Common.dll" (
        copy /y "%BUILD_DIR%\net10.0-windows\System.Drawing.Common.dll" "%BUNDLE_DST%\Contents\net10.0-windows\" >nul
        echo       net10.0\System.Drawing.Common OK
    )
    if exist "%BUILD_DIR%\net10.0-windows\Microsoft.Win32.SystemEvents.dll" (
        copy /y "%BUILD_DIR%\net10.0-windows\Microsoft.Win32.SystemEvents.dll" "%BUNDLE_DST%\Contents\net10.0-windows\" >nul
        echo       net10.0\Microsoft.Win32.Sys. OK
    )
    if exist "%BUILD_DIR%\net10.0-windows\System.Private.Windows.Core.dll" (
        copy /y "%BUILD_DIR%\net10.0-windows\System.Private.Windows.Core.dll" "%BUNDLE_DST%\Contents\net10.0-windows\" >nul
        echo       net10.0\System.Private.Win.  OK
    )
) else (
    rmdir "%BUNDLE_DST%\Contents\net10.0-windows" 2>nul
    echo       net10.0\AutoCADMCPPlugin.dll .. SKIPPED - not in dist
)

echo.

:: Verify installation
echo [3/4] Verifying installation...
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

:: Step 4: MCP server
echo [4/4] MCP server...
set "SERVEREXE=%SCRIPT_DIR%dist\server\autocad-mcp-server.exe"
if exist "%SERVEREXE%" (
    echo       Found: dist\server\autocad-mcp-server.exe
    echo       It is self-contained - there is nothing else to install.
    echo       Point your MCP client at that path, then run MCPSTART in AutoCAD.
) else (
    echo       Not shipped in this folder. The plugin is installed and works
    echo       either way; the MCP server is what your AI client talks to.
    echo       Build it from the repository with:
    echo           build\build-all.ps1
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
if "!NET10!"=="1" (
    echo    AutoCAD 2027      net10.0
) else (
    echo    AutoCAD 2027      NOT installed
    echo.
    echo  There is no net10.0-windows build in the dist folder, so AutoCAD 2027
    echo  is not covered by this pre-built install. To use the plugin in 2027,
    echo  install the .NET SDK 10.0 and build from source instead:
    echo.
    echo      install.bat
)
echo.
echo  Next steps:
echo    1. Open AutoCAD
echo    2. The plugin loads automatically on startup
echo    3. Type MCPSTART to start the MCP server
echo    4. Type MCPSTATUS to check it's running
echo ============================================
echo.
pause
