#requires -Version 7.0
# Copyright (c) 2026 Frame Bridge contributors.
# SPDX-License-Identifier: Apache-2.0

param(
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$ModuleZip,
    [string]$PreviousPayloadDirectory,
    [switch]$AllowSameVersionReplacement
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compilerPath = (Resolve-Path -LiteralPath $Compiler).ProviderPath
$payloadSource = (Resolve-Path -LiteralPath $PayloadDirectory).ProviderPath
$externalModuleZip = (Resolve-Path -LiteralPath $ModuleZip).ProviderPath
$previousPayload = if ($PreviousPayloadDirectory) { (Resolve-Path -LiteralPath $PreviousPayloadDirectory).ProviderPath } else { $null }
$workspace = [IO.Path]::GetFullPath($WorkDirectory)
$null = New-Item -ItemType Directory -Path $workspace -Force
$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $workspace "run-$runId"
$null = New-Item -ItemType Directory -Path $runRoot
$payload = Join-Path $runRoot 'payload'
$installPath = Join-Path $runRoot 'install'
$compiledPath = Join-Path $runRoot 'compiled'
$null = New-Item -ItemType Directory -Path $payload, $compiledPath
$testId = "FrameBridge.InstallerSmokeTest.$($runId.Substring(0, 12))"
$testMutex = "Local\FrameBridge.InstallerSmokeTest.$runId"
$testProductName = "FaceTrackingBridgeInstallerTest-$($runId.Substring(0, 12))"
$desktopIconDirectory = Join-Path $runRoot 'shortcuts/desktop'
$groupIconDirectory = Join-Path $runRoot 'shortcuts/start'
$desktopShortcut = Join-Path $desktopIconDirectory "$testProductName.lnk"
$startShortcut = Join-Path $groupIconDirectory "$testProductName.lnk"
$guideShortcut = Join-Path $groupIconDirectory 'Setup guide.lnk'
$legacyGroupParent = Join-Path $runRoot 'shortcuts/legacy-start'
$legacyNames = @('Frame Bridge', 'Steam Frame Babble Bridge', 'Babble Bridge', 'FaceTracking Bridge')
$foreignTarget = Join-Path $runRoot 'unrelated-tool.exe'
$foreignShortcutHash = $null
$registryKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${testId}_is1"
$userDataPath = Join-Path $env:LOCALAPPDATA 'BabbleFrameBridge'
$python = (Get-Command python.exe -CommandType Application | Select-Object -First 1).Source
$steamVrHelper = Join-Path $runRoot 'fake-steamvr-maintenance.py'
$steamVrProfile = Join-Path $runRoot 'steamvr-profile'
$steamVrSettings = Join-Path $steamVrProfile 'settings.json'
$steamVrEvents = Join-Path $steamVrProfile 'maintenance-events.jsonl'
$steamVrManifest = Join-Path $steamVrProfile 'steamvr/FaceTrackingBridge.vrmanifest'
$steamVrFailure = Join-Path $steamVrProfile 'fail-command.txt'
$checks = [Collections.Generic.List[string]]::new()
$failure = $null
$initialDisplayName = if ($previousPayload) {
    [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $previousPayload 'BabbleFrameBridge.exe')).ProductName
} else { 'Face Tracking Bridge' }
$expectedDisplayName = $initialDisplayName

function Get-PayloadVersion([string]$Directory) {
    $executable = Join-Path $Directory 'BabbleFrameBridge.exe'
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Missing app executable in payload: $Directory" }
    $text = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
    return ([Version]::Parse($text)).ToString(3)
}
$initialVersion = if ($previousPayload) { Get-PayloadVersion $previousPayload } else { '1.2.0' }
$upgradeVersion = if ($previousPayload) { Get-PayloadVersion $payloadSource } else { '1.2.1' }
$migrationTargetVersion = '1.0.0'
$generationReset = $upgradeVersion -eq '1.0.0' -and (
    ($initialDisplayName -eq 'Babble Bridge' -and $initialVersion -in @('1.0.1', '1.0.2')) -or
    ($initialDisplayName -eq 'FaceTracking Bridge' -and $initialVersion -eq '1.0.3'))
