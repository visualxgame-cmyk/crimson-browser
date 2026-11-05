; Crimson Browser — Inno Setup 6/7 installer script.
; Build with:  ISCC.exe installer\CrimsonBrowser.iss
; The published application files must exist in ..\dist\publish first
; (build-installer.ps1 does this automatically).

#define MyAppName "Crimson Browser"
#define MyAppVersion "1.0.0"
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
Compression=lzma2/ultra64
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
Source: "redist\MicrosoftEdgeWebView2Setup.exe"; DestDir: "{tmp}"; Flags: dontcopy deleteafterinstall

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function WebView2Installed: Boolean;
begin
  Result :=
    RegKeyExists(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2Client) or
    RegKeyExists(HKLM, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2Client) or
    RegKeyExists(HKLM, 'Software\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2Client);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';

  if WebView2Installed then
    Exit;

  ExtractTemporaryFile('MicrosoftEdgeWebView2Setup.exe');

  if (not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebView2Setup.exe'),
               '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or
     (ResultCode <> 0) then
  begin
    Result :=
      'Microsoft Edge WebView2 Runtime could not be installed automatically.' + #13#10#13#10 +
      'Crimson Browser needs the WebView2 Runtime to display web pages. ' +
      'Install it from the link below, then run this installer again:' + #13#10 +
      'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
    Exit;
  end;

  if not WebView2Installed then
    Result :=
      'Microsoft Edge WebView2 Runtime is still not available.' + #13#10#13#10 +
      'Install it from the link below, then run this installer again:' + #13#10 +
      'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
end;
