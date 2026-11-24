; Instalador do Azul Groove Music  -  compile com o Inno Setup 6 (ou rode o build.bat, que faz tudo)
#define MyAppName "Azul Groove Music"
#define MyAppVersion "1.0.6"
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
#ifdef Leve
OutputBaseFilename=AzulGrooveMusic-Leve-Setup-{#MyAppVersion}
#else
OutputBaseFilename=AzulGrooveMusic-Setup-{#MyAppVersion}
#endif
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
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
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall; Check: ShouldLaunchApp

[Code]
// Instalação normal: mostra a opção "Abrir o app" no fim, como antes.
// Instalação silenciosa (atualização automática do app): abre o app de novo só quando o app pediu com /RELAUNCH=1.
function ShouldLaunchApp: Boolean;
begin
  Result := (not WizardSilent) or (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

#ifdef Leve
// Versão leve: o .NET não vai dentro do instalador, então confere se o .NET 8 Desktop Runtime (x64) está instalado.
function InitializeSetup: Boolean;
var
  FindRec: TFindRec;
  Found: Boolean;
  ResultCode: Integer;
begin
  Found := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec) then
  begin
    Found := True;
    FindClose(FindRec);
  end;
  Result := Found;
  if not Found then
  begin
    if SuppressibleMsgBox('O Azul Groove Music (versão leve) precisa do .NET 8 Desktop Runtime (x64), que não foi encontrado neste PC.' + #13#10 + #13#10 + 'Abrir a página de download agora?', mbConfirmation, MB_YESNO, IDNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0/runtime', '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;
#endif
