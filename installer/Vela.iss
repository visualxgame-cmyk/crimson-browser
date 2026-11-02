#ifndef PublishDir
  #error PublishDir must point to the self-contained win-x64 publish directory
#endif
#ifndef ReleaseDir
  #define ReleaseDir "..\artifacts\releases"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.5"
#endif
#ifndef ProductId
  #define ProductId "VelaBrowser"
#endif

[Setup]
AppId={#ProductId}
AppName=Vela
AppVersion={#AppVersion}
AppPublisher=Vela
DefaultDirName={localappdata}\Programs\Vela
DefaultGroupName=Vela
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\src\Vela\Assets\vela.ico
UninstallDisplayIcon={app}\Vela.exe
OutputDir={#ReleaseDir}
OutputBaseFilename=Vela-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
AppMutex=Local\VelaBrowser.Running
CloseApplications=no
RestartApplications=no
DisableProgramGroupPage=yes
UninstallDisplayName=Vela Browser
VersionInfoDescription=Vela Browser Setup

[Languages]
Name: "romanian"; MessagesFile: "Romanian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Creează o scurtătură pe desktop"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
#if ProductId == "VelaBrowser"
Name: "{autoprograms}\Vela"; Filename: "{app}\Vela.exe"
Name: "{autodesktop}\Vela"; Filename: "{app}\Vela.exe"; Tasks: desktopicon
#endif

[Run]
Filename: "{app}\Vela.exe"; Description: "Deschide Vela"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var RegisteredCommand: String;
begin
  if CurUninstallStep = usUninstall then begin
    // Remove associations only if this installation owns them. Never change UserChoice.
    if RegQueryStringValue(HKCU, 'Software\Classes\VelaHTML\shell\open\command', '', RegisteredCommand) and
       (RegisteredCommand = '"' + ExpandConstant('{app}\Vela.exe') + '" -- "%1"') then begin
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\VelaHTML');
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Clients\StartMenuInternet\Vela');
      RegDeleteValue(HKCU, 'Software\RegisteredApplications', 'Vela');
    end;
    // Browser profiles live outside {app} and deliberately survive uninstall/reinstall.
  end;
end;
