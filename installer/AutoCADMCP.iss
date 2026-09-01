; ============================================================================
;  AutoCAD MCP Plugin - Inno Setup installer
;
;  Deploys the plugin bundle into the AutoCAD ApplicationPlugins folder so it
;  autoloads on startup, and optionally installs the MCP server executable.
;
;  Build with:
;      iscc installer\AutoCADMCP.iss
;  after staging the bundle:
;      .\build\build-all.ps1
;
;  Silent install for IT deployment:
;      AutoCADMCP-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
; ============================================================================

#define AppName        "AutoCAD MCP Plugin"

; The release workflow passes the tag through as iscc /DAppVersion=x.y.z. A bare
; #define here would run afterwards and overwrite it, which is how 2.0.1 shipped
; an installer named 2.0.0 that also registered itself as 2.0.0 in Programs and
; Features. Guarded, the command line wins and a local `iscc` with no arguments
; still builds.
#ifndef AppVersion
  #define AppVersion   "2.0.2"
#endif
#define AppPublisher   "AutoCAD MCP"
#define BundleName     "AutoCADMCPPlugin.bundle"

; Staged by build\build-all.ps1
#define BundleDir      "..\autocad-plugin\dist\AutoCADMCPPlugin.bundle"
#define ServerDir      "..\autocad-plugin\dist\server"

[Setup]
AppId={{7E3A1C90-4C2B-4E1D-9E5A-2B6F8D41A7C3}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\AutoCADMCP
DefaultGroupName={#AppName}
DisableDirPage=no
DisableProgramGroupPage=yes
OutputDir=..\autocad-plugin\dist
OutputBaseFilename=AutoCADMCP-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
; Per-machine install so every user profile picks the plugin up.
PrivilegesRequired=admin
UninstallDisplayName={#AppName} {#AppVersion}
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full";   Description: "Plugin and MCP server"
Name: "plugin"; Description: "AutoCAD plugin only"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "plugin"; Description: "AutoCAD plugin (autoloads in AutoCAD 2021-2027)"; \
    Types: full plugin custom; Flags: fixed
Name: "server"; Description: "MCP server (self-contained, nothing else to install)"; \
    Types: full custom

[Files]
; --- Plugin bundle -> ApplicationPlugins (all users) ---
; {commonappdata}\Autodesk\ApplicationPlugins is scanned by every AutoCAD release.
Source: "{#BundleDir}\PackageContents.xml"; \
    DestDir: "{commonappdata}\Autodesk\ApplicationPlugins\{#BundleName}"; \
    Components: plugin; Flags: ignoreversion

Source: "{#BundleDir}\Contents\*"; \
    DestDir: "{commonappdata}\Autodesk\ApplicationPlugins\{#BundleName}\Contents"; \
    Components: plugin; Flags: ignoreversion recursesubdirs createallsubdirs

; --- MCP server ---
; One self-contained executable staged by build\build-all.ps1. There is no
; runtime to install, which is why this component no longer has a prerequisite.
Source: "{#ServerDir}\*"; DestDir: "{app}\server"; Excludes: "*.pdb"; \
    Components: server; Flags: ignoreversion recursesubdirs createallsubdirs

; --- Docs ---
Source: "..\README.md";    DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\AutoCAD MCP README"; Filename: "{app}\README.md"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Run]
; Confirms the server runs and reports whether it can already see AutoCAD.
Filename: "{app}\server\autocad-mcp-server.exe"; Parameters: "--check"; \
    Description: "Check the MCP server can reach AutoCAD"; \
    Components: server; Flags: postinstall skipifsilent unchecked

[UninstallDelete]
; Remove the bundle folder itself; Inno only tracks the files it copied.
Type: filesandordirs; Name: "{commonappdata}\Autodesk\ApplicationPlugins\{#BundleName}"

[Code]

{ Refuse to install while AutoCAD is running - the bundle DLLs would be locked
  and the install would half-apply. }
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if Exec('cmd.exe', '/c tasklist /FI "IMAGENAME eq acad.exe" | find /I "acad.exe"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
    begin
      if not WizardSilent() then
        MsgBox('AutoCAD is currently running.' + #13#10#13#10 +
               'Close all AutoCAD windows and run this installer again.',
               mbError, MB_OK);
      Result := False;
    end;
  end;
end;

{ Report which AutoCAD releases were detected, so the user can tell whether the
  matching plugin leg is present. }
function DetectedAutoCADVersions(): String;
var
  Years: array[0..6] of String;
  I: Integer;
  Found: String;
begin
  Years[0] := '2021'; Years[1] := '2022'; Years[2] := '2023'; Years[3] := '2024';
  Years[4] := '2025'; Years[5] := '2026'; Years[6] := '2027';
  Found := '';
  for I := 0 to 6 do
  begin
    if DirExists(ExpandConstant('{commonpf}\Autodesk\AutoCAD ' + Years[I])) then
    begin
      if Found <> '' then Found := Found + ', ';
      Found := Found + Years[I];
    end;
  end;
  Result := Found;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Versions: String;
begin
  if CurStep = ssPostInstall then
  begin
    Versions := DetectedAutoCADVersions();
    if (Versions = '') and (not WizardSilent()) then
      MsgBox('No AutoCAD installation was detected.' + #13#10#13#10 +
             'The plugin has been installed and will load automatically once ' +
             'AutoCAD 2021-2027 is installed.', mbInformation, MB_OK)
    else if not WizardSilent() then
      MsgBox('Installed for AutoCAD: ' + Versions + #13#10#13#10 +
             'Start AutoCAD and type MCPSTART to begin.' + #13#10#13#10 +
             'Point your MCP client at:' + #13#10 +
             ExpandConstant('{app}\server\autocad-mcp-server.exe'),
             mbInformation, MB_OK);
  end;
end;
