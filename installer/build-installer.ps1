# Copyright (c) 2026 Frame Bridge contributors.
# SPDX-License-Identifier: Apache-2.0

param(
    [string]$PayloadDirectory = (Join-Path $PSScriptRoot '../dist/BabbleFrameBridge'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../dist/installer'),
    [string]$Compiler
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-ExistingFileSystemPath([string]$Path, [string]$Description) {
    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    if ($resolved.Provider.Name -ne 'FileSystem') {
        throw "$Description must be a file-system path."
    }
    return [IO.Path]::GetFullPath($resolved.ProviderPath)
}

function Get-NormalizedVersion([string]$Text) {
    $parsed = $null
    if ($Text -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or ![Version]::TryParse($Text, [ref]$parsed)) {
        throw "Use a numeric release version, such as 1.2.0 or 1.2.0.0. Received: $Text"
    }
    return [Version]::new($parsed.Major, $parsed.Minor, [Math]::Max(0, $parsed.Build), [Math]::Max(0, $parsed.Revision))
}

$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $sourceRoot 'src/BabbleFrameBridge.App/BabbleFrameBridge.App.csproj'
$scriptPath = Get-ExistingFileSystemPath (Join-Path $PSScriptRoot 'FrameBridge.iss') 'Installer script'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$versionNodes = @($project.SelectNodes('/Project/PropertyGroup/Version'))
if ($versionNodes.Count -ne 1) { throw 'The app project must declare exactly one Version value.' }
$appVersion = $versionNodes[0].InnerText.Trim()
$expectedVersion = Get-NormalizedVersion $appVersion

$payloadPath = Get-ExistingFileSystemPath $PayloadDirectory 'Payload directory'
if (!(Test-Path -LiteralPath $payloadPath -PathType Container)) { throw 'PayloadDirectory must be a directory.' }
$requiredFiles = @(
    'BabbleFrameBridge.exe',
    'runtime/BabbleFrameBridge.Worker.exe',
    'Assets/Brand/FaceTrackingBridge-headset-v2.ico',
    'START-HERE.txt',
    'LICENSE',
    'NOTICE.txt',
    'THIRD_PARTY_NOTICES.md',
    'licenses/VRCFaceTracking-LICENSE.txt'
)
foreach ($requiredFile in $requiredFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $payloadPath $requiredFile) -PathType Leaf)) {
        throw "The published payload is incomplete: $requiredFile is missing. Run build.ps1 first."
    }
}
$unwantedModule = Get-ChildItem -LiteralPath $payloadPath -Recurse -File | Where-Object {
    $_.Name -in @('VRCFaceTracking.Baballonia.dll', 'VirtualDesktop.FaceTracking.dll', 'SteamLinkVRCFTModule.dll') -or ($_.Extension -eq '.zip' -and $_.Name -like '*Baballonia*')
} | Select-Object -First 1
if ($unwantedModule) {
    throw "The app payload contains a tracking module: $($unwantedModule.FullName). Remove it and publish the module-free app again. Users must provide their own ZIP."
}
$unsupportedRuntime = Get-ChildItem -LiteralPath $payloadPath -Recurse -File -Filter 'vcruntime140.dll' | Select-Object -First 1
if ($unsupportedRuntime) {
    throw "The app payload contains an app-local vcruntime140.dll: $($unsupportedRuntime.FullName). Install Microsoft's official Visual C++ v14 Redistributable separately; do not package a DLL copied from Windows."
}
$legacyBranding = @(
    'Assets/Brand/logo.png', 'Assets/Brand/logo.svg', 'Assets/Brand/logo-white.svg',
    'Assets/Brand/logo_silhouette.svg', 'Assets/Brand/babbleBanner.png',
    'Assets/Brand/BabbleLogos.xaml', 'Assets/Brand/Babble-Brand-LICENSE.txt'
) | Where-Object { Test-Path -LiteralPath (Join-Path $payloadPath $_) -PathType Leaf } | Select-Object -First 1
if ($legacyBranding) { throw "The app payload retains an excluded official branding asset: $legacyBranding. Publish again with the application's original artwork." }
$appExecutable = Join-Path $payloadPath 'BabbleFrameBridge.exe'
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appExecutable).FileVersion
$actualVersion = Get-NormalizedVersion $fileVersion
if ($actualVersion -ne $expectedVersion) {
    throw "Published app version $fileVersion does not match project version $appVersion. Publish the app again before building its installer."
}

if ([string]::IsNullOrWhiteSpace($Compiler)) {
    $compilerCommand = Get-Command iscc.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($compilerCommand) { $Compiler = $compilerCommand.Source }
    if ([string]::IsNullOrWhiteSpace($Compiler)) {
        $locations = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))
        $candidates = foreach ($major in @(7)) {
            foreach ($location in $locations) {
                if (![string]::IsNullOrWhiteSpace($location)) { Join-Path $location "Inno Setup $major/ISCC.exe" }
            }
        }
        $Compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if ([string]::IsNullOrWhiteSpace($Compiler)) {
    throw 'Inno Setup Compiler was not found. Install Inno Setup 7 from https://jrsoftware.org/isdl.php or pass -Compiler with the absolute path to ISCC.exe.'
}
$compilerPath = Get-ExistingFileSystemPath $Compiler 'Compiler'
if (!(Test-Path -LiteralPath $compilerPath -PathType Leaf)) { throw 'Compiler must point to ISCC.exe.' }
$compilerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($compilerPath)
$compilerMajorVersion = $compilerVersion.FileMajorPart
if ($compilerMajorVersion -eq 0) {
    # Official ISCC 7.1.1 carries a 0.0.0.0 Windows file resource; ask the engine.
    $engineVersion = (& $compilerPath --version 2>&1 | Out-String).Trim()
    if ($engineVersion -match '(?m)^\s*(\d+)\.\d+\.\d+\s*$') { $compilerMajorVersion = [int]$Matches[1] }
    elseif ($engineVersion -match 'Inno Setup (\d+)') { $compilerMajorVersion = [int]$Matches[1] }
}
if ($compilerMajorVersion -lt 7) { throw 'Inno Setup 7 or newer is required for the installer styling and directives. Use the tested Inno Setup 7 compiler.' }

$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $outputPath -Force
$outputName = "FaceTracking-Bridge-Setup-$appVersion"
$compilerArguments = @(
    "--define=AppPayload=$payloadPath",
    "--define=AppVersion=$appVersion",
    "--output-dir=$outputPath",
    "--output-filename=$outputName",
    $scriptPath
)

Write-Output "Building Face Tracking Bridge $appVersion installer..."
& $compilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }

$installerPath = Join-Path $outputPath "$outputName.exe"
if (!(Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw 'The compiler reported success but the expected installer was not produced.' }
$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = "$installerPath.sha256"
[IO.File]::WriteAllText($hashPath, "$hash  $outputName.exe`r`n", [Text.UTF8Encoding]::new($false))
Write-Output "Installer: $installerPath"
Write-Output "SHA-256: $hashPath"
