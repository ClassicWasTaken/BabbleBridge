; Copyright (c) 2026 Frame Bridge contributors.
; SPDX-License-Identifier: Apache-2.0

; Babble Bridge per-user installer. Keep BuildIdentity stable for every release.
; Settings and module releases live outside {app} and are never removed here.
#ifndef AppPayload
  #define AppPayload "..\dist\BabbleFrameBridge"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#ifndef BuildIdentity
  #define BuildIdentity "FrameBridge.Desktop.8E250247-8BA9-42B7-A698-94D2A52E880A"
#endif
#ifndef ProductName
  #define ProductName "Babble Bridge"
#endif
#ifndef DisplayName
  #define DisplayName "Babble Bridge"
#endif
#ifndef AppMutexName
  #define AppMutexName "Local\FrameBridge.Desktop"
#endif
#ifndef IconDesktopDir
  #define IconDesktopDir "{autodesktop}"
#endif
#ifndef IconGroupDir
  #define IconGroupDir "{group}"
#endif
#define HeadsetIconPath "Assets\Brand\FrameBridge-headset-v1.ico"

[Setup]
AppId={#BuildIdentity}
AppName={#DisplayName}
AppVersion={#AppVersion}
AppVerName={#DisplayName} {#AppVersion}
AppPublisher=Babble Bridge contributors
AppComments=Independent bridge for headset tracking and VRChat.
AppCopyright=Independent application. See LICENSE and NOTICE.txt.
DefaultDirName={localappdata}\Programs\Babble Bridge
DefaultGroupName={#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UsePreviousAppDir=yes
UsePreviousGroup=no
UsePreviousTasks=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
AllowNoIcons=yes
Uninstallable=yes
UninstallLogMode=append
UninstallDisplayName={#DisplayName}
UninstallDisplayIcon={app}\{#HeadsetIconPath}
AppMutex={#AppMutexName}
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=yes
AlwaysRestart=no
SetupLogging=yes
WizardStyle=modern dynamic windows11 hidebevels
WizardBackColor=#FAF8F5
WizardBackColorDynamicDark=#101C18
WizardImageFile=assets\wizard-panel.png
WizardImageBackColor=#347A66
WizardSmallImageFile=..\src\BabbleFrameBridge.App\Assets\Brand\bridge-logo.png
WizardSmallImageBackColor=#FAF8F5
WizardSmallImageBackColorDynamicDark=#101C18
WizardImageStretch=yes
WizardSizePercent=115,115
SetupIconFile=..\src\BabbleFrameBridge.App\Assets\Bridge.ico
OutputDir=..\dist\installer
OutputBaseFilename=Babble-Bridge-Setup-{#AppVersion}
Compression=lzma2/normal
SolidCompression=yes
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#DisplayName}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription={#DisplayName} Installer
VersionInfoCompany=Babble Bridge contributors

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#AppPayload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[InstallDelete]
; Releases before 1.4 included this module archive in the application folder.
; Imported modules are stored separately in AppData and are preserved.
Type: files; Name: "{app}\Assets\Baballonia-module.zip"
; Retire the former copied runtime and official branding assets from the app
; folder only. New original artwork/provenance is installed by [Files].
Type: files; Name: "{app}\vcruntime140.dll"
Type: files; Name: "{app}\runtime\vcruntime140.dll"
Type: files; Name: "{app}\Assets\Brand\logo.png"
Type: files; Name: "{app}\Assets\Brand\logo.svg"
Type: files; Name: "{app}\Assets\Brand\logo-white.svg"
Type: files; Name: "{app}\Assets\Brand\logo_silhouette.svg"
Type: files; Name: "{app}\Assets\Brand\babbleBanner.png"
Type: files; Name: "{app}\Assets\Brand\BabbleLogos.xaml"
Type: files; Name: "{app}\Assets\Brand\Babble-Brand-LICENSE.txt"
Type: files; Name: "{app}\Assets\Brand\SOURCE.txt"

[Icons]
Name: "{#IconGroupDir}\{#ProductName}"; Filename: "{app}\BabbleFrameBridge.exe"; WorkingDir: "{app}"; IconFilename: "{app}\{#HeadsetIconPath}"; IconIndex: 0; Check: ShouldHaveStartShortcut; AppUserModelID: "FrameBridge.Desktop"
Name: "{#IconGroupDir}\Setup guide"; Filename: "{app}\START-HERE.txt"; Check: ShouldHaveGuideShortcut
Name: "{#IconDesktopDir}\{#ProductName}"; Filename: "{app}\BabbleFrameBridge.exe"; WorkingDir: "{app}"; IconFilename: "{app}\{#HeadsetIconPath}"; IconIndex: 0; Check: ShouldHaveDesktopShortcut; AppUserModelID: "FrameBridge.Desktop"

[Run]
Filename: "{app}\BabbleFrameBridge.exe"; Description: "Open &Babble Bridge"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Messages]
WelcomeLabel1=Meet Babble Bridge.
WelcomeLabel2=Your headset expressions, connected to VRChat.%n%nSetup will install this independent bridge for your Windows account.%n%nThe Microsoft Visual C++ v14 Redistributable (x64) must be installed separately from Microsoft.%n%nFuture versions can be installed over this one. Your settings and imported modules stay in place.
FinishedHeadingLabel=You’re ready to express yourself.
FinishedLabel=Babble Bridge is installed.%n%nOpen Modules to download an official module or import your own ZIP, then connect your tracking provider.%n%nExisting imported modules are preserved. To update the app later, run its newer setup file. To update only the module, import a new ZIP inside the app.
BeveledLabel=Independent runtime bridge

[Code]
procedure SHChangeNotify(EventId: Integer; Flags: Cardinal; Item1: String; Item2: UINT_PTR);
  external 'SHChangeNotify@shell32.dll stdcall';

function IsAppShortcut(const Filename, Target: String): Boolean;
var
  Shell, Shortcut: Variant;
begin
  Result := False;
  if not FileExists(Filename) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(Filename);
    Result := CompareText(ExpandFileName(Shortcut.TargetPath), ExpandFileName(Target)) = 0;
  except
    Log('Could not inspect a former app shortcut: ' + Filename);
  end;
end;

procedure RetireShortcut(const Filename, Target: String);
begin
  { Only this app's exact former shortcut names, with this installation's
    actual target, are eligible for cleanup. Other files are left alone. }
  if ('{#BuildIdentity}' = 'FrameBridge.Desktop.8E250247-8BA9-42B7-A698-94D2A52E880A')
    and IsAppShortcut(Filename, Target) then DeleteFile(Filename);
end;

function ShouldHaveDesktopShortcut: Boolean;
var
  Filename, Target: String;
begin
  { Refresh an existing app shortcut even if an older setup did not record the
    optional task. Do not create a new desktop shortcut unless it was selected. }
  Filename := ExpandConstant('{#IconDesktopDir}\{#ProductName}.lnk');
  Target := ExpandConstant('{app}\BabbleFrameBridge.exe');
  if FileExists(Filename) and not IsAppShortcut(Filename, Target) then begin
    Log('Preserving a different application''s desktop shortcut: ' + Filename);
    Result := False;
    Exit;
  end;
  Result := WizardIsTaskSelected('desktopicon') or IsAppShortcut(Filename, Target) or
    (('{#BuildIdentity}' = 'FrameBridge.Desktop.8E250247-8BA9-42B7-A698-94D2A52E880A')
      and IsAppShortcut(ExpandConstant('{#IconDesktopDir}\Frame Bridge.lnk'), Target));
end;

function ShouldHaveStartShortcut: Boolean;
var
  Filename: String;
begin
  Filename := ExpandConstant('{#IconGroupDir}\{#ProductName}.lnk');
  Result := not FileExists(Filename) or IsAppShortcut(Filename, ExpandConstant('{app}\BabbleFrameBridge.exe'));
  if not Result then Log('Preserving a different application''s Start menu shortcut: ' + Filename);
end;

function ShouldHaveGuideShortcut: Boolean;
var
  Filename: String;
begin
  Filename := ExpandConstant('{#IconGroupDir}\Setup guide.lnk');
  Result := not FileExists(Filename) or IsAppShortcut(Filename, ExpandConstant('{app}\START-HERE.txt'));
  if not Result then Log('Preserving a different application''s guide shortcut: ' + Filename);
end;

procedure RefreshShellItem(const Filename: String);
begin
  if FileExists(Filename) then
    { SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH. This refresh is limited to
      this application's item; no global icon cache or Explorer restart. }
    SHChangeNotify($00002000, $00001005, Filename, 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  OldGroup: String;
begin
  if CurStep = ssPostInstall then begin
    { Inno recreates its uninstall key after processing [Registry] entries.
      Write the generation only after that key has been saved, or an upgrade
      would silently lose the marker and permit a later downgrade. }
    if not RegWriteDWordValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#BuildIdentity}_is1',
      'BabbleBridgeReleaseGeneration', 2) then
      RaiseException('Setup could not record the public release generation. Run Setup again before installing another release.');
    Log('Recorded public release generation 2 after saving uninstall information.');
    if '{#BuildIdentity}' = 'FrameBridge.Desktop.8E250247-8BA9-42B7-A698-94D2A52E880A' then begin
      { Retire an old link only after its replacement was actually created.
        A name collision with another app must not remove the only owned link. }
      if IsAppShortcut(ExpandConstant('{#IconDesktopDir}\{#ProductName}.lnk'),
        ExpandConstant('{app}\BabbleFrameBridge.exe')) then
        RetireShortcut(ExpandConstant('{#IconDesktopDir}\Frame Bridge.lnk'),
          ExpandConstant('{app}\BabbleFrameBridge.exe'));
      OldGroup := ExpandConstant('{autoprograms}\Frame Bridge');
      if IsAppShortcut(ExpandConstant('{#IconGroupDir}\{#ProductName}.lnk'),
        ExpandConstant('{app}\BabbleFrameBridge.exe')) then
        RetireShortcut(OldGroup + '\Frame Bridge.lnk', ExpandConstant('{app}\BabbleFrameBridge.exe'));
      if IsAppShortcut(ExpandConstant('{#IconGroupDir}\Setup guide.lnk'),
        ExpandConstant('{app}\START-HERE.txt')) then
        RetireShortcut(OldGroup + '\Setup guide.lnk', ExpandConstant('{app}\START-HERE.txt'));
      RemoveDir(OldGroup); { Nonrecursive: preserve the folder if it has any other files. }
    end;
    RefreshShellItem(ExpandConstant('{app}\{#HeadsetIconPath}'));
    RefreshShellItem(ExpandConstant('{app}\BabbleFrameBridge.exe'));
    RefreshShellItem(ExpandConstant('{#IconGroupDir}\{#ProductName}.lnk'));
    RefreshShellItem(ExpandConstant('{#IconDesktopDir}\{#ProductName}.lnk'));
  end;
end;

function NextVersionPart(var Remaining: String): Integer;
var
  Dot: Integer;
  Part: String;
begin
  Dot := Pos('.', Remaining);
  if Dot = 0 then begin
    Part := Remaining;
    Remaining := '';
  end else begin
    Part := Copy(Remaining, 1, Dot - 1);
    Delete(Remaining, 1, Dot);
  end;
  Result := StrToIntDef(Part, 0);
end;

function VersionIsNewer(First, Second: String): Boolean;
var
  Index, FirstPart, SecondPart: Integer;
begin
  Result := False;
  for Index := 0 to 3 do begin
    FirstPart := NextVersionPart(First);
    SecondPart := NextVersionPart(Second);
    if FirstPart > SecondPart then begin
      Result := True;
      Exit;
    end;
    if FirstPart < SecondPart then Exit;
  end;
end;

function IsKnownDevelopmentBuild(const Version, InstalledName: String): Boolean;
begin
  { Only exact versions bearing the former development product names can
    transition to the first stable series. A future stable release with the
    same version number must still receive normal downgrade protection. }
  Result := ((CompareText(InstalledName, 'Frame Bridge') = 0) or
    (CompareText(InstalledName, 'Steam Frame Babble Bridge') = 0)) and
    ((Version = '1.2.0') or (Version = '1.3.0') or
    (Version = '1.4.0') or (Version = '1.4.1') or (Version = '1.4.2') or
    (Version = '1.5.0') or (Version = '1.5.1'));
end;

function InitializeSetup: Boolean;
var
  InstalledVersion, InstalledName: String;
  ReleaseGeneration: Cardinal;
begin
  Result := True;
  if RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#BuildIdentity}_is1',
    'DisplayVersion', InstalledVersion) then begin
    if VersionIsNewer(InstalledVersion, '{#AppVersion}') then begin
      InstalledName := '';
      RegQueryStringValue(HKCU,
        'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#BuildIdentity}_is1',
        'DisplayName', InstalledName);
      { The private preview series used 1.0.1–1.0.5. Users may skip the
        first public installer, so allow migration throughout public 1.0.x,
        only while the installation still predates generation 2. }
      if (Pos('1.0.', '{#AppVersion}') = 1) and (CompareText(InstalledName, 'Babble Bridge') = 0) and
        ((InstalledVersion = '1.0.1') or (InstalledVersion = '1.0.2') or
         (InstalledVersion = '1.0.3') or (InstalledVersion = '1.0.4') or (InstalledVersion = '1.0.5')) and
        not RegQueryDWordValue(HKCU,
          'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#BuildIdentity}_is1',
          'BabbleBridgeReleaseGeneration', ReleaseGeneration) then begin
        Log('Migrating the private preview series to public release {#AppVersion}.');
        Exit;
      end;
      if (Pos('1.0.', '{#AppVersion}') = 1) and
        IsKnownDevelopmentBuild(InstalledVersion, InstalledName) then begin
        Log('Transitioning a known development installation to the first stable release.');
        Exit;
      end;
      SuppressibleMsgBox('Babble Bridge ' + InstalledVersion +
        ' is already installed. Use the same or a newer setup file to keep your app up to date.',
        mbInformation, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

