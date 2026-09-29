; Executable regression harness. NEVER calls UpdateUnigetPath or the real installer.
; Validation runner supplies a unique HKCU test subkey and session artifact directory.
#ifndef TestRegistryKey
  #error TestRegistryKey must be an isolated test registry subkey
#endif
#ifndef TestOutput
  #error TestOutput must be a session artifact directory
#endif
[Setup]
#ifdef TestAppId
AppId={#TestAppId}
#else
AppId=UniGetUI-Isolated-Path-Tests
#endif
AppName=UniGetUI isolated PATH tests
AppVersion=1
DefaultDirName={tmp}\UniGetUI-Path-Tests
PrivilegesRequired=lowest
Uninstallable=no
CreateAppDir=no
OutputDir={#TestOutput}
OutputBaseFilename=PathTests
Compression=none
SetupLogging=yes
UsePreviousTasks=yes
[CustomMessages]
UnigetPathError=PATH error: %1 %2
UnigetPathBroadcastError=Environment broadcast failed
InvalidAddToPathProperty=Invalid installer command line: %1
InstallType=Installation type
ShCuts=Shortcuts
PortInst=Portable install
RegInst=Regular install
RegStartMmenuIcon=Start menu shortcut
RegDesktopIcon=Desktop shortcut
RegAddToPath=Add the uniget command to PATH
#ifdef TestTasksFile
#include TestTasksFile
#endif
[Code]
#include "AddToPath.iss"
#include "AddToPathTask.iss"

const
  TestRoot = '{#TestRegistryKey}';
  TestEntry = 'C:\UniGetUI PATH Test';
var
  Assertions: Integer;

procedure AssertPath(Condition: Boolean; Message: String);
begin
  if not Condition then RaiseException('FAILED: ' + Message);
  Assertions := Assertions + 1;
end;

function TestEnv: String;
begin
  Result := TestRoot + '\Environment';
end;

function TestOwners: String;
begin
  Result := TestRoot + '\Owners';
end;

function TestOwnerName(Entry: String): String;
begin
  Result := GetSHA256OfUnicodeString(PathNormalize(Entry, PathRegSz));
end;

procedure CheckValue(Key, Expected: String; ExpectedKind: LongWord);
var
  Actual: String;
  Kind: LongWord;
  Exists: Boolean;
begin
  PathRead(HKCU, Key, 'Path', Actual, Kind, Exists);
  AssertPath(Exists, 'PATH exists');
  AssertPath(Actual = Expected, 'PATH preserved byte-for-byte');
  AssertPath(Kind = ExpectedKind, 'registry type preserved exactly');
end;

procedure CheckOwned(Expected: Boolean);
var
  RecordValue: String;
begin
  AssertPath(RegValueExists(HKCU64, TestOwners, TestOwnerName(TestEntry)) = Expected,
    'ownership record presence');
  if Expected then
  begin
    AssertPath(RegQueryStringValue(HKCU64, TestOwners, TestOwnerName(TestEntry), RecordValue),
      'ownership record readable across upgrades');
    AssertPath(RecordValue = 'O' + TestEntry, 'committed ownership remains unchanged');
  end;
end;

procedure ResetPath(Value: String; Kind: LongWord);
begin
  AssertPath(RegDeleteKeyIncludingSubkeys(HKCU64, TestRoot) or
    not RegKeyExists(HKCU64, TestRoot), 'reset isolated keys');
  if Kind = PathRegSz then
    AssertPath(RegWriteStringValue(HKCU64, TestEnv, 'Path', Value), 'seed REG_SZ')
  else
    AssertPath(RegWriteExpandStringValue(HKCU64, TestEnv, 'Path', Value), 'seed REG_EXPAND_SZ');
end;

procedure UpdatePath(Selected: Boolean);
begin
  PathUpdate(HKCU, TestEnv, TestOwners, TestEntry, Selected);
end;

procedure TestKindsAndUpgrades;
var
  Kind: LongWord;
  Before, Added, UserEdited: String;
begin
  Before := '  "C:\Keep One\"  ;;%SystemRoot%\System32;C:\Keep TWO\;C:\Données\工具😀;';
  Added := Before + ';' + TestEntry;
  for Kind := PathRegSz to PathRegExpandSz do
  begin
    ResetPath(Before, Kind);
    UpdatePath(False); // Opt-out with no ownership is a no-op.
    CheckValue(TestEnv, Before, Kind);
    UpdatePath(True);
    CheckValue(TestEnv, Added, Kind);
    CheckOwned(True);
    UpdatePath(True); // Upgrade retains ownership and does not append again.
    CheckValue(TestEnv, Added, Kind);
    CheckOwned(True);
    UserEdited := 'C:\UserBefore;' + Added + '; "C:\UserAfter\" ';
    PathWrite(HKCU, TestEnv, 'Path', UserEdited, Kind);
    UpdatePath(False); // Deselection / uninstall preserves unrelated new segments.
    CheckValue(TestEnv, 'C:\UserBefore;' + Before + '; "C:\UserAfter\" ', Kind);
    CheckOwned(False);
    UpdatePath(False); // Repeated uninstall cannot remove anything else.
    CheckValue(TestEnv, 'C:\UserBefore;' + Before + '; "C:\UserAfter\" ', Kind);
  end;
  // A PATH much longer than setx's historic limit must not be truncated.
  Before := '';
  while Length(Before) < 40000 do Before := Before + 'C:\Long unrelated segment;';
  ResetPath(Before, PathRegSz);
  UpdatePath(True);
  CheckValue(TestEnv, Before + ';' + TestEntry, PathRegSz);
  UpdatePath(False);
  CheckValue(TestEnv, Before, PathRegSz);
end;

procedure TestPreexistingAndEdited;
var
  Kind: LongWord;
  Before, Entry: String;
begin
  Before := 'C:\Other;  "' + Uppercase(TestEntry) + '/"  ;;C:\Last;';
  for Kind := PathRegSz to PathRegExpandSz do
  begin
    ResetPath(Before, Kind);
    UpdatePath(True);
    CheckValue(TestEnv, Before, Kind);
    CheckOwned(False);
    UpdatePath(True);
    UpdatePath(False);
    CheckValue(TestEnv, Before, Kind);
    CheckOwned(False);
  end;
  Entry := ExpandConstant('{win}') + '\UniGetUI PATH Test';
  Before := 'C:\Other; "%SystemRoot%\UniGetUI PATH Test\" ';
  ResetPath(Before, PathRegExpandSz);
  PathUpdate(HKCU, TestEnv, TestOwners, Entry, True);
  CheckValue(TestEnv, Before, PathRegExpandSz);
  AssertPath(not RegValueExists(HKCU64, TestOwners, TestOwnerName(Entry)),
    'expanded duplicate is not claimed');
  ResetPath(Before, PathRegSz);
  PathUpdate(HKCU, TestEnv, TestOwners, Entry, True);
  CheckValue(TestEnv, Before + ';' + Entry, PathRegSz); // REG_SZ does not expand %variables%.
  PathUpdate(HKCU, TestEnv, TestOwners, Entry, False);
  CheckValue(TestEnv, Before, PathRegSz);

  ResetPath('C:\Keep', PathRegSz);
  UpdatePath(True);
  Before := 'C:\Keep;"' + Uppercase(TestEntry) + '\"';
  PathWrite(HKCU, TestEnv, 'Path', Before, PathRegSz);
  UpdatePath(False);
  CheckValue(TestEnv, Before, PathRegSz);
  CheckOwned(False); // A reformatted owned entry becomes user-owned.

  ResetPath('C:\Keep', PathRegSz);
  UpdatePath(True);
  Before := 'C:\Keep;' + TestEntry + ';' + Uppercase(TestEntry) + '\';
  PathWrite(HKCU, TestEnv, 'Path', Before, PathRegSz);
  UpdatePath(False);
  CheckValue(TestEnv, Before, PathRegSz);
  CheckOwned(False); // Never choose arbitrarily between equivalent duplicates.

  ResetPath('C:\Keep', PathRegSz);
  UpdatePath(True);
  PathWrite(HKCU, TestEnv, 'Path', 'C:\Keep', PathRegSz);
  UpdatePath(True); // User removed it; explicitly opting back in can add it afresh.
  CheckValue(TestEnv, 'C:\Keep;' + TestEntry, PathRegSz);
  UpdatePath(False);
  CheckValue(TestEnv, 'C:\Keep', PathRegSz);
end;

procedure TestAbsentEmptyAndScopes;
var
  Kind: LongWord;
  Exists: Boolean;
  Value: String;
begin
  ResetPath('', PathRegSz);
  UpdatePath(True);
  CheckValue(TestEnv, TestEntry, PathRegSz);
  UpdatePath(False);
  CheckValue(TestEnv, '', PathRegSz);
  UpdatePath(True);
  PathWrite(HKCU, TestEnv, 'Path', TestEntry + '; "C:\UserAfter\" ;', PathRegSz);
  UpdatePath(False); // Owned entry is now first, not last.
  CheckValue(TestEnv, ' "C:\UserAfter\" ;', PathRegSz);
  PathWrite(HKCU, TestEnv, 'Path', '', PathRegSz);
  AssertPath(RegDeleteValue(HKCU64, TestEnv, 'Path'), 'delete isolated empty value');
  UpdatePath(True);
  CheckValue(TestEnv, TestEntry, PathRegExpandSz);
  UpdatePath(False);
  CheckValue(TestEnv, '', PathRegExpandSz);

  ResetPath('C:\UserScope', PathRegSz);
  PathWrite(HKCU, TestRoot + '\OtherScope\Environment', 'Path', 'C:\MachineScope', PathRegExpandSz);
  UpdatePath(True);
  PathUpdate(HKCU, TestRoot + '\OtherScope\Environment',
    TestRoot + '\OtherScope\Owners', TestEntry, False);
  CheckValue(TestRoot + '\OtherScope\Environment', 'C:\MachineScope', PathRegExpandSz);
  CheckOwned(True);
  PathUpdate(HKCU, TestRoot + '\OtherScope\Environment',
    TestRoot + '\OtherScope\Owners', TestEntry, True);
  UpdatePath(False);
  CheckValue(TestEnv, 'C:\UserScope', PathRegSz);
  CheckValue(TestRoot + '\OtherScope\Environment', 'C:\MachineScope;' + TestEntry, PathRegExpandSz);
  PathUpdate(HKCU, TestRoot + '\OtherScope\Environment',
    TestRoot + '\OtherScope\Owners', TestEntry, False);
  CheckValue(TestRoot + '\OtherScope\Environment', 'C:\MachineScope', PathRegExpandSz);
  PathRead(HKCU, TestRoot + '\DoesNotExist', 'Path', Value, Kind, Exists);
  AssertPath(not Exists, 'genuinely missing key recognized');
end;

procedure TestInterruptedOperations;
var
  Operation: String;
  I, Stage: Integer;
  Before, After, Current, Expected: String;
begin
  for I := 0 to 1 do
  begin
    if I = 0 then Operation := 'P' else Operation := 'R';
    if I = 0 then
    begin
      Before := 'C:\Keep';
      After := Before + ';' + TestEntry;
    end
    else
    begin
      Before := 'C:\Keep;' + TestEntry;
      After := 'C:\Keep';
    end;
    for Stage := 0 to 2 do
    begin
      if Stage = 0 then Current := Before
      else if Stage = 1 then Current := After
      else Current := 'C:\UserChange;' + TestEntry;
      ResetPath(Current, PathRegExpandSz);
      PathWrite(HKCU, TestOwners, TestOwnerName(TestEntry),
        Operation + GetSHA256OfUnicodeString(Before) +
        GetSHA256OfUnicodeString(After) + TestEntry, PathRegSz);
      UpdatePath(False);
      if Stage = 2 then Expected := Current else Expected := 'C:\Keep';
      CheckValue(TestEnv, Expected, PathRegExpandSz);
      CheckOwned(False);
    end;
  end;
end;

procedure TestFailures;
var
  Failed: Boolean;
  Number: Cardinal;
begin
  ResetPath('C:\Keep', PathRegSz);
  AssertPath(RegWriteDWordValue(HKCU64, TestEnv, 'Path', 42), 'seed unsupported type');
  Failed := False;
  try UpdatePath(True); except Failed := True; Log(GetExceptionMessage); end;
  AssertPath(Failed, 'unsupported PATH type fails explicitly');
  AssertPath(RegQueryDWordValue(HKCU64, TestEnv, 'Path', Number) and (Number = 42),
    'unsupported PATH remains untouched');
  CheckOwned(False);
  ResetPath('C:\Keep', PathRegSz);
  Failed := False;
  try
    PathWriteIfUnchanged(HKCU, TestEnv, 'wrong snapshot', 'replacement', PathRegSz, True);
  except Failed := True; Log(GetExceptionMessage); end;
  AssertPath(Failed, 'concurrent PATH change is detected');
  CheckValue(TestEnv, 'C:\Keep', PathRegSz);
  Failed := False;
  try
    PathUpdate(HKCU, TestEnv, TestOwners, 'C:\Unsafe;Directory', True);
  except Failed := True; Log(GetExceptionMessage); end;
  AssertPath(Failed, 'unsafe install directory fails explicitly');
  CheckValue(TestEnv, 'C:\Keep', PathRegSz);
  PathUpdate(HKCU, TestEnv, TestOwners, 'C:\Unsafe;Directory', False);
  CheckValue(TestEnv, 'C:\Keep', PathRegSz); // An unselected task does not reject an ordinary install.
end;

procedure TestAccessDenied;
var
  I: Integer;
  Name: String;
  Failed: Boolean;
begin
  for I := 0 to 2 do
  begin
    if I = 0 then Name := 'ReadDenied'
    else if I = 1 then Name := 'WriteDenied'
    else Name := 'OwnerDenied';
    Failed := False;
    try
      PathUpdate(HKCU, TestRoot + '\' + Name + '\Environment',
        TestRoot + '\' + Name + '\Owners', TestEntry, True);
    except
      Failed := Pos('(Win32 5)', GetExceptionMessage) <> 0;
      Log(GetExceptionMessage);
    end;
    AssertPath(Failed, Name + ': access denied is reported, not treated as empty PATH');
  end;
end;

#ifdef TestTasksFile
var
  TaskReport: String;
  TaskReportWritten: Boolean;

procedure SaveTaskReport;
begin
  Log(TaskReport);
  TaskReportWritten := SaveStringToFile('{#TestOutput}\PathTaskTests.result.txt', TaskReport, False);
  if not TaskReportWritten then RaiseException('Cannot save task test report');
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Regular, Portable, StartMenu, Desktop, PathSelected: Boolean;
begin
  if CurPageID <> wpSelectTasks then Exit;
  try
    Regular := WizardIsTaskSelected('regularinstall');
    Portable := WizardIsTaskSelected('portableinstall');
    StartMenu := WizardIsTaskSelected('regularinstall\startmenuicon');
    Desktop := WizardIsTaskSelected('regularinstall\desktopicon');
    TaskReport := 'BeforePath=' + IntToStr(Ord(WizardIsTaskSelected('regularinstall\addtopath'))) + #13#10;
    ApplyUnigetPathTaskAlias; // Same task-page hook as the production installer.
    AssertPath(WizardIsTaskSelected('regularinstall') = Regular, 'alias preserves regular choice');
    AssertPath(WizardIsTaskSelected('portableinstall') = Portable, 'alias preserves portable choice');
    AssertPath(WizardIsTaskSelected('regularinstall\startmenuicon') = StartMenu, 'alias preserves Start menu task');
    AssertPath(WizardIsTaskSelected('regularinstall\desktopicon') = Desktop, 'alias preserves desktop task');
    PathSelected := WizardIsTaskSelected('regularinstall\addtopath');
    TaskReport := TaskReport + 'Path=' + IntToStr(Ord(PathSelected)) + #13#10 +
      'Regular=' + IntToStr(Ord(Regular)) + #13#10 +
      'Portable=' + IntToStr(Ord(Portable)) + #13#10 +
      'StartMenu=' + IntToStr(Ord(StartMenu)) + #13#10 +
      'Desktop=' + IntToStr(Ord(Desktop)) + #13#10 +
      'Alias=' + IntToStr(Ord(UnigetPathAliasPresent)) + #13#10;
    // Exercise the production regular/portable gate, but with isolated keys ONLY.
    if Regular then
      PathUpdate(HKCU, TestRoot + '\TaskEnvironment', TestRoot + '\TaskOwners', TestEntry, PathSelected);
    if Regular then
    begin
      // Simulate a subsequent interactive checkbox edit. Startup must not reapply.
      if PathSelected then WizardSelectTasks('!regularinstall\addtopath')
      else WizardSelectTasks('regularinstall\addtopath');
      ApplyUnigetPathTaskAlias;
      AssertPath(WizardIsTaskSelected('regularinstall\addtopath') <> PathSelected,
        'interactive checkbox edit wins over startup alias');
      if PathSelected then WizardSelectTasks('regularinstall\addtopath')
      else WizardSelectTasks('!regularinstall\addtopath');
    end;
    TaskReport := 'PASS: task wiring' + #13#10 + TaskReport;
  except
    TaskReport := 'FAILED: ' + GetExceptionMessage;
  end;
  SaveTaskReport;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  // Visit the real task page, then cancel before any install action.
  Result := CurPageID <> wpSelectTasks;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := 'Isolated task harness must never install.';
end;
#endif

function InitializeSetup: Boolean;
var
  Report, ReportFile: String;
begin
  Result := False; // Tests only; NEVER install files or register an uninstaller.
#ifdef TestTasksFile
  Result := InitializeUnigetPathTaskAlias;
  if not Result then
  begin
    TaskReport := 'REJECTED: invalid ADDTOPATH';
    SaveTaskReport;
  end;
  Exit;
#endif
  ReportFile := '{#TestOutput}\PathTests.result.txt';
  if ParamStr(ParamCount) = '/ACLTEST' then
    ReportFile := '{#TestOutput}\PathTests-acl.result.txt';
  try
    if ParamStr(ParamCount) = '/ACLTEST' then
      TestAccessDenied
    else
    begin
      TestKindsAndUpgrades;
      TestPreexistingAndEdited;
      TestAbsentEmptyAndScopes;
      TestInterruptedOperations;
      TestFailures;
      // Leave two fixture values for independent .NET RegistryKey verification.
      PathWrite(HKCU, TestRoot + '\CheckSz', 'Path', '%SystemRoot%; "C:\Keep\" ;;', PathRegSz);
      PathWrite(HKCU, TestRoot + '\CheckExpandSz', 'Path', '%SystemRoot%; "C:\Keep\" ;;', PathRegExpandSz);
    end;
    Report := 'PASS: ' + IntToStr(Assertions) + ' assertions';
  except
    Report := GetExceptionMessage;
  end;
  Log(Report);
  if not SaveStringToFile(ReportFile, Report, False) then
    RaiseException('Cannot save test result');
end;
