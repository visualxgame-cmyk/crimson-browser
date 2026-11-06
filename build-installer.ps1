# Builds a self-contained Crimson Browser and wraps it in a Windows installer.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build-installer.ps1
#
# Requirements:
#   * .NET 10 SDK
#   * Inno Setup 6 or 7 (free, https://jrsoftware.org/isdl.php)
#
# Output:
#   dist\CrimsonBrowser-Setup-<version>.exe
#   dist\CrimsonBrowser-Setup-<version>.exe.sha256
#   dist\publish\ (the raw application folder, useful for debugging)

param(
    [switch]$SkipPublish,
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$csproj = Join-Path $root "CrimsonBrowser.csproj"
$publishDir = Join-Path $root "dist\publish"
$iss = Join-Path $root "installer\CrimsonBrowser.iss"

# Keep the installer version and asset name in sync with the project version.
# Release builds pass -Version (for example 1.0.1) so the tag, executable and installer match.
if ($Version) {
    $version = $Version.TrimStart('v', 'V')
}
else {
    [xml]$project = Get-Content $csproj
    $version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $version) { $version = "1.0.0" }
}

function Find-Iscc {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "C:\Program Files\Inno Setup 7\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    )
    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $dotnet = "C:\Program Files\dotnet\dotnet.exe"
}
if (-not (Test-Path $dotnet)) {
    throw "The .NET SDK was not found. Install .NET 10 from https://dotnet.microsoft.com/download"
}

$iscc = Find-Iscc
if (-not $iscc) {
    throw "Inno Setup was not found. Install it (free) from https://jrsoftware.org/isdl.php and run this script again."
}

# The WebView2 Evergreen bootstrapper is bundled so the installer can set up the runtime
# when it is missing. It is downloaded from Microsoft on demand and is not committed.
$redistDir = Join-Path $root "installer\redist"
$webView2Bootstrapper = Join-Path $redistDir "MicrosoftEdgeWebView2Setup.exe"
if (-not (Test-Path $webView2Bootstrapper)) {
    Write-Host "Downloading the Microsoft Edge WebView2 bootstrapper..."
    New-Item -ItemType Directory -Path $redistDir -Force | Out-Null
    Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" `
        -OutFile $webView2Bootstrapper -UseBasicParsing
    if (-not (Test-Path $webView2Bootstrapper)) {
        throw "Could not download the WebView2 bootstrapper from Microsoft."
    }
}

# The running browser locks its own binaries.
$running = Get-Process CrimsonBrowser -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping the running Crimson Browser..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

if (-not $SkipPublish) {
    Write-Host "Publishing Crimson Browser (self-contained, win-x64)..."
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    & $dotnet publish $csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:Version=$version `
        -p:PublishReadyToRun=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
}

if (-not (Test-Path (Join-Path $publishDir "CrimsonBrowser.exe"))) {
    throw "CrimsonBrowser.exe was not found in $publishDir. Run the publish step first."
}

Write-Host "Building the installer with Inno Setup..."
Push-Location (Split-Path -Parent $iss)
try {
    & $iscc "/DMyAppVersion=$version" (Split-Path -Leaf $iss)
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
}
finally {
    Pop-Location
}

$setup = Join-Path $root "dist\CrimsonBrowser-Setup-$version.exe"
if (-not (Test-Path $setup)) {
    throw "The installer was not produced at $setup"
}

# GitHub auto-updates verify this published SHA-256 checksum before running the installer.
$checksumPath = "$setup.sha256"
$hash = (Get-FileHash -Algorithm SHA256 -Path $setup).Hash.ToLowerInvariant()
Set-Content -Path $checksumPath -Value "$hash  $(Split-Path -Leaf $setup)" -Encoding ascii

$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "Installer ready: $setup ($sizeMb MB)" -ForegroundColor Green
Write-Host "Checksum ready:  $checksumPath" -ForegroundColor Green
Write-Host "The installer is unsigned; Windows SmartScreen may show a warning on first run."
exit 0
