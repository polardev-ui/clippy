; Clippy Windows installer — compile with Inno Setup 6
;   ISCC.exe Clippy.iss /DPublishDir=..\build\publish /DBuildDir=..\build

#ifndef PublishDir
  #define PublishDir "..\build\publish"
#endif

#ifndef BuildDir
  #define BuildDir "..\build"
#endif

#define MyAppName "Clippy"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "Clippy"
#define MyAppURL "https://clippy.asia"
#define MyAppExeName "Clippy.exe"

[Setup]
AppId={{B8F4E2A1-9C3D-4F5E-A6B7-CL1PPYWIN01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#BuildDir}
OutputBaseFilename=ClippySetup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}

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

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nClippy buffers your screen in the background and saves instant clips with Ctrl+K or voice commands.%n%nNote: FFmpeg must be installed separately and available on PATH for recording to work.
