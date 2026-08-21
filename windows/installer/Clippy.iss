; Clippy Windows installer — compile with Inno Setup 6
;   ISCC.exe Clippy.iss /DPublishDir=..\build\publish\x64 /DBuildDir=..\build /DTargetArch=x64

#ifndef PublishDir
  #define PublishDir "..\build\publish\x64"
#endif

#ifndef BuildDir
  #define BuildDir "..\build"
#endif

#ifndef SourceRoot
  #define SourceRoot "..\Clippy"
#endif

#ifndef TargetArch
  #define TargetArch "x64"
#endif

#define MyAppName "Clippy"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "Clippy"
#define MyAppURL "https://clippy.asia"
#define MyAppExeName "Clippy.exe"

; The payload is a self-contained native build, so each installer accepts only its own
; architecture. Without this an ARM64 package would install happily on an x64 PC and then
; fail to launch.
#if TargetArch == "arm64"
  #define ArchAllowed "arm64"
  #define ArchLabel "ARM64"
#else
  #define ArchAllowed "x64compatible"
  #define ArchLabel "64-bit"
#endif

[Setup]
AppId={{B8F4E2A1-9C3D-4F5E-A6B7-CL1PPYWIN01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion} ({#ArchLabel})
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#BuildDir}
OutputBaseFilename=ClippySetup-{#TargetArch}
SetupIconFile={#SourceRoot}\Assets\clippy-icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
MinVersion=10.0.19041
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Rolling buffer segments are scratch data and can be large; leave saved clips alone.
Type: filesandordirs; Name: "{localappdata}\Clippy\Buffer"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nClippy buffers your screen in the background and saves instant clips with Ctrl+K or voice commands.%n%nEverything you need is included — no extra setup required.