$sameVersionReplacement = $false
if ($AllowSameVersionReplacement) {
    if (!$previousPayload -or ([Version]$upgradeVersion -ne [Version]$initialVersion)) {
        throw 'AllowSameVersionReplacement requires previous and candidate payloads with the same numeric version.'
    }
    $previousAppHash = (Get-FileHash -LiteralPath (Join-Path $previousPayload 'BabbleFrameBridge.dll') -Algorithm SHA256).Hash
    $candidateAppHash = (Get-FileHash -LiteralPath (Join-Path $payloadSource 'BabbleFrameBridge.dll') -Algorithm SHA256).Hash
    if ($previousAppHash -eq $candidateAppHash) {
        throw 'AllowSameVersionReplacement requires a different candidate app binary; identical builds do not exercise replacement.'
    }
    $sameVersionReplacement = $true
}
if ($previousPayload -and !$generationReset -and !$sameVersionReplacement -and ([Version]$upgradeVersion -le [Version]$initialVersion)) {
    throw 'PreviousPayloadDirectory must contain an older app version or an exact known generation-2 reset source.'
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
function Assert-OwnedPath([string]$Path) {
    $canonical = [IO.Path]::GetFullPath($Path)
    if (!$canonical.StartsWith($runRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing operation outside the isolated test run: $canonical"
    }
}
function Add-Check([string]$Message) {
    $checks.Add($Message)
    Write-Output "PASS $Message"
}
function Read-SteamVrEvents {
    if (Test-Path -LiteralPath $steamVrEvents) {
        foreach ($line in Get-Content -LiteralPath $steamVrEvents) {
            if ($line) { $line | ConvertFrom-Json }
        }
    }
}
function Set-SteamVrOptIn([bool]$Enabled) {
    Assert-OwnedPath $steamVrSettings
    $settings = Get-Content -LiteralPath $steamVrSettings -Raw | ConvertFrom-Json
    $settings.StartWithSteamVr = $Enabled
    [IO.File]::WriteAllText($steamVrSettings, ($settings | ConvertTo-Json -Depth 6))
}
function Assert-SteamVrPreferences([bool]$ExpectedOptIn) {
    $settings = Get-Content -LiteralPath $steamVrSettings -Raw | ConvertFrom-Json
    Assert-True ($settings.StartWithSteamVr -eq $ExpectedOptIn) 'SteamVR opt-in was not preserved or cleared as expected.'
    Assert-True ($settings.StartMinimized -and $settings.CloseWithSteamVr) 'Installer changed unrelated launch preferences.'
    Assert-True $settings.KittyEnabled 'Installer changed the optional kitty preference.'
    Assert-True ($settings.ImportedModule -eq 'preserve-user-module') 'Installer changed the isolated imported-module fixture.'
}
function Assert-SteamVrRefresh([string]$ExpectedPayload) {
    $events = @(Read-SteamVrEvents)
    Assert-True ($events.Count -gt 0) 'Installer did not invoke its SteamVR maintenance hook.'
    $last = $events[-1]
    Assert-True ($last.command -eq '--refresh-steamvr-registration') 'Installer invoked the wrong SteamVR maintenance command.'
    Assert-True ($last.installed_executable_exists -and $last.installed_executable -eq (Join-Path $installPath 'BabbleFrameBridge.exe')) 'SteamVR refresh ran before the installed executable was available.'
    $expectedHash = (Get-FileHash -LiteralPath (Join-Path $ExpectedPayload 'BabbleFrameBridge.dll') -Algorithm SHA256).Hash
    Assert-True ($last.payload_sha256 -eq $expectedHash) 'SteamVR refresh did not run against the newly installed payload.'
}
function Assert-SteamVrUnregistered {
    $events = @(Read-SteamVrEvents)
    Assert-True ($events.Count -gt 0 -and $events[-1].command -eq '--unregister-steamvr') 'Uninstaller did not invoke the SteamVR cleanup command.'
    Assert-True $events[-1].installed_executable_exists 'SteamVR cleanup ran after the app executable was deleted.'
    Assert-True (!(Test-Path -LiteralPath $steamVrManifest)) 'Uninstall retained the isolated owned SteamVR manifest.'
    Assert-SteamVrPreferences $false
}
function New-TestShortcut([string]$Path, [string]$Target) {
    Assert-OwnedPath $Path
    Assert-OwnedPath $Target
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($Path)
        $shortcut.TargetPath = $Target
        $shortcut.Save()
    }
    finally {
        if ($shortcut) { $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) }
        $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}
function Get-TestShortcutTarget([string]$Path) {
    Assert-OwnedPath $Path
    Assert-True (Test-Path -LiteralPath $Path -PathType Leaf) "Missing test shortcut: $Path"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($Path)
        return [IO.Path]::GetFullPath($shortcut.TargetPath)
    }
    finally {
        if ($shortcut) { $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) }
        $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}
