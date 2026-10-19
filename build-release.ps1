<#
.SYNOPSIS
  Release build (AAB/APK) with R8 obfuscation, on any machine.

.DESCRIPTION
  Google Play needs >= 25% obfuscation ("DEX code optimization"). The .NET Android SDK that ships with the
  installed workload (36.1.43) always injects "-dontobfuscate"; SDK 36.1.118 adds AndroidR8ObfuscationMode
  (set to private-members in the csproj). Until the installed workload is >= 36.1.118 this script downloads
  that SDK from NuGet ONCE into a per-user cache (nothing is committed to git, nothing machine-wide is
  touched), points the .NET SDK at it through two environment variables, and runs "dotnet publish".

  Cache folder: %ANDROID_SDK_36118_DIR% if set, else %LOCALAPPDATA%\android-sdk-36118 (about 700 MB).

.EXAMPLE
  .\build-release.ps1                 # Dev ads mode (sandbox / no ads): the build for Play testing tracks
  .\build-release.ps1 -Pro            # real ad units (AdsMode=Pro): the final production build
  .\build-release.ps1 -NoAds          # ads fully off (AdsEnabled=false)
  .\build-release.ps1 -Format apk
  .\build-release.ps1 -SetupOnly      # just download/prepare the SDK
  .\build-release.ps1 -- -p:ApplicationVersion=7   # anything after -- goes to dotnet publish
#>
param(
    [switch]$Pro,
    [switch]$NoAds,
    [ValidateSet('aab', 'apk')][string]$Format = 'aab',
    [switch]$SetupOnly,
    [Parameter(ValueFromRemainingArguments = $true)]$Extra
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is much faster without the progress bar (Windows PowerShell 5.1)

$SdkVersion = '36.1.118'
$Project = Join-Path $PSScriptRoot 'src\PdfReader\PdfReader.csproj'
$Packs = @(
    'Microsoft.Android.Sdk.Windows', 'Microsoft.Android.Ref.36', 'Microsoft.Android.Ref.36.1',
    'Microsoft.Android.Runtime.36.android', 'Microsoft.Android.Runtime.36.1.android',
    'Microsoft.Android.Runtime.Mono.36.android-arm', 'Microsoft.Android.Runtime.Mono.36.android-arm64',
    'Microsoft.Android.Runtime.Mono.36.android-x86', 'Microsoft.Android.Runtime.Mono.36.android-x64'
)

function Get-InstalledAndroidManifestVersion {
    $dotnetRoot = Split-Path (Get-Command dotnet).Source
    $best = [version]'0.0'
    foreach ($dir in Get-ChildItem (Join-Path $dotnetRoot 'sdk-manifests\*\microsoft.net.sdk.android\*') -Directory -ErrorAction SilentlyContinue) {
        $v = $null
        if ([version]::TryParse($dir.Name, [ref]$v) -and $v -gt $best) { $best = $v }
    }
    return $best
}

function Install-NuGetPackage([string]$Id, [string]$Dest) {
    if (Test-Path (Join-Path $Dest '.done')) { return }
    $lower = $Id.ToLowerInvariant()
    $url = "https://api.nuget.org/v3-flatcontainer/$lower/$SdkVersion/$lower.$SdkVersion.nupkg"
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "$lower.$SdkVersion.nupkg"
    Write-Host "  downloading $Id $SdkVersion"
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
    if (Test-Path $Dest) { Remove-Item $Dest -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Dest | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($tmp, $Dest)
    Remove-Item $tmp -Force
    New-Item -ItemType File -Path (Join-Path $Dest '.done') | Out-Null
}

if ((Get-InstalledAndroidManifestVersion) -ge [version]$SdkVersion) {
    Write-Host "Installed Android workload is already >= ${SdkVersion}: using it as is."
}
else {
    $cache = if ($env:ANDROID_SDK_36118_DIR) { $env:ANDROID_SDK_36118_DIR } else { Join-Path $env:LOCALAPPDATA 'android-sdk-36118' }
    Write-Host "Preparing .NET Android SDK $SdkVersion in $cache (first run only)"

    foreach ($id in $Packs) { Install-NuGetPackage $id (Join-Path $cache "packs\$id\$SdkVersion") }

    # Workload manifest, under the 10.0.100 band and under the band of the installed .NET SDK.
    $manifestId = 'Microsoft.NET.Sdk.Android.Manifest-10.0.100'
    $staging = Join-Path $cache "manifest-$SdkVersion"
    Install-NuGetPackage $manifestId $staging
    $dotnetVersion = (& dotnet --version).Trim()
    $band = ($dotnetVersion -replace '^(\d+)\.(\d+)\.(\d)\d\d.*$', '${1}.${2}.${3}00')
    foreach ($b in @('10.0.100', $band) | Select-Object -Unique) {
        $target = Join-Path $cache "sdk-manifests\$b\microsoft.net.sdk.android\$SdkVersion"
        if (-not (Test-Path (Join-Path $target 'WorkloadManifest.json'))) {
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Copy-Item (Join-Path $staging 'data\*') $target -Recurse -Force
        }
    }

    $env:DOTNETSDK_WORKLOAD_MANIFEST_ROOTS = Join-Path $cache 'sdk-manifests'
    $env:DOTNETSDK_WORKLOAD_PACK_ROOTS = $cache
}

if ($SetupOnly) { Write-Host 'SDK ready.'; return }

$publishArgs = @('publish', $Project, '-f', 'net10.0-android', '-c', 'Release', "-p:AndroidPackageFormat=$Format")
if ($Pro) { $publishArgs += '-p:AdsMode=Pro' }
if ($NoAds) { $publishArgs += '-p:AdsEnabled=false' }
if ($Extra) { $publishArgs += ($Extra | Where-Object { $_ -ne '--' }) }

Write-Host "dotnet $($publishArgs -join ' ')"
& dotnet @publishArgs
exit $LASTEXITCODE
