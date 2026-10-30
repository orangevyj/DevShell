; DevShell setup script for Inno Setup 6.
;
; The wizard installs DevShell for the current user only: no admin, nothing machine-wide, and
; nothing that cannot be undone. The files land in the user's own programs folder, the two ways of
; starting DevShell are two values under HKEY_CURRENT_USER, and the shortcuts are the user's own.
;
; Built by Installer\build-setup.ps1, which publishes DevShell first and then passes the publish
; folder in as PublishDir. Compiling this script on its own picks up .\publish instead.

#define MyAppName "DevShell"
#define MyAppVersion "1.5"
#define MyAppPublisher "DevShell"
#define MyAppComment "A from-scratch replacement taskbar for Windows."
#define MyAppExeName "DevShell.exe"

#ifndef PublishDir
  #define PublishDir "publish"
#endif

#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

[Setup]
AppId=DevShell
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments={#MyAppComment}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=DevShell.Setup
SetupIconFile=..\Assets\DevShell.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
SetupMutex=DevShellSetup
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppComment}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
english.ModeGroup=How Windows should start DevShell
english.ModeShell=Instead of Explorer: DevShell becomes the shell for this account
english.ModeAlongside=Beside Explorer: DevShell's bar starts with the session
english.DesktopIcon=Create a desktop shortcut
english.LaunchDevShell=Start DevShell
russian.ModeGroup=Как Windows должна запускать DevShell
russian.ModeShell=Вместо Explorer: DevShell становится оболочкой этой учётной записи
russian.ModeAlongside=Рядом с Explorer: панель DevShell запускается вместе с сеансом
russian.DesktopIcon=Создать ярлык на рабочем столе
russian.LaunchDevShell=Запустить DevShell
german.ModeGroup=Wie Windows DevShell starten soll
german.ModeShell=Statt Explorer: DevShell wird die Shell dieses Kontos
german.ModeAlongside=Neben Explorer: Die DevShell-Leiste startet mit der Sitzung
german.DesktopIcon=Eine Verknüpfung auf dem Desktop erstellen
german.LaunchDevShell=DevShell starten
french.ModeGroup=Comment Windows doit démarrer DevShell
french.ModeShell=À la place de l'Explorateur : DevShell devient le shell de ce compte
french.ModeAlongside=À côté de l'Explorateur : la barre DevShell démarre avec la session
french.DesktopIcon=Créer un raccourci sur le Bureau
french.LaunchDevShell=Démarrer DevShell
spanish.ModeGroup=Cómo debe iniciar Windows DevShell
spanish.ModeShell=En lugar del Explorador: DevShell pasa a ser el shell de esta cuenta
spanish.ModeAlongside=Junto al Explorador: la barra de DevShell se inicia con la sesión
spanish.DesktopIcon=Crear un acceso directo en el escritorio
spanish.LaunchDevShell=Iniciar DevShell

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"
Name: "alongside"; Description: "{cm:ModeAlongside}"; GroupDescription: "{cm:ModeGroup}"; Flags: exclusive
Name: "shellemode"; Description: "{cm:ModeShell}"; GroupDescription: "{cm:ModeGroup}"; Flags: exclusive unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; The old WPF setup left a copy of itself in the install folder to do the uninstalling. Inno keeps
; its own uninstaller, so that leftover is removed here, along with the entry it registered.
[InstallDelete]
Type: files; Name: "{app}\DevShell.Setup.exe"
Type: files; Name: "{app}\payload.zip"

[Registry]
; The two ways of starting DevShell. Only the chosen one is written here; the code below takes the
; other one out, so a change of mind in the wizard is a change of mode and never two copies running.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "DevShell"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: alongside
Root: HKCU; Subkey: "Software\Microsoft\Windows NT\CurrentVersion\Winlogon"; ValueType: string; \
    ValueName: "Shell"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: shellemode
; The uninstall entries the old setup wrote under a name of its own are dead weight now.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\DevShell"; Flags: deletekey

[Icons]
; Inno appends the .lnk extension itself, so the names below must not carry one. Both shortcuts take
; their picture from DevShell.exe, which carries the same logo the setup wizard wears (SetupIconFile
; above), so the Start menu entry, the wizard and the setup file all show one icon.
Name: "{userprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    WorkingDir: "{app}"; Comment: "{#MyAppComment}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    WorkingDir: "{app}"; Comment: "{#MyAppComment}"; IconFilename: "{app}\{#MyAppExeName}"; \
    Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchDevShell}"; WorkingDir: "{app}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Windows never shows an empty Start Menu folder, but leaving one behind is still litter. The
; install folder is Inno's own to remove, but an empty one left by an older copy is not, so it is
; named here as well.
Type: dirifempty; Name: "{userprograms}\{#MyAppName}"
Type: dirifempty; Name: "{app}"

[Code]
const
  WinlogonSubKey = 'Software\Microsoft\Windows NT\CurrentVersion\Winlogon';
  RunSubKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ShellValue = 'Shell';
  RunValue = 'DevShell';

function DevShellExe: String;
begin
  Result := ExpandConstant('{app}\{#MyAppExeName}');
end;

{ Whether a registry value points at this program, quotes and case aside. }
function ValueIsOurs(const Value: String): Boolean;
begin
  Result := CompareText(RemoveQuotes(Trim(Value)), DevShellExe) = 0;
