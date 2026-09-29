// Included inside [Code]. Keep the registry/segment logic independent of installer
// events so AddToPath.Tests.iss can exercise it against isolated HKCU subkeys.
const
  PathRegSz = 1;
  PathRegExpandSz = 2;
  PathKeyRead = $20019;
  PathKeyWrite = $20006;
  PathKey64 = $100;
  PathOwnerKey = 'Software\Devolutions\UniGetUI\InstallerPath';

var
  UnigetPathUpdateFailed: Boolean;

function PathRegOpenKey(Root: Integer; SubKey: String; Options, Access: LongWord;
  var Key: THandle): LongWord;
  external 'RegOpenKeyExW@advapi32.dll stdcall';
function PathRegCreateKey(Root: Integer; SubKey: String; Reserved: LongWord;
  ClassName: String; Options, Access: LongWord; Security: THandle;
  var Key: THandle; var Disposition: LongWord): LongWord;
  external 'RegCreateKeyExW@advapi32.dll stdcall';
function PathRegQuerySize(Key: THandle; Name: String; Reserved: THandle;
  var Kind: LongWord; Data: THandle; var Size: LongWord): LongWord;
  external 'RegQueryValueExW@advapi32.dll stdcall';
function PathRegQueryData(Key: THandle; Name: String; Reserved: THandle;
  var Kind: LongWord; Data: String; var Size: LongWord): LongWord;
  external 'RegQueryValueExW@advapi32.dll stdcall';
function PathRegSetValue(Key: THandle; Name: String; Reserved, Kind: LongWord;
  Data: String; Size: LongWord): LongWord;
  external 'RegSetValueExW@advapi32.dll stdcall';
function PathRegDeleteValue(Key: THandle; Name: String): LongWord;
  external 'RegDeleteValueW@advapi32.dll stdcall';
function PathRegCloseKey(Key: THandle): LongWord;
  external 'RegCloseKey@advapi32.dll stdcall';
function PathExpandEnvironmentSize(Source: String; Dest: THandle;
  Size: LongWord): LongWord;
  external 'ExpandEnvironmentStringsW@kernel32.dll stdcall';
function PathExpandEnvironmentData(Source, Dest: String; Size: LongWord): LongWord;
  external 'ExpandEnvironmentStringsW@kernel32.dll stdcall';
function PathSendEnvironmentChange(Window: THandle; Msg: LongWord;
  WParam: THandle; LParam: String; Flags, Timeout: LongWord;
  var Response: THandle): THandle;
  external 'SendMessageTimeoutW@user32.dll stdcall';

procedure PathCheck(Code: LongWord; Operation: String);
begin
  if Code <> 0 then
    RaiseException(Operation + ': ' + SysErrorMessage(Code) +
      ' (Win32 ' + IntToStr(Code) + ')');
end;

procedure PathClose(Key: THandle);
begin
  PathCheck(PathRegCloseKey(Key), 'Closing PATH registry key');
end;

procedure PathRead(Root: Integer; SubKey, Name: String; var Value: String;
  var Kind: LongWord; var Exists: Boolean);
var
  Key: THandle;
  Code, Size: LongWord;
