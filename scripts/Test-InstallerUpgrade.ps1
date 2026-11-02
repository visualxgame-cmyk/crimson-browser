[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OldPublishDirectory,
    [Parameter(Mandatory)][string]$NewPublishDirectory,
    [string]$OldVersion = '0.1.0',
    [string]$NewVersion = '0.1.1',
    [string]$Compiler = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$Compiler) { $Compiler = Join-Path $projectRoot 'artifacts\tools\InnoSetup643\ISCC.exe' }
$oldPublish = (Resolve-Path -LiteralPath $OldPublishDirectory).Path
$newPublish = (Resolve-Path -LiteralPath $NewPublishDirectory).Path
foreach ($version in @($OldVersion, $NewVersion)) { if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid test version.' } }
foreach ($source in @($oldPublish, $newPublish)) { if (!(Test-Path -LiteralPath (Join-Path $source 'Vela.exe'))) { throw 'Publish directory has no Vela.exe.' } }
$testRoot = Join-Path $projectRoot ('artifacts\upgrade-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $testRoot 'Install'
$data = Join-Path $testRoot 'IsolatedProfile'
New-Item -ItemType Directory -Path $testRoot,$data -Force | Out-Null
$oldOutput = Join-Path $testRoot 'old'; $newOutput = Join-Path $testRoot 'new'
foreach ($build in @(@{ Source=$oldPublish; Output=$oldOutput; Version=$OldVersion }, @{ Source=$newPublish; Output=$newOutput; Version=$NewVersion })) {
    & $Compiler "/DPublishDir=$($build.Source)" "/DReleaseDir=$($build.Output)" "/DAppVersion=$($build.Version)" '/DProductId=VelaBrowser.UpgradeSmoke' (Join-Path $projectRoot 'installer\Vela.iss') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Upgrade test installer compilation failed.' }
}
function Install-TestVersion([string]$Directory, [string]$Version) {
    $setup = Join-Path $Directory "Vela-$Version-win-x64-setup.exe"
    $arguments = @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/DIR="' + $install + '"'), ('/LOG="' + (Join-Path $testRoot "install-$Version.log") + '"'))
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Test installation failed ($($process.ExitCode)). Check the test log; an open Vela instance may block installation." }
    $executable = Join-Path $install 'Vela.exe'
    if (![Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion.StartsWith($Version + '.')) { throw 'Installed executable version is wrong.' }
}
Install-TestVersion $oldOutput $OldVersion
$named = 'bd34a3d7aeba40af884beb8d4479f1f4'
$fixtures = @{
    'Data\settings.json' = '{"Version":3,"EconomyMode":true,"MaxLiveTabs":8,"HomePage":"https://preserve.test"}'
    'Data\bookmarks.json' = '[{"Url":"https://preserve.test","Title":"Păstrat","AddedAt":"2026-01-01T00:00:00Z","Id":"upgrade-favorite"}]'
    'Data\reading-list.json' = '[{"Url":"https://reading.test","Title":"Lectură","AddedAt":"2026-01-01T00:00:00Z","Id":"upgrade-reading","IsRead":true}]'
    'profiles.json' = ('[{"Id":"default","Name":"Principal"},{"Id":"' + $named + '","Name":"Lucru"}]')
    "UserProfiles\$named\Data\settings.json" = '{"Version":3,"Theme":"vela","MaxLiveTabs":7}'
    'Profile\native-data-sentinel.bin' = 'Synthetic native-data sentinel; not real cookies or credentials.'
}
$hashes = @{}
foreach ($entry in $fixtures.GetEnumerator()) {
    $path = Join-Path $data $entry.Key
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, $entry.Value)
    $hashes[$entry.Key] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
function Assert-ProfileUnchanged {
    foreach ($name in $hashes.Keys) { if ((Get-FileHash -LiteralPath (Join-Path $data $name) -Algorithm SHA256).Hash -ne $hashes[$name]) { throw "Profile fixture changed: $name" } }
}
Install-TestVersion $newOutput $NewVersion
if ((Get-FileHash -LiteralPath (Join-Path $install 'Vela.exe') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $newPublish 'Vela.exe') -Algorithm SHA256).Hash) { throw 'Upgrade did not install the new binary.' }
Assert-ProfileUnchanged
# Only this test's isolated installation is removed. The real Vela installation is never targeted.
$uninstaller = (Resolve-Path -LiteralPath (Join-Path $install 'unins000.exe')).Path
if (!$uninstaller.StartsWith([IO.Path]::GetFullPath($testRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Uninstaller escaped the isolated test directory.' }
$uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -PassThru -Wait
if ($uninstall.ExitCode -ne 0) { throw 'Isolated uninstall failed.' }
Assert-ProfileUnchanged
if (Test-Path -LiteralPath (Join-Path $install 'Vela.exe')) { throw 'Uninstall left the application executable.' }
[ordered]@{ oldVersion=$OldVersion; newVersion=$NewVersion; upgradeVerified=$true; uninstallVerified=$true; preservedFixtures=$fixtures.Count;
    limitation='Installer test with isolated fixture data outside the application folder; no real browser profile or login used. Native cookie persistence is tested separately by the smoke harness.' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'result.json') -Encoding utf8NoBOM
Write-Output "PASS: upgrade $OldVersion -> $NewVersion and uninstall preserve all $($fixtures.Count) profile fixtures. Report: $testRoot\result.json"
