#requires -Version 7.0
# Copyright (c) 2026 Frame Bridge contributors.
# SPDX-License-Identifier: Apache-2.0

param(
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$ModuleZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compilerPath = (Resolve-Path -LiteralPath $Compiler).ProviderPath
$payloadSource = (Resolve-Path -LiteralPath $PayloadDirectory).ProviderPath
$externalModuleZip = (Resolve-Path -LiteralPath $ModuleZip).ProviderPath
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
$registryKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${testId}_is1"
$userDataPath = Join-Path $env:LOCALAPPDATA 'BabbleFrameBridge'
$checks = [Collections.Generic.List[string]]::new()
$failure = $null

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
        '--define=ProductName=FrameBridgeInstallerTest',
        "--define=AppMutexName=$testMutex",
        "--output-dir=$compiledPath",
        "--output-filename=FrameBridgeTest-$Version",
        (Join-Path $sourceRoot 'installer/FrameBridge.iss')
    )
    $exitCode = Invoke-TestProcess $compilerPath $arguments "compile-$Version.log"
    Assert-True ($exitCode -eq 0) "Test installer compilation failed ($Version). See compile-$Version.log."
}
function Invoke-TestSetup([string]$SetupPath, [string]$LogLabel, [bool]$FirstInstall = $false) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', "/LOG=$(Join-Path $runRoot "$LogLabel-inno.log")")
    if ($FirstInstall) { $arguments += "/DIR=$installPath" }
    return Invoke-TestProcess $SetupPath $arguments "$LogLabel-process.log"
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
function Uninstall-TestProduct {
    Assert-OwnedPath $installPath
    $null = Read-TestRegistration
    $uninstallers = @(Get-ChildItem -LiteralPath $installPath -File -Filter 'unins*.exe')
    Assert-True ($uninstallers.Count -eq 1) 'Expected one uninstaller inside the isolated install directory.'
    Assert-OwnedPath $uninstallers[0].FullName
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $runRoot 'uninstall-inno.log')")
    $exitCode = Invoke-TestProcess $uninstallers[0].FullName $arguments 'uninstall-process.log'
    Assert-True ($exitCode -eq 0) "Isolated uninstaller exited with $exitCode."
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Test-Path -LiteralPath $registryKey) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Assert-True (!(Test-Path -LiteralPath $registryKey)) 'Test uninstall registration remains after uninstall.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $installPath 'BabbleFrameBridge.exe'))) 'Installed app remains after uninstall.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $installPath 'runtime/BabbleFrameBridge.Worker.exe'))) 'Installed worker remains after uninstall.'
}

