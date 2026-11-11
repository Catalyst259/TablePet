; Independent per-user installation of the skeletal edition. This AppId must
; remain stable for same-version reinstalls and future skeletal upgrades.
#ifndef AppVersion
  #define AppVersion "0.6.0"
#endif
#ifndef PublishDir
  #define PublishDir SourcePath + "..\dist\TablePet-" + AppVersion + "-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir SourcePath + "..\dist"
#endif
#if !FileExists(PublishDir + "\TablePet.exe")
  #error "Publish the self-contained TablePet application before building Setup."
#endif

[Setup]
AppId={{E5578DE9-62E6-4E72-8DC8-B4ED2FAB57EE}
AppName=TablePet
AppVersion={#AppVersion}
AppVerName=TablePet {#AppVersion}
AppPublisher=TablePet
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=TablePet 安装程序
DefaultDirName={localappdata}\Programs\TablePet
DefaultGroupName=TablePet
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\TablePet.exe
SetupIconFile={#PublishDir}\Assets\strawberry-cat\tray.ico
OutputDir={#OutputDir}
OutputBaseFilename=TablePet-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[InstallDelete]
; PrepareToInstall first rejects legacy destinations and stops only the
; skeletal edition. Remove obsolete skeletal bundles, never user settings.
Type: filesandordirs; Name: "{app}\Assets\strawberry-cat\skeletal"
Type: files; Name: "{app}\TablePet.Skeletal.exe"
Type: files; Name: "{app}\TablePet.Skeletal.pdb"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\TablePet"; Filename: "{app}\TablePet.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\TablePet"; Filename: "{app}\TablePet.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\TablePet.exe"; Description: "启动 TablePet"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  PetMutex = 'Local\TablePet.Skeletal.StrawberryCat';
  PetExitEvent = 'Local\TablePet.Skeletal.Exit';
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupValue = 'TablePet.Skeletal';
  CurrentUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{E5578DE9-62E6-4E72-8DC8-B4ED2FAB57EE}_is1';
  LegacyUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{261031CD-604E-4B35-AD80-CC37C782E351}_is1';

function OpenEvent(DesiredAccess: LongWord; InheritHandle: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(EventHandle: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function SameDirectory(const Left, Right: String): Boolean;
begin
  Result := CompareText(AddBackslash(ExpandFileName(Left)),
    AddBackslash(ExpandFileName(Right))) = 0;
end;

function IsCurrentProductDirectory(const Target: String): Boolean;
var
  RegisteredPath: String;
  VersionMS, VersionLS: Cardinal;
begin
  Result := False;
  if RegQueryStringValue(HKCU, CurrentUninstallKey, 'Inno Setup: App Path', RegisteredPath) then
    if (Trim(RegisteredPath) <> '') and SameDirectory(Target, RegisteredPath) then
    begin
      Result := True;
      Exit;
    end;
  { Recognize portable copies as well as registered installs. A pack alone is
    not enough to identify a TablePet.exe belonging to the historical edition. }
  if not FileExists(AddBackslash(Target) + 'Assets\strawberry-cat\skeletal\pack.json') then Exit;
  if FileExists(AddBackslash(Target) + 'TablePet.exe') then
  begin
    { VersionMS contains major/minor. The current product starts at 0.5;
      historical 0.4 executables are rejected even if a pack was copied in. }
    if GetVersionNumbers(AddBackslash(Target) + 'TablePet.exe', VersionMS, VersionLS) then
      Result := VersionMS >= $00000005;
    Exit;
  end;
  Result := FileExists(AddBackslash(Target) + 'TablePet.Skeletal.exe');
end;

function DestinationError(): String;
var
  Target, LegacyPath: String;
begin
  Result := '';
  Target := ExpandConstant('{app}');
  { Historical registrations take priority even if files have been removed. }
  if RegQueryStringValue(HKCU, LegacyUninstallKey, 'Inno Setup: App Path', LegacyPath) then
    if (Trim(LegacyPath) <> '') and SameDirectory(Target, LegacyPath) then
      Result := '此目录是历史版本草莓猫桌宠的安装位置。请为 TablePet 选择其他目录，原有程序和设置会保留。';
  if Result <> '' then Exit;
  if FileExists(AddBackslash(Target) + 'TablePet.dll') or
     (FileExists(AddBackslash(Target) + 'TablePet.exe') and not IsCurrentProductDirectory(Target)) then
    Result := '此目录包含历史版本或其他 TablePet 程序。请选择其他安装目录，原有程序和设置会保留。';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ErrorMessage: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;
  ErrorMessage := DestinationError();
  if ErrorMessage = '' then Exit;
  MsgBox(ErrorMessage, mbError, MB_OK);
  Result := False;
end;

function StopPet(): Boolean;
var
  EventHandle: THandle;
  Attempt: Integer;
begin
  Result := not CheckForMutexes(PetMutex);
  if Result then Exit;
  EventHandle := OpenEvent($0002, False, PetExitEvent);
  if EventHandle = 0 then Exit;
  try
    if not SetEvent(EventHandle) then Exit;
  finally
    CloseHandle(EventHandle);
  end;
  for Attempt := 1 to 100 do
  begin
    Sleep(100);
    if not CheckForMutexes(PetMutex) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Repeat for silent installs and upgrades where the directory page is hidden.
    Reject first, before sending any process event or deleting bundled files. }
  Result := DestinationError();
  if Result <> '' then Exit;
  if not StopPet() then
    Result := '请从系统托盘退出 TablePet，然后重试安装。个人设置会保留。';
end;

function CommandTargets(const Command, Executable: String): Boolean;
begin
  Result := (CompareText(Trim(Command), '"' + Executable + '"') = 0) or
    (CompareText(Trim(Command), Executable) = 0);
end;

procedure RemovePreviousShortcut(const ShortcutPath: String);
var
  Shell, Shortcut: Variant;
  Target, PreviousExe, CurrentExe: String;
begin
  if not FileExists(ShortcutPath) then Exit;
  PreviousExe := ExpandConstant('{app}\TablePet.Skeletal.exe');
  CurrentExe := ExpandConstant('{app}\TablePet.exe');
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(ShortcutPath);
    Target := Shortcut.TargetPath;
    if (CompareText(Target, PreviousExe) = 0) or (CompareText(Target, CurrentExe) = 0) then
    begin
      if not DeleteFile(ShortcutPath) then Log('Unable to remove previous TablePet shortcut: ' + ShortcutPath);
    end;
  except
    Log('Previous shortcut could not be inspected and was preserved: ' + ShortcutPath);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  StartupCommand, PreviousExe, CurrentExe: String;
begin
  if CurStep <> ssPostInstall then Exit;
  PreviousExe := ExpandConstant('{app}\TablePet.Skeletal.exe');
  CurrentExe := ExpandConstant('{app}\TablePet.exe');
  { Only migrate an existing enabled entry for this exact installation.
    An absent value stays absent; another portable installation is untouched. }
  if RegQueryStringValue(HKCU, StartupKey, StartupValue, StartupCommand) then
    if CommandTargets(StartupCommand, PreviousExe) then
      if not RegWriteStringValue(HKCU, StartupKey, StartupValue, '"' + CurrentExe + '"') then
        Log('Unable to migrate the TablePet startup command.');
  RemovePreviousShortcut(ExpandConstant('{userprograms}\草莓猫桌宠 · 骨骼版.lnk'));
  RemovePreviousShortcut(ExpandConstant('{userdesktop}\草莓猫桌宠 · 骨骼版.lnk'));
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not CheckForMutexes(PetMutex) then Exit;
  Result := SuppressibleMsgBox('卸载前需要退出正在运行的 TablePet。个人设置会保留。是否继续？',
    mbConfirmation, MB_YESNO, IDNO) = IDYES;
  if not Result then Exit;
  Result := StopPet();
  if not Result then
    SuppressibleMsgBox('TablePet 尚未退出。请从系统托盘退出后，再运行卸载程序。', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand, InstalledExe, PreviousExe: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  InstalledExe := ExpandConstant('{app}\TablePet.exe');
  PreviousExe := ExpandConstant('{app}\TablePet.Skeletal.exe');
  { PlatformService writes only a quoted executable path for the published
    app. Leave another skeletal/portable installation's startup value intact. }
  if RegQueryStringValue(HKCU, StartupKey, StartupValue, StartupCommand) then
    if CommandTargets(StartupCommand, InstalledExe) or CommandTargets(StartupCommand, PreviousExe) then
      RegDeleteValue(HKCU, StartupKey, StartupValue);
  { Both LocalAppData\DesktopPet.Skeletal and LocalAppData\DesktopPet remain
    outside the installation and are never deleted by this installer. }
end;
