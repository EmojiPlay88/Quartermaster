; Inno Setup 6 script for the per-user Quartermaster installer (unsigned; Windows SmartScreen may warn).
; Built by build/windows/make-windows-packages.ps1, which passes /DAppVersion=, /DSourceDir= and /DOutputDir=.
; The install folder (%LOCALAPPDATA%\Programs\Quartermaster) is NOT the data folder (%LOCALAPPDATA%\Quartermaster):
; uninstalling never touches user data.
#ifndef AppVersion
  #error AppVersion not defined (pass /DAppVersion=x.y.z)
#endif
#ifndef SourceDir
  #error SourceDir not defined (pass /DSourceDir=<staged publish folder>)
#endif
#ifndef OutputDir
  #error OutputDir not defined (pass /DOutputDir=<folder>)
#endif

[Setup]
AppId={{6F0B6C2E-9C1F-4E8E-9C55-5C5E0A1D2B7E}
AppName=Quartermaster
AppVersion={#AppVersion}
AppPublisher=Quartermaster contributors
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Quartermaster
DefaultGroupName=Quartermaster
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Quartermaster.exe
OutputDir={#OutputDir}
OutputBaseFilename=Quartermaster-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Quartermaster"; Filename: "{app}\Quartermaster.exe"
Name: "{autodesktop}\Quartermaster"; Filename: "{app}\Quartermaster.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Quartermaster.exe"; Description: "Launch Quartermaster"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
; Files the in-app updater added, and its backups. Never the data folder.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\Programs\Quartermaster.backup-*"