end;

{ Whether Windows will actually start DevShell instead of Explorer. This is read back from the Shell
  value rather than taken from the task that was ticked: a Shell value written by an earlier install
  is what starts at the next sign-in, and it is that, not the wizard's box, the desktop layer has to
  agree with. }
function DevShellIsTheShell: Boolean;
var
  Current: String;
begin
  Result := RegQueryStringValue(HKCU, WinlogonSubKey, ShellValue, Current) and ValueIsOurs(Current);
end;

{ The two ways of starting DevShell rule each other out, so whichever the wizard did not choose is
  taken out here. Only a value that still points at this program is removed, exactly as the uninstall
  does it, so an account set to some other shell keeps it and never ends up with two copies running. }
procedure EnforceStartMode(AsShell: Boolean);
var
  Current: String;
begin
  if AsShell then
  begin
    if RegQueryStringValue(HKCU, RunSubKey, RunValue, Current) and ValueIsOurs(Current) then
      RegDeleteValue(HKCU, RunSubKey, RunValue);
  end
  else
  begin
    if RegQueryStringValue(HKCU, WinlogonSubKey, ShellValue, Current) and ValueIsOurs(Current) then
      RegDeleteValue(HKCU, WinlogonSubKey, ShellValue);
  end;
end;

{ The bar cannot be replaced while it is running, so it is asked to go before the files land. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#MyAppExeName}', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Sleep(500);
end;

{ DevShell's desktop layer is its own setting, not one of ours: the wallpaper and its icons are the
  bar's job when it is the shell, and two sets of desktop icons is one set too many when it is not.
  The file is read, changed if it is there, and otherwise written whole, so a config that already
  holds the user's other choices survives. }
function SetDesktopLayerText(const Text: String; Show: Boolean): String;
var
  Wanted, Rest, Tail: String;
  Brace, P: Integer;
begin
  if Show then
    Wanted := 'true'
  else
    Wanted := 'false';

  P := Pos('"ShowDesktopLayer"', Text);

  if P > 0 then
  begin
    P := P + Length('"ShowDesktopLayer"');

    while (P <= Length(Text)) and ((Text[P] = ' ') or (Text[P] = ':') or (Text[P] = #9)) do
      P := P + 1;

    if CompareText(Copy(Text, P, 5), 'false') = 0 then
      Result := Copy(Text, 1, P - 1) + Wanted + Copy(Text, P + 5, MaxInt)
    else if CompareText(Copy(Text, P, 4), 'true') = 0 then
      Result := Copy(Text, 1, P - 1) + Wanted + Copy(Text, P + 4, MaxInt)
    else
      Result := Text;

    Exit;
  end;

  Brace := Pos('{', Text);

  if Brace = 0 then
  begin
    Result := '{' + #13#10 + '  "ShowDesktopLayer": ' + Wanted + #13#10 + '}' + #13#10;
    Exit;
  end;

  Tail := Copy(Text, Brace + 1, MaxInt);
  Rest := TrimLeft(Tail);

  if (Length(Rest) > 0) and (Rest[1] = '}') then
    Result := Copy(Text, 1, Brace) + #13#10 + '  "ShowDesktopLayer": ' + Wanted + #13#10 + Tail
  else
    Result := Copy(Text, 1, Brace) + #13#10 + '  "ShowDesktopLayer": ' + Wanted + ',' + Tail;
end;

procedure WriteDesktopLayer(Show: Boolean);
var
  FileName: String;
  Raw: AnsiString;
  Text, Updated: String;
begin
  FileName := ExpandConstant('{localappdata}\{#MyAppName}\config.json');

  try
    { LoadStringFromFile and SaveStringToFile want an AnsiString, which is how these two hand the
      file's own bytes back and forth: the text is turned into a string here and turned back at the
      other end, so a config holding anything but ASCII survives the trip untouched. }
    if LoadStringFromFile(FileName, Raw) then
    begin
      Text := String(Raw);
      Updated := SetDesktopLayerText(Text, Show);

      if Updated <> Text then
        SaveStringToFile(FileName, AnsiString(Updated), False);
    end
    else
    begin
      ForceDirectories(ExpandConstant('{localappdata}\{#MyAppName}'));
      SaveStringToFile(FileName, AnsiString(SetDesktopLayerText('', Show)), False);
    end;
  except
    Log('could not write the desktop layer setting: ' + GetExceptionMessage);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    EnforceStartMode(WizardIsTaskSelected('shellemode'));
    WriteDesktopLayer(DevShellIsTheShell);

    if DevShellIsTheShell then
      Log('Windows will start DevShell instead of Explorer for this user')
    else
      Log('DevShell will start with the session, beside Explorer');
  end;
end;

{ Uninstalling takes DevShell out of the two places it was put, but only where the value is still
  ours: an account set to some other shell keeps it. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Current: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, WinlogonSubKey, ShellValue, Current) and ValueIsOurs(Current) then
      RegDeleteValue(HKCU, WinlogonSubKey, ShellValue);

    if RegQueryStringValue(HKCU, RunSubKey, RunValue, Current) and ValueIsOurs(Current) then
      RegDeleteValue(HKCU, RunSubKey, RunValue);
  end;
end;