function Assert-ForeignShortcutUnchanged {
    Assert-True (Test-Path -LiteralPath $desktopShortcut -PathType Leaf) 'An unrelated desktop shortcut was removed.'
    Assert-True ((Get-FileHash -LiteralPath $desktopShortcut -Algorithm SHA256).Hash -eq $foreignShortcutHash) 'An unrelated desktop shortcut was overwritten.'
    Assert-True ((Get-TestShortcutTarget $desktopShortcut) -eq $foreignTarget) 'An unrelated shortcut target changed.'
}
function Assert-OwnedStartShortcuts {
    Assert-True ((Get-TestShortcutTarget $startShortcut) -eq (Join-Path $installPath 'BabbleFrameBridge.exe')) 'Start shortcut target is incorrect.'
    Assert-True ((Get-TestShortcutTarget $guideShortcut) -eq (Join-Path $installPath 'START-HERE.txt')) 'Setup-guide shortcut target is incorrect.'
}
function Assert-InstalledPayload([string]$ExpectedPayload) {
    foreach ($relative in @('BabbleFrameBridge.exe', 'BabbleFrameBridge.dll', 'runtime/BabbleFrameBridge.Worker.exe', 'runtime/BabbleFrameBridge.Worker.dll')) {
        $expectedHash = (Get-FileHash -LiteralPath (Join-Path $ExpectedPayload $relative) -Algorithm SHA256).Hash
        $actualHash = (Get-FileHash -LiteralPath (Join-Path $installPath $relative) -Algorithm SHA256).Hash
        Assert-True ($expectedHash -eq $actualHash) "Installed payload differs: $relative"
    }
}
function Invoke-TestProcess([string]$File, [string[]]$Arguments, [string]$LogName, [int]$TimeoutSeconds = 180) {
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw "Test process timed out: $LogName"
        }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $runRoot $LogName), $output)
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}
function Get-UserDataSnapshot {
    $files = [Collections.Generic.List[object]]::new()
    if (Test-Path -LiteralPath $userDataPath) {
        foreach ($file in @(Get-ChildItem -LiteralPath $userDataPath -File -Recurse | Sort-Object FullName)) {
            $relative = [IO.Path]::GetRelativePath($userDataPath, $file.FullName)
            # Activity logs can legitimately change while the real app is open.
            if ($relative -match '^logs[\\/]') { continue }
            $files.Add([ordered]@{ Path = $relative; Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash })
        }
    }
    return ConvertTo-Json -InputObject @($files.ToArray()) -Depth 5 -Compress
}
function Compile-TestInstaller([string]$Version, [string]$DisplayName = 'Face Tracking Bridge') {
    Write-Output "Building isolated installer $Version..."
    $iconPath = if (Test-Path -LiteralPath (Join-Path $payload 'Assets/Brand/FaceTrackingBridge-headset-v2.ico')) {
        'Assets\Brand\FaceTrackingBridge-headset-v2.ico'
    } else { 'Assets\Brand\FrameBridge-headset-v1.ico' }
    $arguments = @(
        '--no-compression',
        "--define=AppPayload=$payload",
        "--define=AppVersion=$Version",
        "--define=BuildIdentity=$testId",
        "--define=ProductName=$testProductName",
        "--define=DisplayName=$DisplayName",
        "--define=HeadsetIconPath=$iconPath",
        "--define=IconDesktopDir=$desktopIconDirectory",
        "--define=IconGroupDir=$groupIconDirectory",
        "--define=LegacyGroupParent=$legacyGroupParent",
        '--define=MigrateLegacyShortcuts=1',
        '--define=ManageSteamVrRegistration=1',
        "--define=SteamVrCommandExecutable=$python",
        "--define=SteamVrTestHelperScript=$steamVrHelper",
        "--define=SteamVrDataRoot=$steamVrProfile",
        "--define=AppMutexName=$testMutex",
        "--output-dir=$compiledPath",
        "--output-filename=FrameBridgeTest-$Version",
        (Join-Path $sourceRoot 'installer/FrameBridge.iss')
    )
    $exitCode = Invoke-TestProcess $compilerPath $arguments "compile-$Version.log"
    Assert-True ($exitCode -eq 0) "Test installer compilation failed ($Version). See compile-$Version.log."
}
function Invoke-TestSetup([string]$SetupPath, [string]$LogLabel, [bool]$FirstInstall = $false, [bool]$DesktopIcon = $false) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $runRoot "$LogLabel-inno.log")")
    if ($FirstInstall) { $arguments += "/DIR=$installPath" }
    $arguments += if ($DesktopIcon) { '/TASKS=desktopicon' } else { '/TASKS=' }
    return Invoke-TestProcess $SetupPath $arguments "$LogLabel-process.log" 45
}
function Read-TestRegistration([string]$ExpectedName = $script:expectedDisplayName) {
    Assert-True (Test-Path -LiteralPath $registryKey) 'The isolated test uninstall entry was not created.'
    $registration = Get-ItemProperty -LiteralPath $registryKey
    Assert-True ($registration.DisplayName -eq $ExpectedName) "Installed Apps did not use the expected name: $ExpectedName."
    Assert-True ([IO.Path]::GetFullPath($registration.InstallLocation).TrimEnd('\') -eq $installPath.TrimEnd('\')) 'Installer registration points outside the expected test install directory.'
    $matches = @(Get-ChildItem -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | Where-Object PSChildName -EQ "${testId}_is1")
    Assert-True ($matches.Count -eq 1) 'Expected exactly one uninstall entry for the isolated test AppId.'
    return $registration
}
function Set-TestRegistrationFixture([string]$Version, [string]$Name, [Nullable[int]]$Generation) {
    # Only this run's nonce uninstall key is used. No production registration is changed.
    Set-ItemProperty -LiteralPath $registryKey -Name DisplayVersion -Value $Version
    Set-ItemProperty -LiteralPath $registryKey -Name DisplayName -Value $Name
    if ($null -eq $Generation) {
        Remove-ItemProperty -LiteralPath $registryKey -Name BabbleBridgeReleaseGeneration -ErrorAction SilentlyContinue
    } else {
        $null = New-ItemProperty -LiteralPath $registryKey -Name BabbleBridgeReleaseGeneration -Value $Generation -PropertyType DWord -Force
    }
}
function Restore-CurrentRegistration {
    if (Test-Path -LiteralPath $registryKey) {
        Set-TestRegistrationFixture $upgradeVersion 'Face Tracking Bridge' 3
    }
}
function Assert-AcceptedMigration([string]$Setup, [string]$Version, [string]$Name, [Nullable[int]]$Generation, [string]$Label, [string]$ExpectedVersion = $script:migrationTargetVersion) {
    try {
        Set-TestRegistrationFixture $Version $Name $Generation
        $exitCode = Invoke-TestSetup $Setup $Label
        Assert-True ($exitCode -eq 0) "Known migration was rejected: $Label."
        $registration = Read-TestRegistration
        Assert-True ($registration.DisplayVersion -eq $ExpectedVersion) "Migration registered the wrong target version: $Label."
        Assert-True ((Get-ItemPropertyValue -LiteralPath $registryKey -Name BabbleBridgeReleaseGeneration) -eq 3) "Migration did not record generation 3: $Label."
        Assert-InstalledPayload $payloadSource
        Assert-ForeignShortcutUnchanged
        Assert-OwnedStartShortcuts
    }
    finally { Restore-CurrentRegistration }
}
function Assert-RejectedMigration([string]$Setup, [string]$Version, [string]$Name, [Nullable[int]]$Generation, [string]$Label) {
    try {
        Set-TestRegistrationFixture $Version $Name $Generation
        $exitCode = Invoke-TestSetup $Setup $Label
        Assert-True ($exitCode -ne 0) "Unknown or future numbering migration was accepted: $Label."
        $registration = Read-TestRegistration $Name
        Assert-True ($registration.DisplayVersion -eq $Version) "Rejected migration changed the registered version: $Label."
        if ($null -eq $Generation) {
            Assert-True ($registration.PSObject.Properties.Name -notcontains 'BabbleBridgeReleaseGeneration') "Rejected migration created a generation marker: $Label."
        } else {
            Assert-True ($registration.BabbleBridgeReleaseGeneration -eq $Generation) "Rejected migration changed the generation marker: $Label."
        }
        Assert-InstalledPayload $payloadSource
        Assert-ForeignShortcutUnchanged
        Assert-OwnedStartShortcuts
    }
    finally { Restore-CurrentRegistration }
}
function Invoke-TestUninstaller([string]$LogLabel) {
    Assert-OwnedPath $installPath
    $null = Read-TestRegistration
    $uninstallers = @(Get-ChildItem -LiteralPath $installPath -File -Filter 'unins*.exe')
    Assert-True ($uninstallers.Count -eq 1) 'Expected one uninstaller inside the isolated install directory.'
    Assert-OwnedPath $uninstallers[0].FullName
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $runRoot "$LogLabel-inno.log")")
    return Invoke-TestProcess $uninstallers[0].FullName $arguments "$LogLabel-process.log" 45
}
function Uninstall-TestProduct {
    $exitCode = Invoke-TestUninstaller 'uninstall'
    Assert-True ($exitCode -eq 0) "Isolated uninstaller exited with $exitCode."
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Test-Path -LiteralPath $registryKey) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Assert-True (!(Test-Path -LiteralPath $registryKey)) 'Test uninstall registration remains after uninstall.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $installPath 'BabbleFrameBridge.exe'))) 'Installed app remains after uninstall.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $installPath 'runtime/BabbleFrameBridge.Worker.exe'))) 'Installed worker remains after uninstall.'
    Assert-True (!(Test-Path -LiteralPath $startShortcut)) 'Start shortcut remains after uninstall.'
    Assert-True (!(Test-Path -LiteralPath $guideShortcut)) 'Setup-guide shortcut remains after uninstall.'
}

