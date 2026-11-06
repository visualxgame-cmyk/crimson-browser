; Crimson Browser — Inno Setup 6/7 installer script.
; Build with:  ISCC.exe installer\CrimsonBrowser.iss
; The published application files must exist in ..\dist\publish first
; (build-installer.ps1 does this automatically).

#define MyAppName "Crimson Browser"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "VisualX Studio"
#define MyAppExeName "CrimsonBrowser.exe"
#define MyAppId "{{63B6F537-ADAB-40C0-A6CB-D42BE3873E59}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=CrimsonBrowser-Setup-{#MyAppVersion}
SetupIconFile=..\crimson.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
AppMutex=Local\CrimsonBrowser.SingleInstance
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "romanian"; MessagesFile: "languages\Romanian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: dontcopy deleteafterinstall

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Silent updates pass /CRIMSONRESTART so the updated browser starts again automatically.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: ShouldRestartAfterSilentInstall

[Code]
function ShouldRestartAfterSilentInstall: Boolean;
begin
  Result := WizardSilent and
    (Pos('/crimsonrestart', LowerCase(GetCmdTail)) > 0);
end;

function VisualCppInstalled: Boolean;
var
  Installed, Minor: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed)
    and (Installed = 1)
    and RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Minor', Minor)
    and (Minor >= 29);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if VisualCppInstalled then Exit;
  ExtractTemporaryFile('vc_redist.x64.exe');
  if not Exec(ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := 'Could not start the Microsoft Visual C++ x64 Runtime installer.'
  else if (ResultCode <> 0) and (ResultCode <> 1638) and (ResultCode <> 3010) then
    Result := 'Microsoft Visual C++ x64 Runtime installation failed (' + IntToStr(ResultCode) + ').'
  else if not VisualCppInstalled then
    Result := 'Microsoft Visual C++ 2019 or newer (x64) is required to run Crimson Browser.';
  if ResultCode = 3010 then NeedsRestart := True;
end;
