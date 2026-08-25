#ifndef AppVersion
  #error AppVersion is required.
#endif

#ifndef SourceDir
  #error SourceDir is required.
#endif

#ifndef OutputDir
  #error OutputDir is required.
#endif

#define AppName "mySQLPunk"
#define AppExeName "mySQLPunk.exe"
#define RepositoryUrl "https://github.com/shadowjohn/mySQLPunk"

[Setup]
AppId={{B6F02DBB-A4AF-495F-B7F1-7E2AF6A80B38}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=mySQLPunk
AppPublisherURL={#RepositoryUrl}
AppSupportURL={#RepositoryUrl}/issues
AppUpdatesURL={#RepositoryUrl}/releases
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=mySQLPunk-{#AppVersion}-win-x64-setup
SetupIconFile=..\mySQLPunk\punky.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#AppVersion}
VersionInfoCompany=mySQLPunk
VersionInfoDescription=mySQLPunk database management tool setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
LicenseFile={#SourceDir}\LICENSE.txt

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
