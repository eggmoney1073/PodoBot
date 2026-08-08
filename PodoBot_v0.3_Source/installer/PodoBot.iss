#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif

#define MyAppName "PodoBot"
#define MyAppExeName "PodoBot.exe"

[Setup]
AppId={{E7748758-5585-4EC1-B033-4995CB8B0181}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=PodoBot
DefaultDirName={localappdata}\Programs\PodoBot
DefaultGroupName=PodoBot
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=output
OutputBaseFilename=PodoBotSetup
SetupIconFile=..\src\PodoBot\Assets\PodoBot.ico
UninstallDisplayIcon={app}\PodoBot.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면 바로가기 만들기"; GroupDescription: "추가 옵션:"; Flags: unchecked
Name: "startup"; Description: "Windows 시작 시 PodoBot 자동 실행"; GroupDescription: "추가 옵션:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PodoBot"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\PodoBot"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PodoBot"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "PodoBot 실행"; Flags: nowait postinstall skipifsilent
