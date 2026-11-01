; KUDUTEXT installer (Inno Setup 6)
; Installs for the current Windows user only, so installing never asks for administrator rights.

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\KUDUTEXT"
#endif

#define MyAppName "KUDUTEXT"
#define MyAppExe "KUDUTEXT.exe"
#define MyAppUrl "https://github.com/azeembukhariprivate/kudutext"

[Setup]
AppId={{8710F7A5-8817-43A5-8960-4DB0B0CEF254}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Azeem Bukhari
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=KUDUTEXT-{#MyAppVersion}-setup
SetupIconFile=..\src\KUDUTEXT\Assets\KUDUTEXT.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=Azeem Bukhari
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "self-test.log,screenshots\*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Open KUDUTEXT now"; Flags: nowait postinstall skipifsilent

[Registry]
; Remove saved settings when uninstalling.
Root: HKCU; Subkey: "Software\KUDUTEXT"; Flags: uninsdeletekey dontcreatekey

[Code]
{ The speech models (up to 1.5 GB each) and the list of files are only removed when the person says so.
  The subtitle and text files made next to their videos are never touched. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if DirExists(ExpandConstant('{localappdata}\KUDUTEXT')) then
      if SuppressibleMsgBox('Also remove the downloaded speech models and the list of files from this PC?' + #13#10#13#10 +
        'Your subtitle and text files are kept either way. Choose No to keep the models for if you install KUDUTEXT again.',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(ExpandConstant('{localappdata}\KUDUTEXT'), True, True, True);
end;
