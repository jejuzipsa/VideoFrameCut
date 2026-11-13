#define AppName "FrameCut"
#define AppVersion "0.2.0"
#define AppExe "FrameCut.exe"

[Setup]
AppId={{D3CF5EC4-6A87-4C7E-AB06-074AE6FA93C8}
AppName={#AppName}
AppVerName={#AppName} {#AppVersion} Portable
AppVersion={#AppVersion}
AppPublisher=FrameCut
AppCopyright=FrameCut Contributors
DefaultDirName={localappdata}\Programs\FrameCut
DefaultGroupName=FrameCut
UsePreviousAppDir=no
PrivilegesRequired=lowest
Uninstallable=no
CreateUninstallRegKey=no
DisableProgramGroupPage=yes
DisableDirPage=no
DisableReadyMemo=yes
DisableReadyPage=yes
DirExistsWarning=no
WizardStyle=modern
MinVersion=10.0
ArchitecturesAllowed=x64compatible
OutputDir=output
OutputBaseFilename=FrameCut-Portable-Setup-win-x64
SetupIconFile=..\src\VideoFrameCut.App\Assets\FrameCut.ico
Compression=lzma2/ultra64
SolidCompression=yes

[Files]
; Portable self-contained .NET + LibVLC, without global PATH/registry changes.
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{app}\{#AppExe}"; Description: "FrameCut 실행"; Flags: nowait postinstall skipifsilent
