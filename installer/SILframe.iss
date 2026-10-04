#define AppVersion GetEnv("SILFRAME_APP_VERSION")
#define PublishDir GetEnv("SILFRAME_PUBLISH_DIR")

#if AppVersion == ""
  #error "SILFRAME_APP_VERSION must be set by tools/build-installer.ps1"
#endif
#if PublishDir == ""
  #error "SILFRAME_PUBLISH_DIR must be set by tools/build-installer.ps1"
#endif

[Setup]
AppId={{5D941CF2-FDF4-4AAD-A842-33DAE6AFA0B1}
AppName=SILframe
AppVersion={#AppVersion}
AppVerName=SILframe {#AppVersion}
AppPublisher=SILframe
DefaultDirName={localappdata}\Programs\SILframe
DefaultGroupName=SILframe
DisableProgramGroupPage=yes
UninstallDisplayName=SILframe
UninstallDisplayIcon={app}\SILframe.exe
SetupIconFile={#PublishDir}\Assets\SILframe.ico
PrivilegesRequired=lowest
ChangesAssociations=yes
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=yes
OutputBaseFilename=SILframe-Setup-{#AppVersion}-win-x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
english.AdditionalTasks=Optional tasks:
english.DesktopIcon=Create a desktop shortcut
english.OpenWith=Add SILframe to the Open with menu for common video formats
japanese.AdditionalTasks=追加タスク:
japanese.DesktopIcon=デスクトップにショートカットを作成する
japanese.OpenWith=一般的な動画形式の「プログラムから開く」に SILframe を追加する

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked
Name: "openwith"; Description: "{cm:OpenWith}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SILframe"; Filename: "{app}\SILframe.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall SILframe"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SILframe"; Filename: "{app}\SILframe.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\SILframe.Video"; ValueType: string; ValueName: ""; ValueData: "SILframe video"; Flags: uninsdeletekey; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\SILframe.Video\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\SILframe.exe,0"; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\SILframe.Video\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\SILframe.exe"" ""%1"""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "SILframe"; Flags: uninsdeletekey; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\SILframe.exe"" ""%1"""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".3gp"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".flv"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".m2ts"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".mpeg"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".mpg"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".mts"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".ogv"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".ts"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\SILframe.exe\SupportedTypes"; ValueType: string; ValueName: ".wmv"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.3gp\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.flv\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.mpeg\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.mpg\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.mts\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.ogv\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "SILframe.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
; Capabilities and RegisteredApplications list SILframe under Settings > Apps > Default apps.
; They only make it selectable there; no default is changed.
Root: HKCU; Subkey: "Software\SILframe"; Flags: uninsdeletekeyifempty; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "SILframe"; Flags: uninsdeletekey; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Video player with frame stepping, frame capture, and simple editing"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".3gp"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avi"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".flv"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m2ts"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpeg"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpg"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mts"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogv"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ts"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\SILframe\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wmv"; ValueData: "SILframe.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "SILframe"; ValueData: "Software\SILframe\Capabilities"; Flags: uninsdeletevalue; Tasks: openwith

[Run]
Filename: "{app}\SILframe.exe"; Description: "Launch SILframe"; Flags: postinstall nowait skipifsilent
