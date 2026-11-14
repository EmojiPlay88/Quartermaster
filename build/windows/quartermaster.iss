; Inno Setup 6 script for the per-user Quartermaster installer (unsigned; Windows SmartScreen may warn).
; Built by build/windows/make-windows-packages.ps1, which passes /DAppVersion=, /DSourceDir= and /DOutputDir=.
; The install folder (%LOCALAPPDATA%\Programs\Quartermaster) is NOT the data folder (%LOCALAPPDATA%\Quartermaster):
; uninstalling never touches user data. The folder page is disabled (uninstall removes {app} recursively) and
; PrepareToInstall refuses unsafe /DIR= values (drive or share root, the data folder or anything inside it in long or
; 8.3 spelling, a foreign non-empty folder), also in silent mode. An upgrade first deletes the files the old install
; listed in quartermaster-files.txt (CurStepChanged), so files the new version dropped do not stay behind.
#ifndef AppVersion
  #error AppVersion not defined (pass /DAppVersion=x.y.z)
#endif
#ifndef SourceDir
  #error SourceDir not defined (pass /DSourceDir=<staged publish folder>)
#endif
#ifndef OutputDir
  #error OutputDir not defined (pass /DOutputDir=<folder>)
#endif

[Setup]
AppId={{6F0B6C2E-9C1F-4E8E-9C55-5C5E0A1D2B7E}
AppName=Quartermaster
AppVersion={#AppVersion}
AppPublisher=Quartermaster contributors
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Quartermaster
DefaultGroupName=Quartermaster
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Quartermaster.exe
OutputDir={#OutputDir}
OutputBaseFilename=Quartermaster-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Quartermaster"; Filename: "{app}\Quartermaster.exe"
Name: "{autodesktop}\Quartermaster"; Filename: "{app}\Quartermaster.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Quartermaster.exe"; Description: "Launch Quartermaster"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
; Files the in-app updater added, and its backups. Never the data folder.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\Programs\Quartermaster.backup-*"

[Code]
function DirHasEntries(const Dir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Dir) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

{ Removes trailing backslashes (also from a UNC share root such as \\server\share\). }
function TrimSlashes(const S: String): String;
begin
  Result := S;
  while (Length(Result) > 0) and (Result[Length(Result)] = '\') do
    Delete(Result, Length(Result), 1);
end;

{ True for a drive root (C:\) and a UNC share root (\\server\share, with or without a trailing backslash). }
function IsRootFolder(const Dir: String): Boolean;
var
  S: String;
begin
  S := TrimSlashes(Dir);
  Result := (S = '') or (CompareText(S, ExtractFileDrive(S)) = 0) or
            ((Copy(S, 1, 2) = '\\') and (ExtractFileDrive(S) = ''));
end;

{ The 8.3 spelling of Dir: the nearest existing ancestor is shortened, what does not exist yet is kept as written. }
function ShortForm(const Dir: String): String;
var
  Existing, Rest, Parent: String;
begin
  Existing := TrimSlashes(Dir);
  Rest := '';
  while (Existing <> '') and (not DirExists(Existing)) do
  begin
    Parent := TrimSlashes(ExtractFilePath(Existing));
    if (Parent = Existing) or (Parent = '') then
    begin
      Result := Dir;
      Exit;
    end;
    Rest := Copy(Existing, Length(Parent) + 1, Length(Existing)) + Rest;
    Existing := Parent;
  end;
  if Existing = '' then
    Result := Dir
  else
    Result := GetShortName(Existing) + Rest;
end;

{ True when Dir is Base or lies below it (both without trailing backslash; case-insensitive). }
function IsSameOrUnder(const Dir, Base: String): Boolean;
begin
  Result := (Base <> '') and (CompareText(Copy(Dir, 1, Length(Base)), Base) = 0) and
            ((Length(Dir) = Length(Base)) or (Dir[Length(Base) + 1] = '\'));
end;

{ True when a list entry is a plain relative path that may be deleted below {app}. }
function SafeListEntry(const Rel: String): Boolean;
var
  Padded, Base: String;
begin
  Result := False;
  if Rel = '' then Exit;
  if Rel[1] = '\' then Exit;
  if (Pos(':', Rel) > 0) or (Pos('*', Rel) > 0) or (Pos('?', Rel) > 0) or (Pos('"', Rel) > 0) or
     (Pos('<', Rel) > 0) or (Pos('>', Rel) > 0) or (Pos('|', Rel) > 0) then Exit;
  Padded := '\' + Rel + '\';
  if Pos('\..\', Padded) > 0 then Exit;
  Base := LowerCase(ExtractFileName(Rel));
  if (Base = '') or (Copy(Base, 1, 5) = 'unins') or (Base = 'quartermaster-files.txt') then Exit;
  Result := True;
end;

{ An upgrade over an existing install: delete the files the old version listed in quartermaster-files.txt
  (the new version's files are installed right after), then the folders that became empty. Never outside {app}. }
procedure RemoveStaleFiles;
var
  AppDir, ListFile, Rel, Full, Dir: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  AppDir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  ListFile := AddBackslash(AppDir) + 'quartermaster-files.txt';
  if not FileExists(ListFile) then Exit;
  if not LoadStringsFromFile(ListFile, Lines) then Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Rel := Trim(Lines[I]);
    StringChangeEx(Rel, '/', '\', True);
    if not SafeListEntry(Rel) then Continue;
    Full := AddBackslash(AppDir) + Rel;
    if FileExists(Full) then
    begin
      DeleteFile(Full);
      { Remove folders that are now empty, up to (never including) the install folder. }
      Dir := TrimSlashes(ExtractFilePath(Full));
      while IsSameOrUnder(Dir, AppDir) and (CompareText(Dir, AppDir) <> 0) do
      begin
        if not RemoveDir(Dir) then Break;
        Dir := TrimSlashes(ExtractFilePath(Dir));
      end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    RemoveStaleFiles;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Dir, DataDir, DirShort, DataShort: String;
begin
  Result := '';
  Dir := TrimSlashes(ExpandFileName(WizardDirValue));
  DataDir := TrimSlashes(ExpandConstant('{localappdata}\Quartermaster'));
  if IsRootFolder(Dir) then
  begin
    Result := 'The install folder cannot be a drive or network share root (' + Dir + '). Choose another folder.';
    Exit;
  end;
  { Short names are only worked out for folders that are not a root (a UNC root would touch the network). }
  DirShort := ShortForm(Dir);
  DataShort := ShortForm(DataDir);
  if IsSameOrUnder(Dir, DataDir) or IsSameOrUnder(DirShort, DataDir) or
     IsSameOrUnder(Dir, DataShort) or IsSameOrUnder(DirShort, DataShort) then
    Result := 'The install folder cannot be the Quartermaster data folder or a folder inside it (' + DataDir + '). Choose another folder.'
  else if DirExists(Dir) and DirHasEntries(Dir) and (not FileExists(AddBackslash(Dir) + 'Quartermaster.exe')) then
    Result := 'The folder ' + Dir + ' is not empty and is not a Quartermaster install. Uninstalling removes the whole install folder, so choose an empty folder.';
end;
