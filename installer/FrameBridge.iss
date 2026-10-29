; Copyright (c) 2026 Frame Bridge contributors.
; SPDX-License-Identifier: Apache-2.0

; Babble Bridge per-user installer. Keep BuildIdentity stable for every release.
; Settings and module releases live outside {app} and are never removed here.
#ifndef AppPayload
  #define AppPayload "..\dist\BabbleFrameBridge"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.3"
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
#define HeadsetIconPath "Assets\Brand\FrameBridge-headset-v1.ico"

[Setup]
AppId={#BuildIdentity}
AppName={#DisplayName}
AppVersion={#AppVersion}
AppVerName={#DisplayName} {#AppVersion}
AppPublisher=Babble Bridge contributors
AppComments=Independent bridge for Babble Frame Runtime and VRChat.
AppCopyright=Independent application. See LICENSE and NOTICE.txt.
DefaultDirName={localappdata}\Programs\Frame Bridge
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
OutputBaseFilename=FrameBridge-Setup-{#AppVersion}
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
Name: "{group}\{#ProductName}"; Filename: "{app}\BabbleFrameBridge.exe"; WorkingDir: "{app}"; IconFilename: "{app}\{#HeadsetIconPath}"; IconIndex: 0; AppUserModelID: "FrameBridge.Desktop"
Name: "{group}\Setup guide"; Filename: "{app}\START-HERE.txt"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\BabbleFrameBridge.exe"; WorkingDir: "{app}"; IconFilename: "{app}\{#HeadsetIconPath}"; IconIndex: 0; Check: ShouldHaveDesktopShortcut; AppUserModelID: "FrameBridge.Desktop"

[Run]
Filename: "{app}\BabbleFrameBridge.exe"; Description: "Open &Babble Bridge"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Messages]
WelcomeLabel1=Meet Babble Bridge.
WelcomeLabel2=Your Steam Frame expressions, connected to VRChat.%n%nSetup will install this independent bridge for your Windows account.%n%nThe Microsoft Visual C++ v14 Redistributable (x64) must be installed separately from Microsoft.%n%nFuture versions can be installed over this one. Your settings and Baballonia modules stay in place.
FinishedHeadingLabel=You’re ready to express yourself.
FinishedLabel=Babble Bridge is installed.%n%nOpen Babble Bridge and add your own Baballonia module ZIP, then start Babble Frame Runtime on your headset.%n%nExisting imported modules are preserved. To update the app later, run its newer setup file. To update only the module, import a new ZIP inside the app.
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
begin
  { Refresh an existing app shortcut even if an older setup did not record the
    optional task. Do not create a new desktop shortcut unless it was selected. }
  Result := WizardIsTaskSelected('desktopicon') or
    FileExists(ExpandConstant('{autodesktop}\{#ProductName}.lnk')) or
    (('{#BuildIdentity}' = 'FrameBridge.Desktop.8E250247-8BA9-42B7-A698-94D2A52E880A')
      and IsAppShortcut(ExpandConstant('{autodesktop}\Frame Bridge.lnk'),
        ExpandConstant('{app}\BabbleFrameBridge.exe')));
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
    if '{#BuildIdentity}' = 'FrameBridge.Desktop.8E250247-8BA9-42B7-A698-94D2A52E880A' then begin
      RetireShortcut(ExpandConstant('{autodesktop}\Frame Bridge.lnk'),
        ExpandConstant('{app}\BabbleFrameBridge.exe'));
      OldGroup := ExpandConstant('{autoprograms}\Frame Bridge');
      RetireShortcut(OldGroup + '\Frame Bridge.lnk', ExpandConstant('{app}\BabbleFrameBridge.exe'));
      RetireShortcut(OldGroup + '\Setup guide.lnk', ExpandConstant('{app}\START-HERE.txt'));
      RemoveDir(OldGroup); { Nonrecursive: preserve the folder if it has any other files. }
    end;
    RefreshShellItem(ExpandConstant('{app}\{#HeadsetIconPath}'));
    RefreshShellItem(ExpandConstant('{app}\BabbleFrameBridge.exe'));
    RefreshShellItem(ExpandConstant('{group}\{#ProductName}.lnk'));
    RefreshShellItem(ExpandConstant('{autodesktop}\{#ProductName}.lnk'));
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

function IsKnownDevelopmentBuild(const Version: String): Boolean;
begin
  { The first stable release resets the development numbering. Only these
    exact development versions may transition to 1.0.0 through 1.0.3; future stable
    versions still use the normal downgrade guard. }
  Result := (Version = '1.2.0') or (Version = '1.3.0') or
    (Version = '1.4.0') or (Version = '1.4.1') or (Version = '1.4.2') or
    (Version = '1.5.0') or (Version = '1.5.1');
end;

function InitializeSetup: Boolean;
var
  InstalledVersion: String;
begin
  Result := True;
  if RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#BuildIdentity}_is1',
    'DisplayVersion', InstalledVersion) then begin
    if VersionIsNewer(InstalledVersion, '{#AppVersion}') then begin
      if (('{#AppVersion}' = '1.0.0') or ('{#AppVersion}' = '1.0.1') or ('{#AppVersion}' = '1.0.2') or ('{#AppVersion}' = '1.0.3')) and IsKnownDevelopmentBuild(InstalledVersion) then begin
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

