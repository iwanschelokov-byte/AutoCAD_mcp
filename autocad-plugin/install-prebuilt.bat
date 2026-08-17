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

:: Step 4: Python packages for the MCP server
echo [4/4] Checking the Python packages the MCP server needs...
set "REQ=%SCRIPT_DIR%src\mcp_server\requirements.txt"
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
