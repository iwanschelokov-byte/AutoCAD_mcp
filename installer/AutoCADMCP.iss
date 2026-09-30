; ============================================================================
;  AutoCAD MCP Plugin - Inno Setup installer
;
;  Deploys the plugin bundle into the AutoCAD ApplicationPlugins folder so it
;  autoloads on startup, and optionally installs the MCP server executable.
;
;  Installs for the current user by default (%APPDATA%\Autodesk\ApplicationPlugins);
;  pick "all users" in the first dialog for a machine-wide install into
;  %ProgramData%, which needs administrator rights.
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
  #define AppVersion   "2.0.3"
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
; Ask who the install is for, and default to the current user.
;
; A machine-wide install puts the bundle in %ProgramData%, which AutoCAD is
; documented to scan - but not every machine does: a group policy or a locked
; down profile can leave that folder unread, and the symptom is silent. Files
; are in place, APPAUTOLOAD is on, and MCPSTART is still an unknown command.
; The per-user folder under %APPDATA% is read in every configuration seen so
; far, and it needs no administrator, so that is the default. Choosing "all
; users" in the dialog still installs machine-wide.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
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
; --- Plugin bundle -> ApplicationPlugins ---
; {autoappdata} follows the install mode: %APPDATA% for this user, %ProgramData%
; for all users. Both are AutoCAD plugin locations; the per-user one is the one
; that works everywhere.
Source: "{#BundleDir}\PackageContents.xml"; \
    DestDir: "{autoappdata}\Autodesk\ApplicationPlugins\{#BundleName}"; \
    Components: plugin; Flags: ignoreversion

Source: "{#BundleDir}\Contents\*"; \
    DestDir: "{autoappdata}\Autodesk\ApplicationPlugins\{#BundleName}\Contents"; \
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
Type: filesandordirs; Name: "{autoappdata}\Autodesk\ApplicationPlugins\{#BundleName}"

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
             ExpandConstant('{app}\server\autocad-mcp-server.exe') + #13#10#13#10 +
             'The plugin itself went to:' + #13#10 +
             ExpandConstant('{autoappdata}\Autodesk\ApplicationPlugins\{#BundleName}') +
             #13#10#13#10 +
             'If MCPSTART comes back as an unknown command, this AutoCAD is not ' +
             'reading that folder. Copy the bundle folder above into' + #13#10 +
             ExpandConstant('{userappdata}\Autodesk\ApplicationPlugins') + #13#10 +
             'and restart AutoCAD.',
             mbInformation, MB_OK);
  end;
end;
