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

:: Step 3: Python packages for the MCP server
echo [3/4] Checking the Python packages the MCP server needs...
set "REQ=%~dp0src\mcp_server\requirements.txt"
set "PY="
if not exist "%REQ%" (
    echo       requirements.txt was not found at:
    echo         %REQ%
    echo       Nothing to check. It lives in the repository under
    echo       autocad-plugin\src\mcp_server if you installed from a zip.
    goto :pydone
)
python -c "import sys" >nul 2>&1 && set "PY=python"
if not defined PY (
    py -3 -c "import sys" >nul 2>&1 && set "PY=py -3"
)
if not defined PY (
    echo       No Python on PATH, so the packages could not be checked. The
    echo       plugin itself is installed and works - it is the Python MCP
    echo       server that needs them. Install Python 3.10 or newer, then:
    echo           pip install -r "%REQ%"
    goto :pydone
)
set "MISSING="
%PY% -c "import mcp" >nul 2>&1 || set "MISSING=%MISSING% mcp"
%PY% -c "import openpyxl" >nul 2>&1 || set "MISSING=%MISSING% openpyxl"
%PY% -c "import pikepdf" >nul 2>&1 || set "MISSING=%MISSING% pikepdf"
if not defined MISSING (
    echo       mcp, openpyxl, pikepdf - all present.
    goto :pydone
)
echo       Missing:%MISSING%
echo.
echo       mcp is required - without it the MCP server does not start at all.
echo       openpyxl backs create_table_from_excel, and pikepdf crops a plotted
echo       PDF down to the window that was plotted. Without those two, the
echo       server still runs and reports the affected feature as unavailable.
echo.
set "ANS="
set /p ANS="      Install them now with pip? [y/N] "
if /i not "%ANS%"=="y" (
    echo       Skipped. To do it later:
    echo           pip install -r "%REQ%"
    goto :pydone
)
echo.
%PY% -m pip install -r "%REQ%"
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo       pip failed. The plugin is installed either way; run this by hand:
    echo           pip install -r "%REQ%"
    goto :pydone
)
echo.
echo       Done.
:pydone
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
