; Nova Enhance for Windows — per-user installer (no admin rights needed; good for company PCs).
; Built by the GitHub workflow:  ISCC.exe /DAppVersion=1.0.N /DBuildNo=N /DSourceDir=... installer.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef ShownNo
  #define ShownNo "00"
#endif
#ifndef BuildNo
  #define BuildNo "0"
#endif
#ifndef SourceDir
  #define SourceDir "out"
#endif

[Setup]
AppId={{4B7E2C1A-9D3F-4E8B-A6C2-5F1D0E9B7A33}
AppName=Nova Enhance
AppVersion={#AppVersion}
AppVerName=Nova Enhance for Windows 2.0 (build {#ShownNo})
VersionInfoVersion={#AppVersion}
AppPublisher=A.HAMROUNI
AppCopyright=Designed by A.HAMROUNI
DefaultDirName={autopf}\Nova Enhance
DefaultGroupName=Nova Enhance
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=NovaEnhance-Windows-2.0-build-{#ShownNo}-Setup
SetupIconFile=NovaEnhance\nova.ico
UninstallDisplayIcon={app}\NovaEnhance.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\NovaEnhance.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Nova Enhance"; Filename: "{app}\NovaEnhance.exe"
Name: "{autodesktop}\Nova Enhance"; Filename: "{app}\NovaEnhance.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Nova Enhance"; ValueData: """{app}\NovaEnhance.exe"" --tray"; Flags: uninsdeletevalue

Root: HKCU; Subkey: "Software\Classes\Directory\shell\NovaEnhance.Search"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\NovaEnhance.Search"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Drive\shell\NovaEnhance.Search"; Flags: uninsdeletekey

[Run]
Filename: "{app}\NovaEnhance.exe"; Description: "Start Nova Enhance"; Flags: nowait postinstall skipifsilent
Filename: "{app}\NovaEnhance.exe"; Parameters: "--tray"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM NovaEnhance.exe"; Flags: runhidden; RunOnceId: "StopNova"

[Code]
function InitializeSetup(): Boolean;
var Code: Integer;
begin
  Exec('taskkill.exe', '/F /IM NovaEnhance.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := True;
end;