begin
  Exists := False;
  Value := '';
  Kind := PathRegExpandSz; // Only used for a genuinely missing value.
  Code := PathRegOpenKey(Root, SubKey, 0, PathKeyRead or PathKey64, Key);
  if Code = 2 then Exit; // ERROR_FILE_NOT_FOUND, not access denied or any other error.
  PathCheck(Code, 'Opening ' + SubKey);
  try
    Size := 0;
    Code := PathRegQuerySize(Key, Name, 0, Kind, 0, Size);
    if Code = 2 then
    begin
      Kind := PathRegExpandSz;
      Exit;
    end;
    PathCheck(Code, 'Reading type/size of ' + SubKey + '\' + Name);
    if ((Kind <> PathRegSz) and (Kind <> PathRegExpandSz)) or
       ((Size mod 2) <> 0) or (Size > 1048576) then
      RaiseException('Unsupported registry string type/size: ' + SubKey + '\' + Name);
    // Query raw UTF-16, NEVER expand REG_EXPAND_SZ or infer its type from its text.
    // A concurrent growth returns ERROR_MORE_DATA and aborts rather than truncating.
    if Size = 0 then Size := 2;
    SetLength(Value, Size div 2);
    Code := PathRegQueryData(Key, Name, 0, Kind, Value, Size);
    PathCheck(Code, 'Reading ' + SubKey + '\' + Name);
    if ((Kind <> PathRegSz) and (Kind <> PathRegExpandSz)) or
       ((Size mod 2) <> 0) then
      RaiseException('Registry string changed type/size while reading');
    SetLength(Value, Size div 2);
    if Length(Value) > 0 then
      if Value[Length(Value)] = #0 then
        SetLength(Value, Length(Value) - 1);
    if Pos(#0, Value) <> 0 then
      RaiseException('Registry string contains an embedded NUL');
    Exists := True;
  finally
    PathClose(Key);
  end;
end;

procedure PathWrite(Root: Integer; SubKey, Name, Value: String; Kind: LongWord);
var
  Key: THandle;
  Disposition: LongWord;
begin
  PathCheck(PathRegCreateKey(Root, SubKey, 0, '', 0,
    PathKeyWrite or PathKey64, 0, Key, Disposition), 'Opening for write: ' + SubKey);
  try
    // Pascal Script marshals an empty String as NULL. Supply the terminator
    // explicitly so writing an empty registry string still has a valid buffer.
    PathCheck(PathRegSetValue(Key, Name, 0, Kind, Value + #0,
      (Length(Value) + 1) * 2), 'Writing ' + SubKey + '\' + Name);
  finally
    PathClose(Key);
  end;
end;

procedure PathDeleteOwner(Root: Integer; OwnerKey, OwnerName: String);
var
  Key: THandle;
  Code: LongWord;
begin
  Code := PathRegOpenKey(Root, OwnerKey, 0, PathKeyWrite or PathKey64, Key);
  if Code = 2 then Exit;
  PathCheck(Code, 'Opening PATH ownership key');
  try
    Code := PathRegDeleteValue(Key, OwnerName);
    if Code <> 2 then PathCheck(Code, 'Deleting PATH ownership record');
  finally
    PathClose(Key);
  end;
end;

function PathNormalize(Value: String; Kind: LongWord): String;
var
  Size, Written: LongWord;
  Expanded: String;
begin
  Value := Trim(Value);
  if Length(Value) >= 2 then
    if (Value[1] = '"') and (Value[Length(Value)] = '"') then
      Value := Trim(Copy(Value, 2, Length(Value) - 2));
  if (Kind = PathRegExpandSz) and (Pos('%', Value) <> 0) then
  begin
    Size := PathExpandEnvironmentSize(Value, 0, 0);
    if Size = 0 then RaiseException('Cannot expand PATH segment for comparison');
    SetLength(Expanded, Size);
    Written := PathExpandEnvironmentData(Value, Expanded, Size);
    if (Written = 0) or (Written > Size) then
      RaiseException('Cannot expand PATH segment for comparison');
    SetLength(Expanded, Written - 1);
    Value := Expanded;
  end;
  StringChangeEx(Value, '/', '\', True);
  // Canonicalize absolute paths only; a relative PATH entry is not our install directory.
  if Length(Value) >= 3 then
    if ((Value[2] = ':') and (Value[3] = '\')) or (Copy(Value, 1, 2) = '\\') then
      Value := ExpandFileName(Value);
  while Length(Value) > 3 do
  begin
    if Value[Length(Value)] <> '\' then Break;
    SetLength(Value, Length(Value) - 1);
  end;
  Result := Lowercase(Value);
end;

// Preserve every unrelated segment, including whitespace, quotes and empty entries.
// Only comparisons use normalized strings. Cleanup requires ONE equivalent segment
// with the EXACT spelling we added; a user edit/duplicate relinquishes ownership.
procedure PathFind(Value, Entry: String; Kind: LongWord;
  var Matches, ExactStart: Integer);
var
  Start, Finish: Integer;
  Segment, NormalEntry: String;
begin
  Matches := 0;
  ExactStart := 0;
  NormalEntry := PathNormalize(Entry, PathRegSz);
  Start := 1;
  while Start <= Length(Value) + 1 do
  begin
    Finish := Start;
    while Finish <= Length(Value) do
    begin
      if Value[Finish] = ';' then Break;
      Finish := Finish + 1;
    end;
    Segment := Copy(Value, Start, Finish - Start);
    if PathNormalize(Segment, Kind) = NormalEntry then
    begin
      Matches := Matches + 1;
      if Segment = Entry then ExactStart := Start;
    end;
    Start := Finish + 1;
  end;
end;

procedure PathWriteIfUnchanged(Root: Integer; EnvironmentKey, Before, After: String;
  Kind: LongWord; Existed: Boolean);
var
  Current: String;
  CurrentKind: LongWord;
  CurrentExists: Boolean;
begin
  PathRead(Root, EnvironmentKey, 'Path', Current, CurrentKind, CurrentExists);
  if (Current <> Before) or (CurrentExists <> Existed) or
     (Existed and (CurrentKind <> Kind)) then
    RaiseException('PATH changed concurrently; no PATH write was attempted. Retry setup.');
  PathWrite(Root, EnvironmentKey, 'Path', After, Kind);
end;

// O + entry = committed ownership; P/R + before/after SHA256 + entry =
// pending append/removal. Persist intent BEFORE changing PATH, in one REG_SZ value.
// This survives upgrades and interrupted installs without ever claiming a pre-existing
// entry. Ambiguous recovery deliberately leaves PATH alone (safety over cleanup).
procedure PathUpdate(Root: Integer; EnvironmentKey, OwnerKey, Entry: String;
  Selected: Boolean);
var
  Value, After, RecordValue, OwnedEntry, OwnerName, BeforeHash, AfterHash: String;
  Kind, RecordKind: LongWord;
  Exists, RecordExists, Owned: Boolean;
  Matches, ExactStart: Integer;
begin
  if Selected and ((Entry = '') or (Pos(';', Entry) <> 0) or (Pos('%', Entry) <> 0)) then
    RaiseException('Install directory cannot be represented safely as a PATH segment');
  OwnerName := GetSHA256OfUnicodeString(PathNormalize(Entry, PathRegSz));
  PathRead(Root, OwnerKey, OwnerName, RecordValue, RecordKind, RecordExists);
  // A never-selected task has nothing to clean up, and need not even read PATH.
  if not RecordExists and not Selected then Exit;
  PathRead(Root, EnvironmentKey, 'Path', Value, Kind, Exists);
  Owned := False;
  if RecordExists then
  begin
    if (RecordKind <> PathRegSz) or (Length(RecordValue) < 2) then
      RaiseException('Invalid PATH ownership record');
    if RecordValue[1] = 'O' then
    begin
      Owned := True;
      OwnedEntry := Copy(RecordValue, 2, MaxInt);
    end
    else if ((RecordValue[1] = 'P') or (RecordValue[1] = 'R')) and
            (Length(RecordValue) > 129) then
    begin
      BeforeHash := Copy(RecordValue, 2, 64);
      AfterHash := Copy(RecordValue, 66, 64);
      OwnedEntry := Copy(RecordValue, 130, MaxInt);
      Owned := ((RecordValue[1] = 'P') and
                 (GetSHA256OfUnicodeString(Value) = AfterHash)) or
               ((RecordValue[1] = 'R') and
                 (GetSHA256OfUnicodeString(Value) = BeforeHash));
      Log('Recovering interrupted PATH operation; ownership confirmed: ' + IntToStr(Ord(Owned)));
    end
    else
      RaiseException('Unknown PATH ownership record');
    if PathNormalize(OwnedEntry, PathRegSz) <> PathNormalize(Entry, PathRegSz) then
      RaiseException('PATH ownership record does not match this install directory');
    if Owned then
    begin
      if RecordValue[1] <> 'O' then
        PathWrite(Root, OwnerKey, OwnerName, 'O' + OwnedEntry, PathRegSz);
    end
    else
      PathDeleteOwner(Root, OwnerKey, OwnerName);
  end;

  PathFind(Value, Entry, Kind, Matches, ExactStart);
  if Owned then
  begin
    PathFind(Value, OwnedEntry, Kind, Matches, ExactStart);
    if (Matches <> 1) or (ExactStart = 0) then
    begin
      Log('PATH entry missing, edited or duplicated; preserving PATH and relinquishing ownership.');
      PathDeleteOwner(Root, OwnerKey, OwnerName);
      Owned := False;
    end;
  end;
  if Selected then
  begin
    if Matches > 0 then
    begin
      Log('Equivalent uniget PATH entry already exists; no entry added or claimed.');
      Exit;
    end;
    After := Value;
    // Always add our own delimiter: do not consume a pre-existing empty last segment.
    if After <> '' then After := After + ';';
    After := After + Entry;
    RecordValue := 'P' + GetSHA256OfUnicodeString(Value) +
      GetSHA256OfUnicodeString(After) + Entry;
    PathWrite(Root, OwnerKey, OwnerName, RecordValue, PathRegSz);
    PathWriteIfUnchanged(Root, EnvironmentKey, Value, After, Kind, Exists);
    PathWrite(Root, OwnerKey, OwnerName, 'O' + Entry, PathRegSz);
    Log('Added install directory to PATH for the uniget command.');
  end
  else
  begin
    if not Owned then Exit;
    After := Value;
    if ExactStart > 1 then
      Delete(After, ExactStart - 1, Length(OwnedEntry) + 1)
    else if Length(After) > Length(OwnedEntry) then
      Delete(After, 1, Length(OwnedEntry) + 1)
    else
      After := '';
    RecordValue := 'R' + GetSHA256OfUnicodeString(Value) +
      GetSHA256OfUnicodeString(After) + OwnedEntry;
    PathWrite(Root, OwnerKey, OwnerName, RecordValue, PathRegSz);
    PathWriteIfUnchanged(Root, EnvironmentKey, Value, After, Kind, Exists);
    PathDeleteOwner(Root, OwnerKey, OwnerName);
    Log('Removed installer-owned uniget PATH entry.');
  end;
end;

procedure UpdateUnigetPath(Selected: Boolean);
var
  Root: Integer;
  EnvironmentKey, Scope: String;
  Response: THandle;
begin
  if IsAdminInstallMode then
  begin
    Root := HKEY_LOCAL_MACHINE;
    EnvironmentKey := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
    Scope := 'HKLM (all users)';
  end
  else
  begin
    Root := HKEY_CURRENT_USER;
    EnvironmentKey := 'Environment';
    Scope := 'HKCU (current user)';
  end;
  try
    Log('Updating uniget PATH task: ' + Scope);
    PathUpdate(Root, EnvironmentKey, PathOwnerKey, ExpandConstant('{app}'), Selected);
  except
    UnigetPathUpdateFailed := True;
    Log('ERROR updating uniget PATH: ' + GetExceptionMessage);
    SuppressibleMsgBox(FmtMessage(CustomMessage('UnigetPathError'), [Scope, GetExceptionMessage]),
      mbError, MB_OK, IDOK);
  end;
  // Also broadcast on partial failures: PATH may have changed before finalizing
  // ownership failed. Existing terminals retain their old environment regardless.
  if PathSendEnvironmentChange(HWND_BROADCAST, $001A, 0, 'Environment',
       $0002, 5000, Response) = 0 then
  begin
    Log('WARNING: uniget PATH environment-change broadcast failed or timed out.');
    SuppressibleMsgBox(CustomMessage('UnigetPathBroadcastError'), mbError, MB_OK, IDOK);
  end;
end;
