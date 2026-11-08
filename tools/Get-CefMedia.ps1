#requires -Version 7.2
[CmdletBinding()]
param([string]$ArchivePath)

$ErrorActionPreference = 'Stop'
$version = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'cef-media-version.json') -Raw | ConvertFrom-Json
$cache = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\cef-native'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if (!$ArchivePath) {
    $ArchivePath = Join-Path $cache ($version.runtimeSha256 + '.zip')
    if (!(Test-Path -LiteralPath $ArchivePath)) {
        $partial = $ArchivePath + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
        try {
            Invoke-WebRequest -Uri $version.runtimeUrl -OutFile $partial
            if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $version.runtimeSha256) {
                throw 'Downloaded native CEF archive does not match the pinned SHA256.'
            }
            Move-Item -LiteralPath $partial -Destination $ArchivePath
        }
        finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial } }
    }
}
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $version.runtimeSha256) {
    throw 'Native CEF archive does not match the pinned SHA256.'
}
# Extract from the verified archive for each package build; never reuse modified native files.
$destination = Join-Path $cache ([Guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $destination
if ((Get-FileHash -LiteralPath (Join-Path $destination 'Release\libcef.dll') -Algorithm SHA256).Hash -ne $version.libcefSha256) {
    throw 'Extracted CEF library does not match the verified media build.'
}
Write-Output $destination
