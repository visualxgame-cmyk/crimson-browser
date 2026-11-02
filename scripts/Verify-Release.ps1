[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [switch]$RequireSignature,
    [string]$ExpectedSignerThumbprint = ''
)
$ErrorActionPreference = 'Stop'
$releaseRoot = (Resolve-Path -LiteralPath $Directory).Path
$manifest = Get-Content -LiteralPath (Join-Path $releaseRoot 'release-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.architecture -ne 'win-x64' -or !$manifest.files) { throw 'Invalid release manifest.' }
foreach ($entry in $manifest.files) {
    if ([IO.Path]::GetFileName($entry.name) -ne $entry.name) { throw 'Invalid release filename.' }
    $path = Join-Path $releaseRoot $entry.name
    if ((Get-Item -LiteralPath $path).Length -ne $entry.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Checksum mismatch: $($entry.name)" }
    if ([IO.Path]::GetExtension($path) -eq '.exe') {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if (($RequireSignature -or $manifest.signed) -and $signature.Status -ne 'Valid') { throw "Untrusted signature: $($entry.name)" }
        if ($ExpectedSignerThumbprint -and $signature.SignerCertificate.Thumbprint -ne $ExpectedSignerThumbprint) { throw 'Unexpected signing certificate.' }
        Write-Output "$($entry.name): SHA256 OK; Authenticode $($signature.Status)"
    } else { Write-Output "$($entry.name): SHA256 OK" }
}
if (!$manifest.signed) { Write-Warning 'This is an unsigned local release. Checksums detect corruption; they do not authenticate its publisher.' }