$before = Get-UserDataSnapshot
[IO.File]::WriteAllText((Join-Path $runRoot 'user-data-before.json'), $before)
try {
    Assert-True (!(Test-Path -LiteralPath $registryKey)) 'The generated test AppId unexpectedly exists.'
    # This test helper never imports OpenVR or contacts SteamVR. Both the fake
    # profile and executable are explicit compiler overrides for the nonce AppId.
    # Even cleanup after a failed test cannot unregister a real user's app.
    Assert-OwnedPath $steamVrHelper
    Assert-OwnedPath $steamVrProfile
    $null = New-Item -ItemType Directory -Path $steamVrProfile
    [IO.File]::WriteAllText($steamVrSettings, (@{
        StartWithSteamVr = $false; StartMinimized = $true; CloseWithSteamVr = $true; KittyEnabled = $true
        ImportedModule = 'preserve-user-module'
    } | ConvertTo-Json))
    $fakeMaintenanceSource = @'
import argparse
import hashlib
import json
from pathlib import Path
import sys

parser = argparse.ArgumentParser()
parser.add_argument("--installed-executable", required=True)
parser.add_argument("--data", required=True)
commands = parser.add_mutually_exclusive_group(required=True)
commands.add_argument("--refresh-steamvr-registration", action="store_true")
commands.add_argument("--unregister-steamvr", action="store_true")
args = parser.parse_args()
root = Path(args.data).resolve(strict=True)
executable = Path(args.installed_executable).resolve()
# This fixture refuses to act outside its isolated sibling install directory.
if executable != root.parent / "install" / "BabbleFrameBridge.exe":
    raise SystemExit("Unexpected installed executable")
settings_path = root / "settings.json"
manifest = root / "steamvr" / "FaceTrackingBridge.vrmanifest"
command = "--refresh-steamvr-registration" if args.refresh_steamvr_registration else "--unregister-steamvr"
payload = executable.with_suffix(".dll")
event = {"command": command, "installed_executable": str(executable),
         "installed_executable_exists": executable.is_file(),
         "payload_sha256": hashlib.sha256(payload.read_bytes()).hexdigest() if payload.is_file() else None}
with (root / "maintenance-events.jsonl").open("a", encoding="utf-8") as log:
    log.write(json.dumps(event) + "\n")
settings = json.loads(settings_path.read_text(encoding="utf-8-sig"))
if args.unregister_steamvr:
    settings["StartWithSteamVr"] = False
    settings_path.write_text(json.dumps(settings), encoding="utf-8")
    manifest.unlink(missing_ok=True)
elif settings["StartWithSteamVr"]:
    manifest.parent.mkdir(exist_ok=True)
    manifest.write_text(json.dumps({"app_key": "isolated.fake.facetrackingbridge", "binary_path_windows": str(executable)}), encoding="utf-8")
# Simulate native registration failure after local cleanup. Setup must log the
# failure and continue; no SteamVR process or production app key is ever used.
failure = root / "fail-command.txt"
if failure.is_file() and failure.read_text(encoding="utf-8") == command:
    raise SystemExit(23)
'@
    [IO.File]::WriteAllText($steamVrHelper, $fakeMaintenanceSource)
    Assert-OwnedPath $payload
    Assert-OwnedPath $installPath
    $firstPayloadSource = if ($previousPayload) { $previousPayload } else { $payloadSource }
    foreach ($item in Get-ChildItem -LiteralPath $firstPayloadSource -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $payload -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $desktopIconDirectory, $groupIconDirectory -Force
    [IO.File]::WriteAllText($foreignTarget, 'unrelated shortcut target fixture; never executed')
    New-TestShortcut $desktopShortcut $foreignTarget
    $foreignShortcutHash = (Get-FileHash -LiteralPath $desktopShortcut -Algorithm SHA256).Hash
    # Every legacy fixture is inside this run's verified workspace directories.
    # A collision at the new desktop name must preserve the only owned old link.
    foreach ($legacyName in $legacyNames) {
        $legacyGroup = Join-Path $legacyGroupParent $legacyName
        $null = New-Item -ItemType Directory -Path $legacyGroup -Force
        New-TestShortcut (Join-Path $desktopIconDirectory "$legacyName.lnk") (Join-Path $installPath 'BabbleFrameBridge.exe')
        New-TestShortcut (Join-Path $legacyGroup "$legacyName.lnk") (Join-Path $installPath 'BabbleFrameBridge.exe')
        New-TestShortcut (Join-Path $legacyGroup 'Setup guide.lnk') (Join-Path $installPath 'START-HERE.txt')
        [IO.File]::WriteAllText((Join-Path $legacyGroup 'keep.txt'), 'unrelated existing group content')
    }
    $foreignLegacyShortcut = Join-Path $desktopIconDirectory 'Steam Frame Babble Bridge.lnk'
    New-TestShortcut $foreignLegacyShortcut $foreignTarget
    $foreignLegacyHash = (Get-FileHash -LiteralPath $foreignLegacyShortcut -Algorithm SHA256).Hash
    $marker = Join-Path $payload 'installer-smoke-marker.txt'
    [IO.File]::WriteAllText($marker, 'first-release')
    Compile-TestInstaller $initialVersion $initialDisplayName
    $firstSetup = Join-Path $compiledPath "FrameBridgeTest-$initialVersion.exe"
    $exitCode = Invoke-TestSetup $firstSetup 'install' $true
    Assert-True ($exitCode -eq 0) "First installation failed with exit code $exitCode."
    $registration = Read-TestRegistration
    Assert-True ($registration.DisplayVersion -eq $initialVersion) 'First install version is incorrect.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $installPath 'installer-smoke-marker.txt') -Raw) -eq 'first-release') 'Initial payload marker was not installed.'
    Assert-InstalledPayload $payload
    Assert-SteamVrRefresh $payload
    Assert-SteamVrPreferences $false
    Assert-True (!(Test-Path -LiteralPath $steamVrManifest)) 'Fresh installation opted into SteamVR launch without a user choice.'
    Add-Check 'Fresh installation runs hidden SteamVR maintenance after files exist and leaves automatic launch disabled.'
    $initialAppHash = (Get-FileHash -LiteralPath (Join-Path $installPath 'BabbleFrameBridge.dll') -Algorithm SHA256).Hash
    Assert-OwnedStartShortcuts
    Assert-ForeignShortcutUnchanged
    foreach ($legacyName in $legacyNames) {
        $legacyGroup = Join-Path $legacyGroupParent $legacyName
        Assert-True (!(Test-Path -LiteralPath (Join-Path $legacyGroup "$legacyName.lnk"))) 'An owned old Start menu shortcut survived replacement.'
        Assert-True (!(Test-Path -LiteralPath (Join-Path $legacyGroup 'Setup guide.lnk'))) 'An owned old setup-guide shortcut survived replacement.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $legacyGroup 'keep.txt') -Raw) -eq 'unrelated existing group content') 'Migrating shortcuts removed unrelated group content.'
        Assert-True (Test-Path -LiteralPath (Join-Path $desktopIconDirectory "$legacyName.lnk")) 'A legacy desktop link was removed before its replacement existed.'
    }
    Add-Check 'Owned former Start menu shortcuts migrated only after replacement; desktop collisions and unrelated group content were preserved.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $installPath 'Assets/Baballonia-module.zip'))) 'The installer included a module archive.'
    foreach ($moduleDll in @('VRCFaceTracking.Baballonia.dll', 'VirtualDesktop.FaceTracking.dll', 'SteamLinkVRCFTModule.dll')) {
        Assert-True (@(Get-ChildItem -LiteralPath $installPath -Recurse -File -Filter $moduleDll).Count -eq 0) "The installer included an external module DLL: $moduleDll"
    }
    Add-Check 'First install created one test uninstall entry and extracted the complete verified app payload.'

    Set-SteamVrOptIn $true
    $exitCode = Invoke-TestSetup $firstSetup 'reinstall' $false $true
    Assert-True ($exitCode -eq 0) "Same-version reinstall failed with $exitCode."
    $registration = Read-TestRegistration
    Assert-True ($registration.DisplayVersion -eq $initialVersion) 'Reinstall changed the registered version unexpectedly.'
    Assert-ForeignShortcutUnchanged
    Assert-OwnedStartShortcuts
    Assert-SteamVrRefresh $payload
    Assert-SteamVrPreferences $true
    $fakeRegistration = Get-Content -LiteralPath $steamVrManifest -Raw | ConvertFrom-Json
    Assert-True ($fakeRegistration.binary_path_windows -eq (Join-Path $installPath 'BabbleFrameBridge.exe')) 'Reinstall registered the wrong SteamVR executable path.'
    Add-Check 'An existing SteamVR automatic-launch choice is refreshed against the installed executable without changing other preferences.'
    Add-Check 'Same-version reinstall reused the installation directory and uninstall entry.'
    Add-Check 'An unrelated same-name desktop shortcut survived unchecked installation and explicitly selected reinstallation.'

    # Recreate the preceding product's owned Start menu links after installing
    # the old payload, so the real upgrade exercises the spacing-name migration.
    $previousNamedGroup = Join-Path $legacyGroupParent 'FaceTracking Bridge'
    New-TestShortcut (Join-Path $previousNamedGroup 'FaceTracking Bridge.lnk') (Join-Path $installPath 'BabbleFrameBridge.exe')
    New-TestShortcut (Join-Path $previousNamedGroup 'Setup guide.lnk') (Join-Path $installPath 'START-HERE.txt')

    if ($generationReset) {
        # Reproduce the exact public generation preceding the 1.0.0 reset.
        Set-TestRegistrationFixture $initialVersion $initialDisplayName 2
    }
    if ($previousPayload) {
        Assert-OwnedPath $payload
        Remove-Item -LiteralPath $payload -Recurse -Force
        $null = New-Item -ItemType Directory -Path $payload
        foreach ($item in Get-ChildItem -LiteralPath $payloadSource -Force) {
            Copy-Item -LiteralPath $item.FullName -Destination $payload -Recurse -Force
        }
    }
    [IO.File]::WriteAllText($marker, 'upgraded-release')
    $legacyAssets = Join-Path $installPath 'Assets'
    $null = New-Item -ItemType Directory -Path $legacyAssets -Force
    [IO.File]::WriteAllText((Join-Path $legacyAssets 'Baballonia-module.zip'), 'obsolete-bundled-fixture')
    Compile-TestInstaller $upgradeVersion
    $upgradeSetup = Join-Path $compiledPath "FrameBridgeTest-$upgradeVersion.exe"
    $heldMutex = [Threading.Mutex]::new($false, $testMutex)
    try {
        $steamVrCallsBeforeBlockedSetup = @(Read-SteamVrEvents).Count
        $exitCode = Invoke-TestSetup $upgradeSetup 'upgrade-blocked-by-running-app'
        Assert-True ($exitCode -ne 0) 'Setup ignored the running application mutex.'
        $registration = Read-TestRegistration
        Assert-True ($registration.DisplayVersion -eq $initialVersion) 'A blocked update changed the installed version.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $installPath 'installer-smoke-marker.txt') -Raw) -eq 'first-release') 'A blocked update changed installed files.'
        Assert-True (@(Read-SteamVrEvents).Count -eq $steamVrCallsBeforeBlockedSetup) 'A blocked update changed SteamVR registration.'
    }
    finally { $heldMutex.Dispose() }
    Add-Check 'Setup rejected an update while the isolated application mutex existed, before changing version or files.'
    $exitCode = Invoke-TestSetup $upgradeSetup 'upgrade'
    Assert-True ($exitCode -eq 0) "Application payload replacement failed with $exitCode."
    $expectedDisplayName = 'Face Tracking Bridge'
    $registration = Read-TestRegistration
    Assert-True ($registration.DisplayVersion -eq $upgradeVersion) 'Upgrade did not update DisplayVersion.'
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryKey -Name BabbleBridgeReleaseGeneration) -eq 3) 'Upgrade did not register public release generation 3.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $installPath 'installer-smoke-marker.txt') -Raw) -eq 'upgraded-release') 'Upgrade did not replace the payload marker.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $legacyAssets 'Baballonia-module.zip'))) 'Upgrade retained the legacy baked-in archive.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $installPath 'Assets/Brand/FrameBridge-headset-v1.ico'))) 'Upgrade retained the retired green shortcut icon.'
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $installPath 'Assets/Brand/FaceTrackingBridge-headset-v2.ico') -Algorithm SHA256).Hash -eq
        (Get-FileHash -LiteralPath (Join-Path $payloadSource 'Assets/Brand/FaceTrackingBridge-headset-v2.ico') -Algorithm SHA256).Hash) 'Upgrade did not install the new cyan shortcut icon.'
    Assert-InstalledPayload $payloadSource
    Assert-SteamVrRefresh $payloadSource
    Assert-SteamVrPreferences $true
    Assert-True ((Get-Content -LiteralPath $steamVrManifest -Raw | ConvertFrom-Json).app_key -eq 'isolated.fake.facetrackingbridge') 'Upgrade changed the stable fake SteamVR application key.'
    Add-Check 'Upgrade refreshes the same owned SteamVR registration after replacing app files and preserves the automatic-launch choice.'
    if ($previousPayload) {
        Assert-True ((Get-FileHash -LiteralPath (Join-Path $installPath 'BabbleFrameBridge.dll') -Algorithm SHA256).Hash -ne $initialAppHash) 'The real app upgrade left the old application binary in place.'
        if ($sameVersionReplacement) {
            Add-Check "An earlier $initialVersion build was replaced by the corrected $upgradeVersion build; installed app and worker hashes match the new payload."
        }
        else { Add-Check "Real app binaries upgraded from $initialVersion to $upgradeVersion; installed app and worker hashes match the new payload." }
    }
    else { Add-Check 'A newer synthetic release replaced installed files and updated the same uninstall entry.' }
    Assert-ForeignShortcutUnchanged
    Assert-OwnedStartShortcuts
    Add-Check 'Upgrade registers the Face Tracking Bridge name with the same application identity and installation directory.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $previousNamedGroup 'FaceTracking Bridge.lnk'))) 'Upgrade left the owned former FaceTracking Bridge Start shortcut.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $previousNamedGroup 'Setup guide.lnk'))) 'Upgrade left the owned former FaceTracking Bridge guide shortcut.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $previousNamedGroup 'keep.txt') -Raw) -eq 'unrelated existing group content') 'Spacing-name migration removed unrelated group content.'
    Add-Check 'The real upgrade migrates owned FaceTracking Bridge Start menu shortcuts after their new replacements exist.'
    if ($generationReset) {
        Add-Check "Known generation-2 $initialDisplayName $initialVersion migrated to $upgradeVersion and recorded generation 3."
    }

    # Exercise exact known reset sources and reject unknown or future builds.
    # All simulated registrations are confined to the nonce test identity.
    $migrationSetup = $upgradeSetup
    if ($migrationTargetVersion -ne $upgradeVersion) {
        Compile-TestInstaller $migrationTargetVersion
        $migrationSetup = Join-Path $compiledPath "FrameBridgeTest-$migrationTargetVersion.exe"
    }
    foreach ($version in @('1.0.1', '1.0.2')) {
        Assert-AcceptedMigration $migrationSetup $version 'Babble Bridge' 2 "reset-babble-$version"
    }
    Assert-AcceptedMigration $migrationSetup '1.0.3' 'FaceTracking Bridge' 2 'reset-facetracking-1.0.3'
    Assert-AcceptedMigration $migrationSetup '1.0.0' 'Babble Bridge' 2 'same-version-babble-1.0.0'
    Add-Check 'Exact generation-2 Babble Bridge 1.0.1/1.0.2 and FaceTracking Bridge 1.0.3 migrate to 1.0.0; existing 1.0.0 reinstalls normally.'

    foreach ($version in @('1.0.1', '1.0.2', '1.0.3', '1.0.4', '1.0.5')) {
        Assert-AcceptedMigration $migrationSetup $version 'Babble Bridge' $null "private-preview-$version"
    }
    foreach ($legacyName in @('Frame Bridge', 'Steam Frame Babble Bridge')) {
        Assert-AcceptedMigration $migrationSetup '1.5.1' $legacyName $null ("development-" + $legacyName.Replace(' ', '-'))
    }
    Add-Check 'Unmarked private previews and exact older development builds migrate and record generation 3.'

    $rejectedCases = @(
        @('1.0.1', 'Babble Bridge', 3),
        @('1.0.2', 'Babble Bridge', 3),
        @('1.0.3', 'FaceTracking Bridge', 3),
        @('1.0.1', 'FaceTracking Bridge', 3),
        @('1.0.5', 'Babble Bridge', 3),
        @('1.5.1', 'Frame Bridge', 3),
        @('1.5.1', 'Steam Frame Babble Bridge', 3),
        @('1.2.0', 'Babble Bridge', 3),
        @('1.0.3', 'Babble Bridge', 2),
        @('1.0.2', 'FaceTracking Bridge', 2),
        @('1.0.4', 'FaceTracking Bridge', 2),
        @('1.0.3', 'Unrelated Bridge', 2),
        @('1.5.1', 'Frame Bridge', 2),
        @('1.0.3', 'FaceTracking Bridge', 4),
        @('1.0.3', 'FaceTracking Bridge', $null),
        @('1.0.3', 'Face Tracking Bridge', 3),
        @('1.0.3', 'Face Tracking Bridge', 2)
    )
    $caseIndex = 0
    foreach ($case in $rejectedCases) {
        $caseIndex++
        Assert-RejectedMigration $migrationSetup $case[0] $case[1] $case[2] "reject-numbering-$caseIndex"
    }
    Add-Check 'Generation-3/future builds and unknown name/version/generation combinations are rejected without changing files or registration.'

    # A user can skip the first reset installer. The exact old generation-2
    # source is still accepted by a later 1.0.x setup, while generation 3 cannot
    # reuse that exception even with the same old name and numeric version.
    $skippedResetVersion = '1.0.1'
    Compile-TestInstaller $skippedResetVersion
    $skippedResetSetup = Join-Path $compiledPath "FrameBridgeTest-$skippedResetVersion.exe"
    Assert-AcceptedMigration $skippedResetSetup '1.0.3' 'FaceTracking Bridge' 2 'skip-reset-generation-two' $skippedResetVersion
    Assert-AcceptedMigration $skippedResetSetup '1.0.5' 'Babble Bridge' $null 'skip-reset-private-preview' $skippedResetVersion
    Assert-RejectedMigration $skippedResetSetup '1.0.3' 'FaceTracking Bridge' 3 'skip-reset-generation-three-rejected'
    Assert-RejectedMigration $skippedResetSetup '1.0.2' 'Babble Bridge' 3 'skip-reset-generation-three-babble-rejected'
    Add-Check 'A later 1.0.1 installer accepts skipped-reset generation-2/preview sources and rejects the same numeric transitions after generation 3.'

    if (!$generationReset) {
        $downgradeSetup = $firstSetup
        if ($sameVersionReplacement) {
            # Same-version replacement is intentionally permitted by Setup.
            # Build a genuinely older numeric setup to retain downgrade coverage.
            Assert-True ([Version]$upgradeVersion -gt [Version]'0.0.0') 'A numeric downgrade fixture needs a release newer than 0.0.0.'
            Compile-TestInstaller '0.0.0'
            $downgradeSetup = Join-Path $compiledPath 'FrameBridgeTest-0.0.0.exe'
        }
        $exitCode = Invoke-TestSetup $downgradeSetup 'downgrade'
        Assert-True ($exitCode -ne 0) 'The old installer unexpectedly allowed a downgrade.'
        $registration = Read-TestRegistration
        Assert-True ($registration.DisplayVersion -eq $upgradeVersion) 'Downgrade rejection changed the installed version.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $installPath 'installer-smoke-marker.txt') -Raw) -eq 'upgraded-release') 'Downgrade rejection changed installed files.'
        Add-Check 'An older installer was rejected before changing installed version or files.'
    }

    # Use the existing diagnostic mapping mode so a real local VRChat OSCQuery
    # service cannot replace this test's parameter map. The real module, wire
    # protocol, VRCFT mapper and worker-restart assertions are still exercised.
    $integrationWrapper = Join-Path $runRoot 'installed_worker_diagnostic.py'
    $wrapperSource = @'
