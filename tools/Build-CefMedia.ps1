#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('Check', 'Sync', 'Build', 'Package', 'All')]
    [string]$Phase = 'Check',
    [string]$BuildRoot = 'D:\CrimsonCEF',
    [ValidateRange(1, 8)][int]$Jobs = 4
)

$ErrorActionPreference = 'Stop'
$version = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'cef-media-version.json') -Raw | ConvertFrom-Json
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
if ($BuildRoot.Length -gt 30 -or $BuildRoot -match '[^a-zA-Z0-9:\\_-]') {
    throw 'CEF requires a short ASCII build path without spaces.'
}
$tools = Join-Path $BuildRoot 'depot_tools'
$python = Join-Path $tools '.cipd_bin\3.11\bin\python3.exe'
$source = Join-Path $BuildRoot 'source\chromium\src'
$cef = Join-Path $source 'cef'
$automate = Join-Path $BuildRoot 'automate-git.py'
$logRoot = Join-Path $BuildRoot 'logs'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer was not found.' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Workload.NativeDesktop Microsoft.VisualStudio.Component.VC.ATLMFC -property installationPath
if (!$vs) { throw 'Visual Studio Desktop C++ and MFC/ATL components are required.' }
if (!(Test-Path -LiteralPath $python)) { throw 'Run the official depot_tools update_depot_tools.bat bootstrap first.' }
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($BuildRoot))
Write-Output "CEF $($version.cefVersion), Chromium $($version.chromiumVersion), $Jobs parallel jobs"
Write-Output "Free space: $([math]::Round($drive.AvailableFreeSpace / 1GB, 1)) GiB; work directory: $BuildRoot"
if ($Phase -eq 'Check') { return }
if ($Phase -in @('Sync', 'All') -and $drive.AvailableFreeSpace -lt 100GB) {
    throw 'Source synchronization requires at least 100 GiB free. Existing files will not be removed.'
}
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$lock = [IO.File]::Open((Join-Path $BuildRoot 'engine-build.lock'), [IO.FileMode]::OpenOrCreate,
    [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)

# These settings affect this build process and its children only.
$environment = @{}
foreach ($name in @('PATH', 'DEPOT_TOOLS_WIN_TOOLCHAIN', 'DEPOT_TOOLS_UPDATE', 'GN_DEFINES', 'GN_ARGUMENTS', 'GN_OUT_CONFIGS', 'GYP_MSVS_OVERRIDE_PATH')) {
    $environment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
function Invoke-BuildTool([string]$Executable, [string[]]$Arguments, [string]$Name, [string]$WorkingDirectory) {
    $stdout = Join-Path $logRoot ($Name + '.stdout.log')
    $stderr = Join-Path $logRoot ($Name + '.stderr.log')
    Write-Output "Starting $Name. Logs: $stdout and $stderr"
    if ([IO.Path]::GetExtension($Executable) -eq '.bat') {
        $Arguments = @('/d', '/c', $Executable) + $Arguments
        $Executable = $env:ComSpec
    }
    # All arguments are controlled paths without spaces or pinned revision identifiers.
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory `
        -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    try {
        $process.PriorityClass = 'BelowNormal'
        while (!$process.WaitForExit(15000)) {
            if ($drive.AvailableFreeSpace -lt 12GB) {
                $process.Kill($true)
                throw 'Stopped the owned build process to preserve 12 GiB of free disk space. Its checkout is resumable.'
            }
        }
        if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode). See $stderr" }
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

try {
    $env:PATH = "$tools;$env:PATH"
    $env:DEPOT_TOOLS_WIN_TOOLCHAIN = '0'
    $env:DEPOT_TOOLS_UPDATE = '0'
    $env:GYP_MSVS_OVERRIDE_PATH = $vs.Trim()
    $env:GN_OUT_CONFIGS = 'Release_GN_x64'
    $env:GN_ARGUMENTS = $null
    $env:GN_DEFINES = 'is_official_build=true is_component_build=false proprietary_codecs=true ffmpeg_branding=Chrome symbol_level=0 blink_symbol_level=0 v8_symbol_level=0 use_thin_lto=false chrome_pgo_phase=0'
    if ($Phase -in @('Sync', 'All')) {
        if (!(Test-Path -LiteralPath $automate)) {
            Invoke-WebRequest -Uri "https://raw.githubusercontent.com/chromiumembedded/cef/$($version.cefCommit)/tools/automate/automate-git.py" -OutFile $automate
        }
        if ((Get-FileHash -LiteralPath $automate -Algorithm SHA256).Hash -ne $version.automateSha256) {
            throw 'Official CEF automation script checksum does not match the pinned revision.'
        }
        Invoke-BuildTool $python @($automate, "--download-dir=$BuildRoot\source", "--depot-tools-dir=$tools",
            "--branch=$($version.branch)", "--checkout=$($version.cefCommit)",
            "--chromium-checkout=refs/tags/$($version.chromiumVersion)", '--no-chromium-history',
            '--no-build', '--no-distrib', '--x64-build', '--no-depot-tools-update') 'sync' $BuildRoot
    }
    if ($Phase -in @('Build', 'Package', 'All')) {
        if (!(Test-Path -LiteralPath (Join-Path $cef 'CHROMIUM_BUILD_COMPATIBILITY.txt'))) { throw 'CEF source checkout is incomplete.' }
        $actual = & git -C $cef rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or $actual.Trim() -ne $version.cefCommit) { throw 'CEF checkout differs from the pinned CefSharp-compatible revision.' }
    }
    if ($Phase -in @('Build', 'All')) {
        Invoke-BuildTool $python @((Join-Path $cef 'tools\gclient_hook.py')) 'generate' $cef
        Invoke-BuildTool (Join-Path $tools 'autoninja.bat') @('-C', 'out\Release_GN_x64', '-j', "$Jobs", 'cefclient', 'bootstrap', 'bootstrapc') 'compile' $source
    }
    if ($Phase -in @('Package', 'All')) {
        Invoke-BuildTool $python @((Join-Path $cef 'tools\make_distrib.py'), "--output-dir=$cef\binary_distrib", '--ninja-build', '--x64-build',
            '--minimal', '--allow-partial', '--no-symbols', '--no-docs', '--no-archive') 'package' $cef
        Write-Output "Native distribution is under $cef\binary_distrib. It must pass media and browser regression checks before use."
    }
}
finally {
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process') }
    $lock.Dispose()
}
