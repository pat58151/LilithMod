; Inno Setup script for LilithMod.
; Build with installer\build-installer.ps1 — it stages the payload (the release
; zip, extracted) into installer\payload\ and passes /DAppVersion=<version>.
; The install rules implemented here are documented in document\PORTABILITY.md,
; which is kept locally and not published.

#ifndef AppVersion
  #define AppVersion "1.0.0.0"
#endif
#ifndef PayloadDir
  #define PayloadDir "payload"
#endif

#define GameExe "Lilith.exe"
#define SteamAppId "4643090"
#define GameFolderName "The NOexistenceN of Lilith"

[Setup]
AppId={{C7A9F2D4-5B31-4E86-9A0D-2F6C1E7B8A43}
AppName=LilithMod
AppVersion={#AppVersion}
AppPublisher=pattarapongsinpat
AppSupportURL=https://github.com/pattarapongsinpat/LilithMod
DefaultDirName={code:GetDefaultDir}
AppendDefaultDirName=no
DirExistsWarning=no
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=LilithMod-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
; Keep the uninstaller with the loader instead of littering the game root.
UninstallFilesDir={app}\BepInEx
UninstallDisplayName=LilithMod (for {#GameFolderName})
WizardStyle=modern

[Messages]
SelectDirDesc=Where is {#GameFolderName} installed?
SelectDirLabel3=Setup will install LilithMod into your game folder. It must contain {#GameExe}. Setup looked for it in your Steam libraries; correct the path if the guess is wrong.
FinishedLabelNoIcons=LilithMod is installed.%n%nIMPORTANT — the first launch takes several minutes while BepInEx generates files from the game. This is normal. Do not close the game while it appears frozen; force-quitting can break the next launch too.%n%nChat needs an API key pasted in-game; F7/F8 stay greyed out until one is set. Voice is optional and not included — see BepInEx\plugins\LilithMod\voice-setup\README.txt.%n%nIf the game ever looks unmodded, fully exit the game AND Steam, then start Steam again.
FinishedLabel=LilithMod is installed.%n%nIMPORTANT — the first launch takes several minutes while BepInEx generates files from the game. This is normal. Do not close the game while it appears frozen; force-quitting can break the next launch too.%n%nChat needs an API key pasted in-game; F7/F8 stay greyed out until one is set. Voice is optional and not included — see BepInEx\plugins\LilithMod\voice-setup\README.txt.%n%nIf the game ever looks unmodded, fully exit the game AND Steam, then start Steam again.

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
; Never launch Lilith.exe directly: with Steam closed that can leave Steam
; holding DOORSTOP_DISABLE=TRUE, which silently disables the mod on later
; launches until Steam is fully restarted.
Filename: "steam://run/{#SteamAppId}"; Description: "Launch {#GameFolderName} via Steam"; Flags: shellexec postinstall nowait skipifsilent

[Code]
var
  DetectedGameDir: String;
  DetectionDone: Boolean;

function NormalizePath(P: String): String;
begin
  StringChangeEx(P, '/', '\', True);
  Result := RemoveBackslashUnlessRoot(P);
end;

{ After the first occurrence of Key, return the next double-quoted substring. }
function ExtractQuotedAfter(const Line, Key: String): String;
var
  S: String;
  I: Integer;
begin
  Result := '';
  I := Pos(Key, Line);
  if I = 0 then exit;
  S := Copy(Line, I + Length(Key), Length(Line));
  I := Pos('"', S);
  if I = 0 then exit;
  S := Copy(S, I + 1, Length(S));
  I := Pos('"', S);
  if I = 0 then exit;
  Result := Copy(S, 1, I - 1);
end;

function TryGameUnder(Root: String): String;
var
  Candidate: String;
begin
  Result := '';
  if Root = '' then exit;
  Candidate := AddBackslash(NormalizePath(Root)) + 'steamapps\common\{#GameFolderName}';
  if FileExists(AddBackslash(Candidate) + '{#GameExe}') then
    Result := Candidate;
end;

{ Mirrors Find-GameFolder in reapply-mod.ps1: Steam registry, then every
  library in libraryfolders.vdf. }
function FindGameDir(): String;
var
  Steam, P: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := '';
  Steam := '';
  if not RegQueryStringValue(HKCU, 'Software\Valve\Steam', 'SteamPath', Steam) then
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath', Steam);
  if Steam = '' then exit;
  Steam := NormalizePath(Steam);
  Result := TryGameUnder(Steam);
  if Result <> '' then exit;
  if LoadStringsFromFile(AddBackslash(Steam) + 'steamapps\libraryfolders.vdf', Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
      if Pos('"path"', Lines[I]) > 0 then
      begin
        P := ExtractQuotedAfter(Lines[I], '"path"');
        StringChangeEx(P, '\\', '\', True);
        Result := TryGameUnder(P);
        if Result <> '' then exit;
      end;
end;

function GetDefaultDir(Param: String): String;
begin
  if not DetectionDone then
  begin
    DetectedGameDir := FindGameDir();
    DetectionDone := True;
  end;
  if DetectedGameDir <> '' then
    Result := DetectedGameDir
  else
    Result := ExpandConstant('{autopf32}') + '\Steam\steamapps\common\{#GameFolderName}';
end;

function IsGameRunning(): Boolean;
var
  WbemLocator, WbemServices, Processes: Variant;
begin
  Result := False;
  try
    WbemLocator := CreateOleObject('WbemScripting.SWbemLocator');
    WbemServices := WbemLocator.ConnectServer('.', 'root\CIMV2');
    Processes := WbemServices.ExecQuery('SELECT Name FROM Win32_Process WHERE Name = ''{#GameExe}''');
    Result := Processes.Count > 0;
  except
    { If WMI is unavailable, proceed; the file copy will surface a lock error. }
    Result := False;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
    if not FileExists(AddBackslash(WizardDirValue) + '{#GameExe}') then
    begin
      MsgBox('No {#GameExe} found in:' + #13#10 + WizardDirValue + #13#10#13#10 +
             'Select the folder that contains {#GameExe} (usually ' +
             '...\steamapps\common\{#GameFolderName}).', mbError, MB_OK);
      Result := False;
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  { The game locks LilithMod.dll; copying over it fails while it runs. }
  while IsGameRunning() do
    if MsgBox('{#GameFolderName} is running. Close the game, then press Retry.',
              mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := 'Setup cannot continue while the game is running.';
      exit;
    end;
end;

{ The single most important step: Steam passes
  DOORSTOP_DISABLE=TRUE on essentially every launch. Without
  ignore_disable_switch = true the mod silently never loads and every log looks
  healthy. Rewrite if needed, then RE-READ and verify — never trust a
  search-and-replace to have matched. }
procedure AssertDoorstopSwitch();
var
  Ini, T: String;
  Lines: TArrayOfString;
  I: Integer;
  Found, Changed, Verified: Boolean;
begin
  Ini := AddBackslash(ExpandConstant('{app}')) + 'doorstop_config.ini';
  Found := False;
  Changed := False;
  Verified := False;
  if LoadStringsFromFile(Ini, Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      T := Trim(Lines[I]);
      if Pos('ignore_disable_switch', Lowercase(T)) = 1 then
      begin
        Found := True;
        if T <> 'ignore_disable_switch = true' then
        begin
          Lines[I] := 'ignore_disable_switch = true';
          Changed := True;
        end;
      end;
    end;
    if Found and Changed then
      SaveStringsToUTF8File(Ini, Lines, False);
    if Found then
      if LoadStringsFromFile(Ini, Lines) then
        for I := 0 to GetArrayLength(Lines) - 1 do
          if Trim(Lines[I]) = 'ignore_disable_switch = true' then
            Verified := True;
  end;
  if not Verified then
    MsgBox('CRITICAL: Setup could not verify this line in ' + Ini + ':' + #13#10#13#10 +
           'ignore_disable_switch = true' + #13#10#13#10 +
           'Without it, Steam disables the mod on every launch and the game ' +
           'will look unmodded while every log looks healthy. Open the file in ' +
           'Notepad and set that line manually before playing.',
           mbCriticalError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Cfg, Bak: String;
begin
  if CurStep = ssInstall then
  begin
    { Preserve the user's API key before anything is overwritten. }
    Cfg := AddBackslash(ExpandConstant('{app}')) + 'BepInEx\config\LilithMod.cfg';
    if FileExists(Cfg) then
    begin
      Bak := Cfg + '.bak-' + GetDateTimeString('yyyymmdd-hhnnss', #0, #0);
      if not FileCopy(Cfg, Bak, False) then
        MsgBox('Warning: could not back up LilithMod.cfg (it holds your API key). ' +
               'Setup will not overwrite it, but consider copying it somewhere safe.',
               mbInformation, MB_OK);
    end;
    { Note: Setup never writes LilithMod.cfg itself. The mod generates it on
      first run; writing one here would pin today's defaults forever. }
  end
  else if CurStep = ssPostInstall then
    AssertDoorstopSwitch();
end;

function InitializeUninstall(): Boolean;
var
  PluginsDir, Others: String;
  FindRec: TFindRec;
begin
  Result := True;
  while IsGameRunning() do
    if MsgBox('{#GameFolderName} is running. Close the game, then press Retry.',
              mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      exit;
    end;
  { The uninstaller removes the BepInEx loader it installed. If any other mod
    lives in plugins\, that mod dies with it — warn first. }
  PluginsDir := AddBackslash(ExpandConstant('{app}')) + 'BepInEx\plugins';
  Others := '';
  if FindFirst(PluginsDir + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') and
           (CompareText(FindRec.Name, 'LilithMod') <> 0) then
          Others := Others + '    ' + FindRec.Name + #13#10;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  if Others <> '' then
    if MsgBox('Other BepInEx plugins were found:' + #13#10#13#10 + Others + #13#10 +
              'Uninstalling LilithMod also removes the BepInEx loader they need, ' +
              'and they will stop working.' + #13#10#13#10 + 'Continue anyway?',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDNO then
      Result := False;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir, PluginDir, CfgDir: String;
  HasData: Boolean;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    AppDir := AddBackslash(ExpandConstant('{app}'));
    PluginDir := AppDir + 'BepInEx\plugins\LilithMod\';
    CfgDir := AppDir + 'BepInEx\config\';
    { memory.json and notes.json are the only irreplaceable things the mod
      creates — her memory of the user's conversations. Default is KEEP. }
    HasData := FileExists(PluginDir + 'memory.json') or
               FileExists(PluginDir + 'notes.json') or
               DirExists(PluginDir + 'voice-cache') or
               DirExists(PluginDir + 'custom') or
               FileExists(CfgDir + 'LilithMod.cfg');
    if HasData then
      if MsgBox('Keep Lilith''s personal data?' + #13#10#13#10 +
                'This is her memory of your conversations (memory.json), her notes ' +
                '(notes.json), custom dialogue, cached voice audio, and your settings ' +
                'including the API key (LilithMod.cfg).' + #13#10#13#10 +
                'Yes = keep them (recommended; a reinstall picks them up again).' + #13#10 +
                'No = delete them permanently. This cannot be undone.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDNO then
      begin
        DeleteFile(PluginDir + 'memory.json');
        DeleteFile(PluginDir + 'notes.json');
        DelTree(PluginDir + 'voice-cache', True, True, True);
        DelTree(PluginDir + 'custom', True, True, True);
        DeleteFile(CfgDir + 'LilithMod.cfg');
        DelTree(CfgDir + 'LilithMod.cfg.bak-*', False, True, False);
        RemoveDir(AppDir + 'BepInEx\plugins\LilithMod');
        RemoveDir(AppDir + 'BepInEx\plugins');
        RemoveDir(AppDir + 'BepInEx\config');
        RemoveDir(AppDir + 'BepInEx');
      end;
  end;
end;
