#ifndef BeamVersion
  #define BeamVersion "0.1.0-alpha.3"
#endif
#ifndef PublishDirectory
  #error PublishDirectory must be provided by Publish-BeamInstaller.ps1
#endif
#ifndef BeamFileVersion
  #define BeamFileVersion "0.1.0.0"
#endif

#ifdef BeamTestId
  #ifndef BeamTestRoot
    #error BeamTestRoot is required with BeamTestId
  #endif
  #define BeamAppId "Beam.InstallerTest." + BeamTestId
  #define BeamAppName "Beam Installer Test " + BeamTestId
  #define BeamMutex "Local\Beam.InstallerTest." + BeamTestId
  #define BeamRunKey "Software\Beam\InstallerTests\" + BeamTestId + "\Run"
  #define BeamDefaultDirectory BeamTestRoot + "\Installed app with spaces"
  #define BeamStartMenu BeamTestRoot + "\Shortcuts\Start Menu\Beam"
  #define BeamDesktop BeamTestRoot + "\Shortcuts\Desktop\Beam"
#else
  #define BeamAppId "{{6B0808A8-7811-4EBF-9223-BD041CAB8D4C}"
  #define BeamAppName "Beam"
  #define BeamMutex "Local\Beam.Desktop.6B0808A8-7811-4EBF-9223-BD041CAB8D4C"
  #define BeamRunKey "Software\Microsoft\Windows\CurrentVersion\Run"
  #define BeamDefaultDirectory "{localappdata}\Programs\Beam"
  #define BeamStartMenu "{userprograms}\Beam\Beam"
  #define BeamDesktop "{userdesktop}\Beam"
#endif

[Setup]
AppId={#BeamAppId}
AppName={#BeamAppName}
AppVersion={#BeamVersion}
AppPublisher=Beam
AppPublisherURL=https://github.com/IlhamSay123/beam
DefaultDirName={#BeamDefaultDirectory}
DefaultGroupName=Beam
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputBaseFilename=Beam-{#BeamVersion}-win-x64-Setup
VersionInfoVersion={#BeamFileVersion}
UninstallDisplayIcon={app}\Beam.App.exe
AppMutex={#BeamMutex}
CloseApplications=no
RestartApplications=no
UsePreviousTasks=no
WizardStyle=modern
#ifdef BeamTestId
Compression=zip
SolidCompression=no
#else
Compression=lzma2/fast
SolidCompression=yes
#endif

[Tasks]
Name: "startup"; Description: "Start Beam with Windows (in the system tray)"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{#BeamStartMenu}"; Filename: "{app}\Beam.App.exe"; WorkingDir: "{app}"
Name: "{#BeamDesktop}"; Filename: "{app}\Beam.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Beam.App.exe"; Description: "Open Beam"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  StartupKey = '{#BeamRunKey}';
  StartupValue = 'Beam';

var
  StartupPreferenceInitialized: Boolean;

function StartupCommand: String;
begin
  Result := '"' + ExpandConstant('{app}\Beam.App.exe') + '" --startup';
end;

function OwnsStartup: Boolean;
var
  Value: String;
begin
  Result := RegQueryStringValue(HKCU, StartupKey, StartupValue, Value) and
    (CompareText(Value, StartupCommand) = 0);
end;

function ExplicitStartupChoice: Boolean;
var
  I, Separator: Integer;
  Tasks, Task: String;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    { /TASKS replaces all defaults. /MERGETASKS overrides only named tasks. }
    if CompareText(Copy(ParamStr(I), 1, 7), '/TASKS=') = 0 then
    begin
      Result := True;
      Exit;
    end;
    if CompareText(Copy(ParamStr(I), 1, 12), '/MERGETASKS=') = 0 then
    begin
      Tasks := Lowercase(Copy(ParamStr(I), 13, MaxInt));
      while Tasks <> '' do
      begin
        Separator := Pos(',', Tasks);
        if Separator = 0 then
          Separator := Length(Tasks) + 1;
        Task := Trim(Copy(Tasks, 1, Separator - 1));
        Delete(Tasks, 1, Separator);
        if (Task = 'startup') or (Task = '!startup') or (Task = '*startup') then
        begin
          Result := True;
          Exit;
        end;
      end;
    end;
  end;
end;

procedure InitializeStartupPreference;
begin
  if StartupPreferenceInitialized then
    Exit;
  StartupPreferenceInitialized := True;
  { Use the app's current preference, not the tasks saved by an old setup. }
  if not ExplicitStartupChoice and OwnsStartup then
    WizardSelectTasks('startup');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectTasks then
    InitializeStartupPreference;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Silent installs also reach this hook with a fully initialized app path. }
  InitializeStartupPreference;
  Result := '';
  if WizardIsTaskSelected('startup') and (Length(StartupCommand) > 260) then
    Result := 'This folder path is too long for Windows startup. Choose a shorter folder or uncheck Start Beam with Windows.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('startup') then
    begin
      { Do not rewrite an unchanged value or Windows' independent startup approval. }
      if not OwnsStartup then
        if not RegWriteStringValue(HKCU, StartupKey, StartupValue, StartupCommand) then
          RaiseException('Could not save Windows startup. Open Beam and try the startup option again.');
    end
    else if OwnsStartup then
      if not RegDeleteValue(HKCU, StartupKey, StartupValue) then
        RaiseException('Could not turn off Windows startup. Check your Windows account permissions.');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { Only remove this installed copy's command, including opt-in performed in Beam. }
  if (CurUninstallStep = usUninstall) and OwnsStartup then
    if not RegDeleteValue(HKCU, StartupKey, StartupValue) then
      RaiseException('Could not remove Windows startup. Check your Windows account permissions, then retry uninstall.');
end;
