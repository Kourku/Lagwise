; Inno Setup Script for NetPulse
#define MyAppName "NetPulse"
#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"  ; build.ps1 passes /DMyAppVersion=<version from Directory.Build.props>
#endif
#define MyAppPublisher "NetPulse Diagnostics"
#define MyAppExeName "NetPulse.exe"

[Setup]
AppId={{9B78D8D3-48FA-46D1-9871-FE748D80F125}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=NetPulse-{#MyAppVersion}-InnoSetup
SetupIconFile=..\src\NetPulse.App\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\portable\NetPulse.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\NetPulse.App\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
