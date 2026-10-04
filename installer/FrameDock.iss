#define AppVersion GetEnv("FRAMEDOCK_APP_VERSION")
#define PublishDir GetEnv("FRAMEDOCK_PUBLISH_DIR")

#if AppVersion == ""
  #error "FRAMEDOCK_APP_VERSION must be set by tools/build-installer.ps1"
#endif
#if PublishDir == ""
  #error "FRAMEDOCK_PUBLISH_DIR must be set by tools/build-installer.ps1"
#endif

[Setup]
AppId={{F0C84929-7404-493D-A4AE-AF087434DFB6}
AppName=FrameDock
AppVersion={#AppVersion}
AppVerName=FrameDock {#AppVersion}
AppPublisher=FrameDock
DefaultDirName={localappdata}\Programs\FrameDock
DefaultGroupName=FrameDock
DisableProgramGroupPage=yes
UninstallDisplayName=FrameDock
UninstallDisplayIcon={app}\FrameDock.exe
SetupIconFile={#PublishDir}\Assets\FrameDock.ico
PrivilegesRequired=lowest
ChangesAssociations=yes
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=yes
OutputBaseFilename=FrameDock-Setup-{#AppVersion}-win-x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
english.AdditionalTasks=Optional tasks:
english.DesktopIcon=Create a desktop shortcut
english.OpenWith=Add FrameDock to the Open with menu for common video formats
japanese.AdditionalTasks=追加タスク:
japanese.DesktopIcon=デスクトップにショートカットを作成する
japanese.OpenWith=一般的な動画形式の「プログラムから開く」に FrameDock を追加する

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked
Name: "openwith"; Description: "{cm:OpenWith}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\FrameDock"; Filename: "{app}\FrameDock.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall FrameDock"; Filename: "{uninstallexe}"
Name: "{autodesktop}\FrameDock"; Filename: "{app}\FrameDock.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\FrameDock.Video"; ValueType: string; ValueName: ""; ValueData: "FrameDock video"; Flags: uninsdeletekey; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\FrameDock.Video\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\FrameDock.exe,0"; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\FrameDock.Video\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\FrameDock.exe"" ""%1"""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "FrameDock"; Flags: uninsdeletekey; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\FrameDock.exe"" ""%1"""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".3gp"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".flv"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".m2ts"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".mpeg"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".mpg"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".mts"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".ogv"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".ts"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\Applications\FrameDock.exe\SupportedTypes"; ValueType: string; ValueName: ".wmv"; ValueData: ""; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.3gp\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.flv\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.mpeg\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.mpg\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.mts\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.ogv\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKCU; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "FrameDock.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
; Capabilities and RegisteredApplications list FrameDock under Settings > Apps > Default apps.
; They only make it selectable there; no default is changed.
Root: HKCU; Subkey: "Software\FrameDock"; Flags: uninsdeletekeyifempty; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "FrameDock"; Flags: uninsdeletekey; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Video player with frame stepping, frame capture, and simple editing"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".3gp"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avi"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".flv"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m2ts"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpeg"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpg"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mts"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogv"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ts"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\FrameDock\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wmv"; ValueData: "FrameDock.Video"; Tasks: openwith
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "FrameDock"; ValueData: "Software\FrameDock\Capabilities"; Flags: uninsdeletevalue; Tasks: openwith

[Run]
Filename: "{app}\FrameDock.exe"; Description: "Launch FrameDock"; Flags: postinstall nowait skipifsilent