import importlib.util
import json
from pathlib import Path
import sys

test_path = Path(sys.argv[1]).resolve(strict=True)
worker_path = Path(sys.argv[2]).resolve(strict=True)
module_zip = Path(sys.argv[3]).resolve(strict=True)
spec = importlib.util.spec_from_file_location("bridge_installer_integration", test_path)
test = importlib.util.module_from_spec(spec)
spec.loader.exec_module(test)
original_popen = test.subprocess.Popen

def diagnostic_popen(command, *args, **kwargs):
    if command and Path(command[0]).resolve() == worker_path:
        command = [*command, "--all-parameters"]
    return original_popen(command, *args, **kwargs)

test.subprocess.Popen = diagnostic_popen
result = test.run(worker_path, module_zip)
result["mode"] = "existing --all-parameters diagnostic mapping"
result["avatar_discovery"] = "not tested; real local VRChat discovery is excluded"
result["checks"] = [check.replace("VRCFT avatar mapping", "VRCFT diagnostic parameter mapping") for check in result["checks"]]
print(json.dumps(result, indent=2))
'@
    [IO.File]::WriteAllText($integrationWrapper, $wrapperSource)
    $integrationArguments = @($integrationWrapper, (Join-Path $sourceRoot 'tests/integration_test.py'), (Join-Path $installPath 'runtime/BabbleFrameBridge.Worker.exe'), $externalModuleZip)
    $exitCode = Invoke-TestProcess $python $integrationArguments 'installed-worker-integration.json'
    Assert-True ($exitCode -eq 0) 'Tracking integration failed against the installed worker.'
    Add-Check 'The installed worker passed real-module BFR discovery, exact diagnostic OSC mapping, and mode-change restart checks.'

    Uninstall-TestProduct
    Assert-SteamVrUnregistered
    Assert-ForeignShortcutUnchanged
    Add-Check 'The isolated uninstaller removed installed app files and its test registration.'
    Add-Check 'Uninstall removed this installation''s Start menu and guide shortcuts and preserved the unrelated desktop shortcut.'
    Add-Check 'Uninstall invokes SteamVR cleanup before deleting the app, clears its automatic-launch choice, and preserves other settings and module fixtures.'

    # The collision fixture is ours and was verified unchanged above. A fresh
    # lifecycle exercises the normal desktop shortcut creation and removal too.
    Assert-OwnedPath $desktopShortcut
    Remove-Item -LiteralPath $desktopShortcut -Force
    [IO.File]::WriteAllText($steamVrFailure, '--refresh-steamvr-registration')
    $exitCode = Invoke-TestSetup $upgradeSetup 'desktop-shortcut-install' $true $false
    Assert-True ($exitCode -eq 0) 'Installation preserving an existing owned desktop shortcut failed.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $runRoot 'desktop-shortcut-install-inno.log') -Raw) -match 'Warning: SteamVR registration maintenance returned 23') 'Installer did not log the simulated SteamVR registration failure.'
    Assert-SteamVrPreferences $false
    Assert-True (!(Test-Path -LiteralPath $steamVrManifest)) 'Reinstall after uninstall silently restored the SteamVR launch opt-in.'
    Remove-Item -LiteralPath $steamVrFailure -Force
    Add-Check 'SteamVR registration failure is logged without blocking installation; reinstall after uninstall requires a new automatic-launch choice.'
    Assert-OwnedStartShortcuts
    Assert-True ((Get-TestShortcutTarget $desktopShortcut) -eq (Join-Path $installPath 'BabbleFrameBridge.exe')) 'Requested desktop shortcut target is incorrect.'
    foreach ($legacyName in @('Frame Bridge', 'Babble Bridge', 'FaceTracking Bridge')) {
        Assert-True (!(Test-Path -LiteralPath (Join-Path $desktopIconDirectory "$legacyName.lnk"))) 'An owned former desktop shortcut survived its replacement.'
    }
    Assert-True ((Get-FileHash -LiteralPath $foreignLegacyShortcut -Algorithm SHA256).Hash -eq $foreignLegacyHash) 'A different application''s legacy-name shortcut was altered.'
    Add-Check 'Existing owned desktop shortcuts migrate without requiring the optional task; unrelated legacy-name shortcuts remain unchanged.'
    $foreignFormerName = Join-Path $desktopIconDirectory 'FaceTracking Bridge.lnk'
    New-TestShortcut $foreignFormerName $foreignTarget
    $foreignFormerHash = (Get-FileHash -LiteralPath $foreignFormerName -Algorithm SHA256).Hash
    $exitCode = Invoke-TestSetup $upgradeSetup 'foreign-former-spelling-shortcut'
    Assert-True ($exitCode -eq 0) 'Reinstall preserving the unrelated former-spelling shortcut failed.'
    Assert-True ((Get-FileHash -LiteralPath $foreignFormerName -Algorithm SHA256).Hash -eq $foreignFormerHash) 'The spacing-name migration altered an unrelated FaceTracking Bridge shortcut.'
    Add-Check 'An unrelated FaceTracking Bridge desktop shortcut survives the spacing-name migration.'
    $heldMutex = [Threading.Mutex]::new($false, $testMutex)
    try {
        $steamVrCallsBeforeBlockedUninstall = @(Read-SteamVrEvents).Count
        $exitCode = Invoke-TestUninstaller 'uninstall-blocked-by-running-app'
        Assert-True ($exitCode -ne 0) 'Uninstall ignored the running application mutex.'
        $registration = Read-TestRegistration
        Assert-True ($registration.DisplayVersion -eq $upgradeVersion) 'A blocked uninstall changed the installed registration.'
        Assert-InstalledPayload $payloadSource
        Assert-OwnedStartShortcuts
        Assert-True ((Get-TestShortcutTarget $desktopShortcut) -eq (Join-Path $installPath 'BabbleFrameBridge.exe')) 'A blocked uninstall removed the desktop shortcut.'
        Assert-True (@(Read-SteamVrEvents).Count -eq $steamVrCallsBeforeBlockedUninstall) 'A blocked uninstall changed SteamVR registration.'
    }
    finally { $heldMutex.Dispose() }
    Add-Check 'Uninstall rejected removal while the isolated application mutex existed, preserving files, registration and shortcuts.'
    Set-SteamVrOptIn $true
    [IO.File]::WriteAllText($steamVrFailure, '--unregister-steamvr')
    Uninstall-TestProduct
    Assert-SteamVrUnregistered
    Assert-True ((Get-Content -LiteralPath (Join-Path $runRoot 'uninstall-inno.log') -Raw) -match 'Warning: SteamVR registration maintenance returned 23') 'Uninstaller did not log the simulated SteamVR cleanup failure.'
    Add-Check 'A simulated SteamVR cleanup failure is logged without blocking uninstall or discarding unrelated launch preferences.'
    Assert-True (!(Test-Path -LiteralPath $desktopShortcut)) 'This installation''s desktop shortcut remains after uninstall.'
    Add-Check 'The migrated desktop shortcut was created and was removed by normal uninstall.'
}
catch { $failure = $_.Exception.Message }
finally {
    # Cleanup is restricted to this run's randomly identified product and verified directory.
    if (Test-Path -LiteralPath $registryKey) {
        try { Uninstall-TestProduct }
        catch { if (!$failure) { $failure = $_.Exception.Message } else { $failure += " Cleanup: $($_.Exception.Message)" } }
    }
    $after = Get-UserDataSnapshot
    [IO.File]::WriteAllText((Join-Path $runRoot 'user-data-after.json'), $after)
    if ($before -eq $after) { Add-Check 'Real user settings, imported modules, and module state remained byte-for-byte unchanged.' }
    elseif (!$failure) { $failure = 'The actual app data snapshot changed during the isolated installer test.' }
    $result = if ($failure) { 'FAIL' } else { 'PASS' }
    $report = [ordered]@{
        Result = $result; TestAppId = $testId; TestInstallDirectory = $installPath
        ProductionAppIdUsed = $false; Versions = @($initialVersion, $upgradeVersion)
        RealPayloadUpgrade = [bool]$previousPayload
        SameVersionReplacement = [bool]$sameVersionReplacement
        GenerationTwoReset = [bool]$generationReset
        PublicReleaseGeneration = 3
        DevelopmentMigrationTarget = $migrationTargetVersion
        ShortcutDirectories = @($desktopIconDirectory, $groupIconDirectory)
        AvatarDiscovery = 'Not tested; existing --all-parameters diagnostic mode avoids any running VRChat client.'
        SteamVrMaintenance = 'Isolated fake helper only; real SteamVR registration and native APIs are never called.'
        Checks = $checks.ToArray(); Failure = $failure
    }
    $json = ConvertTo-Json $report -Depth 6
    [IO.File]::WriteAllText((Join-Path $runRoot 'report.json'), $json)
    $upgradeDescription = if ($sameVersionReplacement) {
        "Real application payloads tested: earlier $initialVersion build to corrected $upgradeVersion build (explicit same-version replacement)."
    } elseif ($previousPayload) { "Real application payloads tested: $initialVersion to $upgradeVersion." } else { 'The synthetic newer installer tested upgrade behavior without changing production version.' }
    $summary = @('FACE TRACKING BRIDGE INSTALLER VALIDATION', '', "Result: $result", '', 'Isolated per-user install, reinstall, upgrade, downgrade rejection and uninstall.', 'The test used a separate random application identity and workspace install and shortcut folders.', $upgradeDescription, 'Shortcut collision protection and running-application install/uninstall guards were exercised.', 'SteamVR hooks used an isolated fake helper; real SteamVR registration and native APIs were excluded.', 'Installed-worker mapping used the existing --all-parameters diagnostic mode.', 'VRChat avatar discovery was excluded to avoid interference with a running local game.', '')
    $summary += $checks | ForEach-Object { '- ' + $_ }
    if ($failure) { $summary += @('', 'Failure: ' + $failure) }
    $summaryText = $summary -join [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $runRoot 'report.txt'), $summaryText)
    if (!$failure) { [IO.File]::WriteAllText((Join-Path $sourceRoot 'installer/TESTED-INSTALLER.txt'), $summaryText) }
    Write-Output $json
}
if ($failure) { throw $failure }
exit 0
