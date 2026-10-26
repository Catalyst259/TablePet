; Build with tools/build-installer.ps1. The payload must be a versioned publish folder.
#ifndef AppVersion
  #define AppVersion "0.2.1"
#endif
#ifndef PublishDir
  #define PublishDir SourcePath + "..\dist\TablePet-" + AppVersion + "-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir SourcePath + "..\dist"
#endif

[Setup]
AppId={{261031CD-604E-4B35-AD80-CC37C782E351}
AppName=草莓猫桌宠
AppVersion={#AppVersion}
AppVerName=草莓猫桌宠 {#AppVersion}
AppPublisher=TablePet
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=草莓猫桌宠安装程序
DefaultDirName={localappdata}\Programs\TablePet
DefaultGroupName=草莓猫桌宠
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\TablePet.exe
SetupIconFile=..\assets\strawberry-cat\tray.ico
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

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "README.md,docs\INSTALLATION.md"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\INSTALLATION.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\草莓猫桌宠"; Filename: "{app}\TablePet.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\草莓猫桌宠"; Filename: "{app}\TablePet.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\TablePet.exe"; Description: "启动草莓猫桌宠"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  PetMutex = 'Local\TablePet.StrawberryCat';
  PetExitEvent = 'Local\TablePet.Exit';
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function OpenEvent(DesiredAccess: LongWord; InheritHandle: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(EventHandle: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

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
  Result := '';
  if not StopPet() then
    Result := '请从系统托盘退出草莓猫桌宠，然后重试安装。设置会保留。';
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not CheckForMutexes(PetMutex) then Exit;
  Result := SuppressibleMsgBox('卸载前需要退出正在运行的草莓猫桌宠。个人设置会保留。是否继续？',
    mbConfirmation, MB_YESNO, IDNO) = IDYES;
  if not Result then Exit;
  Result := StopPet();
  if not Result then
    SuppressibleMsgBox('草莓猫尚未退出。请从系统托盘退出后，再运行卸载程序。', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand, InstalledExe: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  InstalledExe := ExpandConstant('{app}\TablePet.exe');
  if RegQueryStringValue(HKCU, StartupKey, 'TablePet', StartupCommand) then
    if (CompareText(Trim(StartupCommand), '"' + InstalledExe + '"') = 0) or
       (CompareText(Trim(StartupCommand), InstalledExe) = 0) then
      RegDeleteValue(HKCU, StartupKey, 'TablePet');
  { Settings in LocalAppData\DesktopPet are intentionally outside the installation. }
end;
