; Independent per-user installation of the skeletal edition. This AppId must
; remain stable for same-version reinstalls and future skeletal upgrades.
#ifndef AppVersion
  #define AppVersion "0.5.0"
#endif
#ifndef PublishDir
  #define PublishDir SourcePath + "..\dist\TablePet.Skeletal-" + AppVersion + "-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir SourcePath + "..\dist"
#endif
#if !FileExists(PublishDir + "\TablePet.Skeletal.exe")
  #error "Publish the self-contained skeletal edition before building Setup."
#endif

[Setup]
AppId={{E5578DE9-62E6-4E72-8DC8-B4ED2FAB57EE}
AppName=草莓猫桌宠 · 骨骼版
AppVersion={#AppVersion}
AppVerName=草莓猫桌宠 · 骨骼版 {#AppVersion}
AppPublisher=TablePet
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=草莓猫桌宠 · 骨骼版安装程序
DefaultDirName={localappdata}\Programs\TablePet.Skeletal
DefaultGroupName=草莓猫桌宠 · 骨骼版
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\TablePet.Skeletal.exe
SetupIconFile={#PublishDir}\Assets\strawberry-cat\tray.ico
OutputDir={#OutputDir}
OutputBaseFilename=TablePet.Skeletal-{#AppVersion}-Setup
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

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\草莓猫桌宠 · 骨骼版"; Filename: "{app}\TablePet.Skeletal.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\草莓猫桌宠 · 骨骼版"; Filename: "{app}\TablePet.Skeletal.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\TablePet.Skeletal.exe"; Description: "启动草莓猫桌宠 · 骨骼版"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  PetMutex = 'Local\TablePet.Skeletal.StrawberryCat';
  PetExitEvent = 'Local\TablePet.Skeletal.Exit';
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupValue = 'TablePet.Skeletal';
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

function DestinationError(): String;
var
  Target, LegacyPath: String;
begin
  Result := '';
  Target := ExpandConstant('{app}');
  { Assets/manifest paths are shared between editions. Guard custom legacy
    locations as well as the default, even if its executable was removed. }
  if FileExists(AddBackslash(Target) + 'TablePet.exe') or
     FileExists(AddBackslash(Target) + 'TablePet.dll') or
     SameDirectory(Target, ExpandConstant('{localappdata}\Programs\TablePet')) then
    Result := '此目录属于原版草莓猫桌宠。请为骨骼版选择独立目录，例如 TablePet.Skeletal；原版安装和设置会保留。';
  if Result <> '' then Exit;
  if RegQueryStringValue(HKCU, LegacyUninstallKey, 'Inno Setup: App Path', LegacyPath) then
    if (Trim(LegacyPath) <> '') and SameDirectory(Target, LegacyPath) then
      Result := '此目录是已安装原版草莓猫桌宠的位置。请为骨骼版选择独立目录。';
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
    Result := '请从系统托盘退出草莓猫桌宠 · 骨骼版，然后重试安装。个人设置会保留。';
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not CheckForMutexes(PetMutex) then Exit;
  Result := SuppressibleMsgBox('卸载前需要退出正在运行的草莓猫桌宠 · 骨骼版。个人设置会保留。是否继续？',
    mbConfirmation, MB_YESNO, IDNO) = IDYES;
  if not Result then Exit;
  Result := StopPet();
  if not Result then
    SuppressibleMsgBox('骨骼版草莓猫尚未退出。请从系统托盘退出后，再运行卸载程序。', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand, InstalledExe: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  InstalledExe := ExpandConstant('{app}\TablePet.Skeletal.exe');
  { PlatformService writes only a quoted executable path for the published
    app. Leave another skeletal/portable installation's startup value intact. }
  if RegQueryStringValue(HKCU, StartupKey, StartupValue, StartupCommand) then
    if (CompareText(Trim(StartupCommand), '"' + InstalledExe + '"') = 0) or
       (CompareText(Trim(StartupCommand), InstalledExe) = 0) then
      RegDeleteValue(HKCU, StartupKey, StartupValue);
  { Both LocalAppData\DesktopPet.Skeletal and LocalAppData\DesktopPet remain
    outside the installation and are never deleted by this installer. }
end;
