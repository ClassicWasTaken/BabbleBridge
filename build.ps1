# Copyright (c) 2026 Frame Bridge contributors.
# SPDX-License-Identifier: Apache-2.0

param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$destination = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist/BabbleFrameBridge'))
$destinationPrefix = $destination.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

# Retire exact files from earlier publications before publishing into a reused folder.
# Never touch the separate AppData directory containing users' imported modules.
$legacyFiles = @(
    'vcruntime140.dll', 'runtime/vcruntime140.dll',
    'Assets/Baballonia-module.zip',
    'Assets/Brand/Babble-Brand-LICENSE.txt', 'Assets/Brand/BabbleLogos.xaml',
    'Assets/Brand/babbleBanner.png', 'Assets/Brand/logo.png',
    'Assets/Brand/logo.svg', 'Assets/Brand/logo-white.svg',
    'Assets/Brand/logo_silhouette.svg', 'Assets/Brand/SOURCE.txt'
)
foreach ($relative in $legacyFiles) {
    $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (!$target.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected legacy payload path.' }
    if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }
}

& $Dotnet publish (Join-Path $projectRoot 'src/BabbleFrameBridge.App/BabbleFrameBridge.App.csproj') -c Release -r win-x64 --self-contained true -o $destination --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows app publish failed.' }
& $Dotnet publish (Join-Path $projectRoot 'src/BabbleFrameBridge.Worker/BabbleFrameBridge.Worker.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $destination 'runtime') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tracking worker publish failed.' }
# Reused publish folders can retain symbols from builds made before source-path
# mapping was enabled. Distribution symbols are deliberately omitted.
foreach ($symbols in Get-ChildItem -LiteralPath $destination -Recurse -File -Filter '*.pdb') {
    $symbolsPath = [IO.Path]::GetFullPath($symbols.FullName)
    if (!$symbolsPath.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected symbol output path.' }
    Remove-Item -LiteralPath $symbolsPath -Force
}
$unwanted = Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object {
    $_.Name -in @('vcruntime140.dll', 'VRCFaceTracking.Baballonia.dll', 'Baballonia-module.zip',
        'VirtualDesktop.FaceTracking.dll', 'SteamLinkVRCFTModule.dll', 'LinkFTModule.zip', 'VirtualDesktop.VRCFaceTracking.zip',
        'Babble-Brand-LICENSE.txt', 'BabbleLogos.xaml', 'babbleBanner.png', 'logo_silhouette.svg', 'logo-white.svg')
} | Select-Object -First 1
if ($unwanted) { throw "The published app contains an excluded legacy or external component: $($unwanted.FullName)" }
foreach ($file in @('START-HERE.txt', 'LICENSE', 'NOTICE.txt', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $destination -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $destination -Recurse -Force
Write-Output "Portable app: $destination"
