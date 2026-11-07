#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DistributionRoot,
    [string]$MediaFixture
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'cef-media-version.json') -Raw | ConvertFrom-Json
$output = Join-Path $root 'artifacts\cef-media-verification'
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (!$MediaFixture) {
    $MediaFixture = Join-Path $output $version.mediaFixture
    if (!(Test-Path -LiteralPath $MediaFixture)) {
        Invoke-WebRequest -Uri "https://raw.githubusercontent.com/chromium/chromium/refs/tags/$($version.chromiumVersion)/media/test/data/$($version.mediaFixture)" -OutFile $MediaFixture
    }
}
if ((Get-FileHash -LiteralPath $MediaFixture -Algorithm SHA256).Hash -ne $version.mediaFixtureSha256) {
    throw 'Media fixture differs from the pinned Chromium H.264/AAC fixture.'
}
& dotnet build (Join-Path $root 'tests\CrimsonBrowser.CefTests\CrimsonBrowser.CefTests.csproj') -c Release --nologo -o $output
if ($LASTEXITCODE -ne 0) { throw 'CEF verification application could not be built.' }
& (Join-Path $PSScriptRoot 'Stage-CefMedia.ps1') -DistributionRoot $DistributionRoot -ApplicationDirectory $output
& (Join-Path $output 'CrimsonBrowser.CefTests.exe') --require-aac-h264 "--codec-fixture=$MediaFixture"
if ($LASTEXITCODE -ne 0) { throw 'Custom CEF failed decoding or browser regression checks. Do not package this engine.' }
Write-Output 'PASS: custom native engine passed local H.264/AAC playback and browser regression checks.'
