#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DistributionRoot,
    [Parameter(Mandatory)][string]$ApplicationDirectory
)

$ErrorActionPreference = 'Stop'
$version = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'cef-media-version.json') -Raw | ConvertFrom-Json
$DistributionRoot = (Resolve-Path -LiteralPath $DistributionRoot).Path
$ApplicationDirectory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$release = Join-Path $DistributionRoot 'Release'
$resources = Join-Path $DistributionRoot 'Resources'
foreach ($file in @('Release\libcef.dll', 'Resources\icudtl.dat', 'Resources\resources.pak', 'Resources\locales\en-US.pak')) {
    if (!(Test-Path -LiteralPath (Join-Path $DistributionRoot $file) -PathType Leaf)) { throw "Incomplete CEF distribution: $file" }
}
if (!(Test-Path -LiteralPath (Join-Path $ApplicationDirectory 'CefSharp.Core.Runtime.dll') -PathType Leaf)) {
    throw 'The destination must be a built Crimson/CefSharp application directory.'
}
$native = Join-Path $release 'libcef.dll'
$actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($native).FileVersion
$expected = "$($version.cefVersion)+g$($version.cefCommit.Substring(0, 7))+chromium-$($version.chromiumVersion)"
if ($actual -ne $expected) { throw "Native CEF version mismatch. Expected $expected; received $actual" }
$stream = [IO.File]::OpenRead($native)
$reader = [Reflection.PortableExecutable.PEReader]::new($stream)
try {
    if ($reader.PEHeaders.CoffHeader.Machine.ToString() -ne 'Amd64') { throw 'Native CEF must be Windows x64.' }
}
finally { $reader.Dispose(); $stream.Dispose() }

# Keep CefSharp's managed/native bridge; replace the matching CEF runtime as one distribution.
$files = @(
    Get-ChildItem -LiteralPath $release -File -Recurse | Where-Object { $_.Extension -in @('.dll', '.bin', '.dat', '.pak', '.json') }
    Get-ChildItem -LiteralPath $resources -File -Recurse
)
$manifest = foreach ($file in $files) {
    $base = if ($file.FullName.StartsWith($release + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { $release } else { $resources }
    $relative = [IO.Path]::GetRelativePath($base, $file.FullName)
    $destination = Join-Path $ApplicationDirectory $relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $sourceHash) { throw "Copy verification failed: $relative" }
    [ordered]@{ path = $relative; sha256 = $sourceHash.ToLowerInvariant() }
}
[ordered]@{ nativeVersion = $actual; files = @($manifest) } | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $ApplicationDirectory 'crimson-cef-native.json') -Encoding utf8
Write-Output "Staged and verified $($files.Count) native CEF files. Media decoding must be tested separately."
