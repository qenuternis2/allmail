#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
[Setup]
AppId={{0B21BAE5-7FB5-4B09-9C77-A8A763DB7A12}
AppName=SecureBrowser
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
VersionInfoTextVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}.0
VersionInfoProductTextVersion={#AppVersion}
AppPublisher=SecureBrowser
AppPublisherURL=https://github.com/qenuternis2/allmail
DefaultDirName={localappdata}\Programs\SecureBrowser
DefaultGroupName=SecureBrowser
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=SecureBrowser-{#AppVersion}-setup-win-x64
SetupIconFile=..\src\ProtonProfiles.App\Assets\globe.ico
UninstallDisplayIcon={app}\SecureBrowser.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "Значок на рабочем столе"; Flags: unchecked
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"
[Icons]
Name: "{userprograms}\SecureBrowser"; Filename: "{app}\SecureBrowser.exe"
Name: "{userdesktop}\SecureBrowser"; Filename: "{app}\SecureBrowser.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\SecureBrowser.exe"; Description: "Запустить SecureBrowser"; Flags: nowait postinstall skipifsilent
[Code]
function RemoveDataRequested: Boolean;
var I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/REMOVEUSERDATA') = 0 then Result := True;
end;

function InitializeUninstall: Boolean;
var RemoveData: Boolean; Choice, ExitCode: Integer;
begin
  Result := False;
  RemoveData := RemoveDataRequested;
  if not UninstallSilent then begin
    Choice := MsgBox('Удалить также все локальные профили SecureBrowser, cookies, настройки и сохранённые пароли прокси?' + #13#10 +
      '«Нет» сохраняет данные для следующей установки. Сохранённые вне программы вложения и общий WebView2 Runtime не удаляются.',
      mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON2);
    if Choice = IDCANCEL then Exit;
    RemoveData := Choice = IDYES;
  end;
  if RemoveData then begin
    if not Exec(ExpandConstant('{app}\SecureBrowser.exe'), '--remove-managed-data --confirmed', ExpandConstant('{app}'),
      SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
      MsgBox('Не удалось запустить удаление данных. Программа и оставшиеся данные сохранены.', mbError, MB_OK);
      Exit;
    end;
    if ExitCode <> 0 then Exit;
  end;
  Result := True;
end;
