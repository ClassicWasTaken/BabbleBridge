#requires -Version 7.0
# Copyright (c) 2026 Frame Bridge contributors.
# SPDX-License-Identifier: Apache-2.0

param(
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$ModuleZip,
    [string]$PreviousPayloadDirectory
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
$testProductName = "BabbleBridgeInstallerTest-$($runId.Substring(0, 12))"
$desktopIconDirectory = Join-Path $runRoot 'shortcuts/desktop'
$groupIconDirectory = Join-Path $runRoot 'shortcuts/start'
$desktopShortcut = Join-Path $desktopIconDirectory "$testProductName.lnk"
$startShortcut = Join-Path $groupIconDirectory "$testProductName.lnk"
$guideShortcut = Join-Path $groupIconDirectory 'Setup guide.lnk'
$foreignTarget = Join-Path $runRoot 'unrelated-tool.exe'
$foreignShortcutHash = $null
$registryKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${testId}_is1"
$userDataPath = Join-Path $env:LOCALAPPDATA 'BabbleFrameBridge'
$checks = [Collections.Generic.List[string]]::new()
$failure = $null

function Get-PayloadVersion([string]$Directory) {
    $executable = Join-Path $Directory 'BabbleFrameBridge.exe'
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Missing app executable in payload: $Directory" }
    $text = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
    return ([Version]::Parse($text)).ToString(3)
}
$initialVersion = if ($previousPayload) { Get-PayloadVersion $previousPayload } else { '1.2.0' }
$upgradeVersion = if ($previousPayload) { Get-PayloadVersion $payloadSource } else { '1.2.1' }
$migrationTargetVersion = if ($upgradeVersion.StartsWith('1.0.', [StringComparison]::Ordinal)) { $upgradeVersion } else { '1.0.5' }
$publicReset = $upgradeVersion -eq '1.0.0' -and $initialVersion -in @('1.0.1', '1.0.2', '1.0.3', '1.0.4', '1.0.5')
if ($previousPayload -and !$publicReset -and ([Version]$upgradeVersion -le [Version]$initialVersion)) {
    throw 'PreviousPayloadDirectory must contain an older real app version than PayloadDirectory.'
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
function Compile-TestInstaller([string]$Version) {
    Write-Output "Building isolated installer $Version..."
    $arguments = @(
        '--no-compression',
        "--define=AppPayload=$payload",
        "--define=AppVersion=$Version",
        "--define=BuildIdentity=$testId",
        "--define=ProductName=$testProductName",
        "--define=IconDesktopDir=$desktopIconDirectory",
        "--define=IconGroupDir=$groupIconDirectory",
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
function Read-TestRegistration {
    Assert-True (Test-Path -LiteralPath $registryKey) 'The isolated test uninstall entry was not created.'
    $registration = Get-ItemProperty -LiteralPath $registryKey
    Assert-True ($registration.DisplayName -eq 'Babble Bridge') 'Installed Apps did not use the Babble Bridge name.'
    Assert-True ([IO.Path]::GetFullPath($registration.InstallLocation).TrimEnd('\') -eq $installPath.TrimEnd('\')) 'Installer registration points outside the expected test install directory.'
    $matches = @(Get-ChildItem -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | Where-Object PSChildName -EQ "${testId}_is1")
    Assert-True ($matches.Count -eq 1) 'Expected exactly one uninstall entry for the isolated test AppId.'
    return $registration
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
    $marker = Join-Path $payload 'installer-smoke-marker.txt'
    [IO.File]::WriteAllText($marker, 'first-release')
    Compile-TestInstaller $initialVersion
    $firstSetup = Join-Path $compiledPath "FrameBridgeTest-$initialVersion.exe"
    $exitCode = Invoke-TestSetup $firstSetup 'install' $true
    Assert-True ($exitCode -eq 0) "First installation failed with exit code $exitCode."
    $registration = Read-TestRegistration
    Assert-True ($registration.DisplayVersion -eq $initialVersion) 'First install version is incorrect.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $installPath 'installer-smoke-marker.txt') -Raw) -eq 'first-release') 'Initial payload marker was not installed.'
    Assert-InstalledPayload $payload
    $initialAppHash = (Get-FileHash -LiteralPath (Join-Path $installPath 'BabbleFrameBridge.dll') -Algorithm SHA256).Hash
    Assert-OwnedStartShortcuts
    Assert-ForeignShortcutUnchanged
    Assert-True (!(Test-Path -LiteralPath (Join-Path $installPath 'Assets/Baballonia-module.zip'))) 'The installer included a module archive.'
    foreach ($moduleDll in @('VRCFaceTracking.Baballonia.dll', 'VirtualDesktop.FaceTracking.dll', 'SteamLinkVRCFTModule.dll')) {
        Assert-True (@(Get-ChildItem -LiteralPath $installPath -Recurse -File -Filter $moduleDll).Count -eq 0) "The installer included an external module DLL: $moduleDll"
    }
    Add-Check 'First install created one test uninstall entry and extracted the complete verified app payload.'

    $exitCode = Invoke-TestSetup $firstSetup 'reinstall' $false $true
    Assert-True ($exitCode -eq 0) "Same-version reinstall failed with $exitCode."
    $registration = Read-TestRegistration
    Assert-True ($registration.DisplayVersion -eq $initialVersion) 'Reinstall changed the registered version unexpectedly.'
    Assert-ForeignShortcutUnchanged
    Assert-OwnedStartShortcuts
    Add-Check 'Same-version reinstall reused the installation directory and uninstall entry.'
    Add-Check 'An unrelated same-name desktop shortcut survived unchecked installation and explicitly selected reinstallation.'

    if ($publicReset) {
        # Reproduce the real private preview registration, which had no generation marker.
        Remove-ItemProperty -LiteralPath $registryKey -Name BabbleBridgeReleaseGeneration -ErrorAction SilentlyContinue
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
        $exitCode = Invoke-TestSetup $upgradeSetup 'upgrade-blocked-by-running-app'
        Assert-True ($exitCode -ne 0) 'Setup ignored the running application mutex.'
        $registration = Read-TestRegistration
        Assert-True ($registration.DisplayVersion -eq $initialVersion) 'A blocked update changed the installed version.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $installPath 'installer-smoke-marker.txt') -Raw) -eq 'first-release') 'A blocked update changed installed files.'
    }
    finally { $heldMutex.Dispose() }
    Add-Check 'Setup rejected an update while the isolated application mutex existed, before changing version or files.'
    $exitCode = Invoke-TestSetup $upgradeSetup 'upgrade'
    Assert-True ($exitCode -eq 0) "Newer-version upgrade failed with $exitCode."
    $registration = Read-TestRegistration
    Assert-True ($registration.DisplayVersion -eq $upgradeVersion) 'Upgrade did not update DisplayVersion.'
    Assert-True ((Get-ItemPropertyValue -LiteralPath $registryKey -Name BabbleBridgeReleaseGeneration) -eq 2) 'Upgrade did not register public release generation 2.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $installPath 'installer-smoke-marker.txt') -Raw) -eq 'upgraded-release') 'Upgrade did not replace the payload marker.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $legacyAssets 'Baballonia-module.zip'))) 'Upgrade retained the legacy baked-in archive.'
    Assert-InstalledPayload $payloadSource
    if ($previousPayload) {
        Assert-True ((Get-FileHash -LiteralPath (Join-Path $installPath 'BabbleFrameBridge.dll') -Algorithm SHA256).Hash -ne $initialAppHash) 'The real app upgrade left the old application binary in place.'
        Add-Check "Real app binaries upgraded from $initialVersion to $upgradeVersion; installed app and worker hashes match the new payload."
    }
    else { Add-Check 'A newer synthetic release replaced installed files and updated the same uninstall entry.' }
    Assert-ForeignShortcutUnchanged
    Assert-OwnedStartShortcuts
    if ($publicReset) {
        Add-Check "The private preview $initialVersion migrated to public release $upgradeVersion and registered generation 2 for future downgrade protection."
    }

    # Exercise both sides of the historical numbering exception without ever
    # touching the production registration. Every metadata change is confined
    # to this run's nonce AppId and restored even if an assertion fails.
    $migrationSetup = $upgradeSetup
    if ($migrationTargetVersion -ne $upgradeVersion) {
        Compile-TestInstaller $migrationTargetVersion
        $migrationSetup = Join-Path $compiledPath "FrameBridgeTest-$migrationTargetVersion.exe"
    }
    try {
        Set-ItemProperty -LiteralPath $registryKey -Name DisplayVersion -Value '1.2.0'
        Set-ItemProperty -LiteralPath $registryKey -Name DisplayName -Value 'Babble Bridge'
        $exitCode = Invoke-TestSetup $migrationSetup 'future-stable-downgrade'
        Assert-True ($exitCode -ne 0) 'A future stable version was mistaken for a development build and allowed to downgrade.'
        $registration = Read-TestRegistration
        Assert-True ($registration.DisplayVersion -eq '1.2.0') 'A rejected future-stable downgrade changed the registered version.'
        Assert-InstalledPayload $payloadSource
        Assert-ForeignShortcutUnchanged
        Assert-OwnedStartShortcuts
    }
    finally {
        if (Test-Path -LiteralPath $registryKey) {
            Set-ItemProperty -LiteralPath $registryKey -Name DisplayVersion -Value $upgradeVersion
            Set-ItemProperty -LiteralPath $registryKey -Name DisplayName -Value 'Babble Bridge'
        }
    }
    Add-Check 'A future stable version sharing an old development version number was rejected before changing files or registration.'

    $legacyNameIndex = 0
    foreach ($legacyName in @('Frame Bridge', 'Steam Frame Babble Bridge')) {
        $legacyNameIndex++
        try {
            Set-ItemProperty -LiteralPath $registryKey -Name DisplayVersion -Value '1.5.1'
            Set-ItemProperty -LiteralPath $registryKey -Name DisplayName -Value $legacyName
            $exitCode = Invoke-TestSetup $migrationSetup "legacy-numbering-migration-$legacyNameIndex"
            Assert-True ($exitCode -eq 0) "Known development branding did not permit its numbering migration: $legacyName"
            $registration = Read-TestRegistration
            Assert-True ($registration.DisplayVersion -eq $migrationTargetVersion) 'The legacy numbering migration did not register the stable target version.'
            Assert-InstalledPayload $payloadSource
            Assert-ForeignShortcutUnchanged
            Assert-OwnedStartShortcuts
        }
        finally {
            if (Test-Path -LiteralPath $registryKey) {
                Set-ItemProperty -LiteralPath $registryKey -Name DisplayVersion -Value $upgradeVersion
                Set-ItemProperty -LiteralPath $registryKey -Name DisplayName -Value 'Babble Bridge'
            }
        }
    }
    Add-Check 'Both former development product names permitted their known-version migration to the stable series.'

    if ($publicReset) {
        try {
            Set-ItemProperty -LiteralPath $registryKey -Name DisplayVersion -Value '1.0.5'
            $exitCode = Invoke-TestSetup $upgradeSetup 'generation-two-downgrade'
            Assert-True ($exitCode -ne 0) 'Public generation 2 incorrectly received the private preview numbering exception.'
            Assert-True ((Read-TestRegistration).DisplayVersion -eq '1.0.5') 'Rejected generation 2 downgrade changed registration.'
        }
        finally { Set-ItemProperty -LiteralPath $registryKey -Name DisplayVersion -Value $upgradeVersion }
        Assert-InstalledPayload $payloadSource
        Assert-ForeignShortcutUnchanged
        Assert-OwnedStartShortcuts
        Add-Check 'The one-time private preview reset does not allow future generation 2 releases to downgrade.'

        # A preview user can skip 1.0.0 and install the next public release.
        # Once that install writes generation 2, the same numeric transition
        # must no longer be allowed, even for this later public target.
        $skippedFirstPublicVersion = '1.0.1'
        Compile-TestInstaller $skippedFirstPublicVersion
        $skippedFirstPublicSetup = Join-Path $compiledPath "FrameBridgeTest-$skippedFirstPublicVersion.exe"
        try {
            Set-ItemProperty -LiteralPath $registryKey -Name DisplayVersion -Value '1.0.5'
            Remove-ItemProperty -LiteralPath $registryKey -Name BabbleBridgeReleaseGeneration
            $exitCode = Invoke-TestSetup $skippedFirstPublicSetup 'preview-skipped-first-public-version'
            Assert-True ($exitCode -eq 0) 'A private preview user could not migrate after skipping public version 1.0.0.'
            $registration = Read-TestRegistration
            Assert-True ($registration.DisplayVersion -eq $skippedFirstPublicVersion) 'The skipped-version migration registered the wrong public release.'
            Assert-True ((Get-ItemPropertyValue -LiteralPath $registryKey -Name BabbleBridgeReleaseGeneration) -eq 2) 'The skipped-version migration did not write generation 2.'
            Set-ItemProperty -LiteralPath $registryKey -Name DisplayVersion -Value '1.0.5'
            $exitCode = Invoke-TestSetup $skippedFirstPublicSetup 'later-public-generation-two-downgrade'
            Assert-True ($exitCode -ne 0) 'A later public installer incorrectly allowed a generation 2 downgrade.'
            Assert-True ((Read-TestRegistration).DisplayVersion -eq '1.0.5') 'A rejected later-public downgrade changed registration.'
            Assert-InstalledPayload $payloadSource
            Assert-ForeignShortcutUnchanged
            Assert-OwnedStartShortcuts
        }
        finally {
            if (Test-Path -LiteralPath $registryKey) {
                Set-ItemProperty -LiteralPath $registryKey -Name DisplayVersion -Value $upgradeVersion
                Set-ItemProperty -LiteralPath $registryKey -Name DisplayName -Value 'Babble Bridge'
                Set-ItemProperty -LiteralPath $registryKey -Name BabbleBridgeReleaseGeneration -Value 2
            }
        }
        Add-Check 'Preview users can skip public version 1.0.0, while later public installers still reject generation 2 downgrades.'
    }
    else {
        $exitCode = Invoke-TestSetup $firstSetup 'downgrade'
        Assert-True ($exitCode -ne 0) 'The old installer unexpectedly allowed a downgrade.'
        $registration = Read-TestRegistration
        Assert-True ($registration.DisplayVersion -eq $upgradeVersion) 'Downgrade rejection changed the installed version.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $installPath 'installer-smoke-marker.txt') -Raw) -eq 'upgraded-release') 'Downgrade rejection changed installed files.'
        Add-Check 'An older installer was rejected before changing installed version or files.'
    }

    $python = (Get-Command python.exe -CommandType Application | Select-Object -First 1).Source
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
    Assert-ForeignShortcutUnchanged
    Add-Check 'The isolated uninstaller removed installed app files and its test registration.'
    Add-Check 'Uninstall removed this installation''s Start menu and guide shortcuts and preserved the unrelated desktop shortcut.'

    # The collision fixture is ours and was verified unchanged above. A fresh
    # lifecycle exercises the normal desktop shortcut creation and removal too.
    Assert-OwnedPath $desktopShortcut
    Remove-Item -LiteralPath $desktopShortcut -Force
    $exitCode = Invoke-TestSetup $upgradeSetup 'desktop-shortcut-install' $true $true
    Assert-True ($exitCode -eq 0) 'Installation with a requested desktop shortcut failed.'
    Assert-OwnedStartShortcuts
    Assert-True ((Get-TestShortcutTarget $desktopShortcut) -eq (Join-Path $installPath 'BabbleFrameBridge.exe')) 'Requested desktop shortcut target is incorrect.'
    $heldMutex = [Threading.Mutex]::new($false, $testMutex)
    try {
        $exitCode = Invoke-TestUninstaller 'uninstall-blocked-by-running-app'
        Assert-True ($exitCode -ne 0) 'Uninstall ignored the running application mutex.'
        $registration = Read-TestRegistration
        Assert-True ($registration.DisplayVersion -eq $upgradeVersion) 'A blocked uninstall changed the installed registration.'
        Assert-InstalledPayload $payloadSource
        Assert-OwnedStartShortcuts
        Assert-True ((Get-TestShortcutTarget $desktopShortcut) -eq (Join-Path $installPath 'BabbleFrameBridge.exe')) 'A blocked uninstall removed the desktop shortcut.'
    }
    finally { $heldMutex.Dispose() }
    Add-Check 'Uninstall rejected removal while the isolated application mutex existed, preserving files, registration and shortcuts.'
    Uninstall-TestProduct
    Assert-True (!(Test-Path -LiteralPath $desktopShortcut)) 'This installation''s desktop shortcut remains after uninstall.'
    Add-Check 'A requested desktop shortcut was created and was removed by normal uninstall.'
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
        PrivatePreviewReset = [bool]$publicReset
        PublicReleaseGeneration = 2
        DevelopmentMigrationTarget = $migrationTargetVersion
        ShortcutDirectories = @($desktopIconDirectory, $groupIconDirectory)
        AvatarDiscovery = 'Not tested; existing --all-parameters diagnostic mode avoids any running VRChat client.'
        Checks = $checks.ToArray(); Failure = $failure
    }
    $json = ConvertTo-Json $report -Depth 6
    [IO.File]::WriteAllText((Join-Path $runRoot 'report.json'), $json)
    $upgradeDescription = if ($previousPayload) { "Real application payloads tested: $initialVersion to $upgradeVersion." } else { 'The synthetic newer installer tested upgrade behavior without changing production version.' }
    $summary = @('BABBLE BRIDGE INSTALLER VALIDATION', '', "Result: $result", '', 'Isolated per-user install, reinstall, upgrade, downgrade rejection and uninstall.', 'The test used a separate random application identity and workspace install and shortcut folders.', $upgradeDescription, 'Shortcut collision protection and running-application install/uninstall guards were exercised.', 'Installed-worker mapping used the existing --all-parameters diagnostic mode.', 'VRChat avatar discovery was excluded to avoid interference with a running local game.', '')
    $summary += $checks | ForEach-Object { '- ' + $_ }
    if ($failure) { $summary += @('', 'Failure: ' + $failure) }
    $summaryText = $summary -join [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $runRoot 'report.txt'), $summaryText)
    if (!$failure) { [IO.File]::WriteAllText((Join-Path $sourceRoot 'installer/TESTED-INSTALLER.txt'), $summaryText) }
    Write-Output $json
}
if ($failure) { throw $failure }
