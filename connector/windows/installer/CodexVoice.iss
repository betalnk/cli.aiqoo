; Build with Build-Installer.ps1. Model paths are passed by the build script.
#ifndef PublishDir
  #error PublishDir must be defined by Build-Installer.ps1
#endif
#ifndef ModelsRoot
  #error ModelsRoot must be defined by Build-Installer.ps1
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir must be defined by Build-Installer.ps1
#endif
#ifndef AppVersion
  #error AppVersion must be defined by Build-Installer.ps1
#endif

#define AppName "Codex Voice"
#define AppExe "CodexVoice.exe"

[Setup]
AppId={{7056D261-406B-467A-B4CB-BEDDA8CD61B7}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=betalnk
DefaultDirName={localappdata}\Programs\CodexVoice
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
SetupIconFile=..\assets\codex-voice.ico
UninstallDisplayIcon={app}\{#AppExe}
OutputDir={#InstallerOutputDir}
OutputBaseFilename=CodexVoice-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ModelsRoot}\ggml-largev3turbo-q5_0.bin"; DestDir: "{app}\models"; Flags: ignoreversion
Source: "{#ModelsRoot}\vosk-model-small-ru-0.22\*"; DestDir: "{app}\models\vosk-model-small-ru-0.22"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD_PARTY.md"; DestDir: "{app}\notices"; Flags: ignoreversion
Source: "..\..\..\LICENSE"; DestDir: "{app}\notices"; DestName: "SOURCE-LICENSE.txt"; Flags: ignoreversion
Source: "notices\*"; DestDir: "{app}\notices\licenses"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--models-root ""{app}\models"""; WorkingDir: "{app}\models"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--models-root ""{app}\models"""; WorkingDir: "{app}\models"