$before = Get-UserDataSnapshot
[IO.File]::WriteAllText((Join-Path $runRoot 'user-data-before.json'), $before)
try {
    Assert-True (!(Test-Path -LiteralPath $registryKey)) 'The generated test AppId unexpectedly exists.'
    Assert-OwnedPath $payload
    Assert-OwnedPath $installPath
    foreach ($item in Get-ChildItem -LiteralPath $payloadSource -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $payload -Recurse -Force
    }
    $marker = Join-Path $payload 'installer-smoke-marker.txt'
    [IO.File]::WriteAllText($marker, 'first-release')
    Compile-TestInstaller '1.2.0'
    $firstSetup = Join-Path $compiledPath 'FrameBridgeTest-1.2.0.exe'
    $exitCode = Invoke-TestSetup $firstSetup 'install' $true
    Assert-True ($exitCode -eq 0) "First installation failed with exit code $exitCode."
    $registration = Read-TestRegistration
    Assert-True ($registration.DisplayVersion -eq '1.2.0') 'First install version is incorrect.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $installPath 'installer-smoke-marker.txt') -Raw) -eq 'first-release') 'Initial payload marker was not installed.'
    foreach ($relative in @('BabbleFrameBridge.exe', 'BabbleFrameBridge.dll', 'runtime/BabbleFrameBridge.Worker.exe')) {
        $expectedHash = (Get-FileHash -LiteralPath (Join-Path $payload $relative) -Algorithm SHA256).Hash
        $actualHash = (Get-FileHash -LiteralPath (Join-Path $installPath $relative) -Algorithm SHA256).Hash
        Assert-True ($expectedHash -eq $actualHash) "Installed payload differs: $relative"
    }
    Assert-True (!(Test-Path -LiteralPath (Join-Path $installPath 'Assets/Baballonia-module.zip'))) 'The installer included a module archive.'
    Assert-True (@(Get-ChildItem -LiteralPath $installPath -Recurse -File -Filter 'VRCFaceTracking.Baballonia.dll').Count -eq 0) 'The installer included a module DLL.'
    Add-Check 'First install created one test uninstall entry and extracted the complete verified app payload.'

    $exitCode = Invoke-TestSetup $firstSetup 'reinstall'
    Assert-True ($exitCode -eq 0) "Same-version reinstall failed with $exitCode."
    $registration = Read-TestRegistration
    Assert-True ($registration.DisplayVersion -eq '1.2.0') 'Reinstall changed the registered version unexpectedly.'
    Add-Check 'Same-version reinstall reused the installation directory and uninstall entry.'

    [IO.File]::WriteAllText($marker, 'upgraded-release')
    $legacyAssets = Join-Path $installPath 'Assets'
    $null = New-Item -ItemType Directory -Path $legacyAssets -Force
    [IO.File]::WriteAllText((Join-Path $legacyAssets 'Baballonia-module.zip'), 'obsolete-bundled-fixture')
    Compile-TestInstaller '1.2.1'
    $exitCode = Invoke-TestSetup (Join-Path $compiledPath 'FrameBridgeTest-1.2.1.exe') 'upgrade'
    Assert-True ($exitCode -eq 0) "Newer-version upgrade failed with $exitCode."
    $registration = Read-TestRegistration
    Assert-True ($registration.DisplayVersion -eq '1.2.1') 'Upgrade did not update DisplayVersion.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $installPath 'installer-smoke-marker.txt') -Raw) -eq 'upgraded-release') 'Upgrade did not replace the payload marker.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $legacyAssets 'Baballonia-module.zip'))) 'Upgrade retained the legacy baked-in archive.'
    Add-Check 'A newer synthetic release replaced installed files and updated the same uninstall entry.'

    $exitCode = Invoke-TestSetup $firstSetup 'downgrade'
    Assert-True ($exitCode -ne 0) 'The old installer unexpectedly allowed a downgrade.'
    $registration = Read-TestRegistration
    Assert-True ($registration.DisplayVersion -eq '1.2.1') 'Downgrade rejection changed the installed version.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $installPath 'installer-smoke-marker.txt') -Raw) -eq 'upgraded-release') 'Downgrade rejection changed installed files.'
    Add-Check 'An older installer was rejected before changing installed version or files.'

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
    Add-Check 'The isolated uninstaller removed installed app files and its test registration.'
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
        ProductionAppIdUsed = $false; Versions = @('1.2.0', '1.2.1 (synthetic installer metadata; same real app binaries)')
        AvatarDiscovery = 'Not tested; existing --all-parameters diagnostic mode avoids any running VRChat client.'
        Checks = $checks.ToArray(); Failure = $failure
    }
    $json = ConvertTo-Json $report -Depth 6
    [IO.File]::WriteAllText((Join-Path $runRoot 'report.json'), $json)
    $summary = @('BABBLE BRIDGE INSTALLER VALIDATION', '', "Result: $result", '', 'Isolated per-user install, reinstall, upgrade, downgrade rejection and uninstall.', 'The test used a separate random application identity and a workspace install folder.', 'The synthetic newer installer tested upgrade behavior without changing production version.', 'Installed-worker mapping used the existing --all-parameters diagnostic mode.', 'VRChat avatar discovery was excluded to avoid interference with a running local game.', '')
    $summary += $checks | ForEach-Object { '- ' + $_ }
    if ($failure) { $summary += @('', 'Failure: ' + $failure) }
    $summaryText = $summary -join [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $runRoot 'report.txt'), $summaryText)
    if (!$failure) { [IO.File]::WriteAllText((Join-Path $sourceRoot 'installer/TESTED-INSTALLER.txt'), $summaryText) }
    Write-Output $json
}
if ($failure) { throw $failure }
