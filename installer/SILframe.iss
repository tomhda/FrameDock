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
ChangesEnvironment=yes
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
english.AddToPath=Add the SILframe folder to PATH, to start silframe-cli by name (for scripts and AI agents)
japanese.AdditionalTasks=追加タスク:
japanese.DesktopIcon=デスクトップにショートカットを作成する
japanese.OpenWith=一般的な動画形式の「プログラムから開く」に SILframe を追加する
japanese.AddToPath=SILframe のフォルダーを PATH に追加し、silframe-cli を名前だけで実行できるようにする（スクリプトや AI エージェント向け）

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked
Name: "openwith"; Description: "{cm:OpenWith}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked
Name: "addtopath"; Description: "{cm:AddToPath}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked

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

[Code]
// The optional "addtopath" task puts the install folder on the current user's
// PATH so that silframe-cli can be started by name. Uninstalling takes it out
// again. Other entries of PATH are left exactly as they are.

const
  EnvironmentKey = 'Environment';

function SameFolder(const A, B: string): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(Trim(A)), RemoveBackslashUnlessRoot(Trim(B))) = 0;
end;

// Returns Paths without the entries equal to Folder, and whether any was found.
function WithoutFolder(const Paths, Folder: string; var Found: Boolean): string;
var
  Rest, Entry: string;
  Separator: Integer;
begin
  Result := '';
  Found := False;
  Rest := Paths;
  while Rest <> '' do
  begin
    Separator := Pos(';', Rest);
    if Separator = 0 then
    begin
      Entry := Rest;
      Rest := '';
    end
    else
    begin
      Entry := Copy(Rest, 1, Separator - 1);
      Rest := Copy(Rest, Separator + 1, Length(Rest));
    end;

    if Trim(Entry) = '' then
      Continue;

    if SameFolder(Entry, Folder) then
      Found := True
    else
    begin
      if Result <> '' then
        Result := Result + ';';
      Result := Result + Entry;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Paths, Remaining: string;
  Found: Boolean;
begin
  if (CurStep <> ssPostInstall) or not WizardIsTaskSelected('addtopath') then
    Exit;

  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Paths) then
    Paths := '';
  Remaining := WithoutFolder(Paths, ExpandConstant('{app}'), Found);
  if Found then
    Exit;

  if Remaining <> '' then
    Remaining := Remaining + ';';
  RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Remaining + ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Paths, Remaining: string;
  Found: Boolean;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Paths) then
    Exit;
  Remaining := WithoutFolder(Paths, ExpandConstant('{app}'), Found);
  if Found then
    RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Remaining);
end;
