; Instalador do Azul Groove Music  -  compile com o Inno Setup 6 (ou rode o build.bat, que faz tudo)
#define MyAppName "Azul Groove Music"
#define MyAppVersion "1.0.4"
#define MyAppPublisher "ASTM Software"
#define MyAppExeName "AzulGrooveMusic.exe"

[Setup]
LicenseFile=LICENSE.txt
AppId={{7B1E5C3A-92D4-4F6B-8A1D-3C5E9F2B6A40}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Azul Groove Music
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=installer
OutputBaseFilename=AzulGrooveMusic-Setup-{#MyAppVersion}
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName} (modo seguro)"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--safe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
