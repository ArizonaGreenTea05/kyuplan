; Inno Setup script of the WPF client. Built by .github/workflows/release.yml (the installer ends up in the server image, which offers it for
; download and to the installed clients as their update); by hand:
;
;   dotnet publish src/Kyuplan.Client.Wpf -c Release -r win-x64 --self-contained -o artifacts/wpf
;   ISCC.exe /DAppVersion=1.2.3 /DVersionInfo=1.2.3.0 installer\Kyuplan.iss      (result: artifacts\installer)
;
; Keep this file ASCII: Inno Setup reads a script without a byte order mark in the ANSI code page.
;
; The installer does not know where the API runs: the client asks for the address itself on its first start (and can be pointed
; to another one from its login window). appsettings.json (the API key, if the API wants one) is installed once and then left alone,
; so that an update does not overwrite what was edited there.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; Inno Setup wants four numbers for the version of the Setup.exe file itself, no suffix like -beta.1.
#ifndef VersionInfo
  #define VersionInfo "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\wpf"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "Kyuplan"
#define AppExe "Kyuplan.exe"

[Setup]
; Identifies the program across versions: never change it.
AppId={{9F2D7623-74B7-45DA-A1B1-4E0136E60F3C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Kyuplan
VersionInfoVersion={#VersionInfo}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\assets\branding\app.ico
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-windows-{#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Per user by default (no administrator needed, appsettings.json stays editable); the dialog offers "for all users".
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "appsettings.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; The program updates itself by running this installer silently with /Relaunch=1 and ends; a silent setup would not start it again.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: RelaunchAfterUpdate

[Code]
function RelaunchAfterUpdate: Boolean;
begin
  Result := ExpandConstant('{param:Relaunch|0}') = '1';
end;
