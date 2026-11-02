[CmdletBinding()]
param(
    [string]$Compiler = '',
    [string]$CertificateThumbprint = '',
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [switch]$RequireSignature,
    [switch]$TestPackage
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'src\Vela\Vela.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup.Version
$staging = Join-Path $repository ( 'artifacts\package-' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $repository 'artifacts\releases'
New-Item -ItemType Directory -Path $staging,$output -Force | Out-Null
if ($RequireSignature -and !$CertificateThumbprint) { throw 'A trusted code-signing certificate is required.' }
if (!$Compiler) {
    $candidates = @((Join-Path $repository 'artifacts\tools\InnoSetup643\ISCC.exe'), "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")
    $Compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$Compiler -or !(Test-Path -LiteralPath $Compiler)) { throw 'Install Inno Setup 6.4.3+ and pass -Compiler path\ISCC.exe.' }
$certificate = $null
if ($CertificateThumbprint) {
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
    if (!$certificate.HasPrivateKey -or $certificate.NotAfter -lt (Get-Date)) { throw 'The signing certificate has no private key or has expired.' }
}
function Sign-ReleaseFile([string]$Path) {
    if (!$certificate) { return }
    $signed = Set-AuthenticodeSignature -LiteralPath $Path -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer
    if ($signed.Status -ne 'Valid') { throw "Signature failed: $($signed.StatusMessage)" }
}
dotnet publish $project -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $staging --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Sign-ReleaseFile (Join-Path $staging 'Vela.exe')
$productId = if ($TestPackage) { 'VelaBrowser.PackageTest' } else { 'VelaBrowser' }
& $Compiler "/DPublishDir=$staging" "/DReleaseDir=$output" "/DAppVersion=$version" "/DProductId=$productId" (Join-Path $repository 'installer\Vela.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $output "Vela-$version-win-x64-setup.exe"
Sign-ReleaseFile $setup
$archive = Join-Path $output "Vela-$version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -Force
$files = @($setup,$archive) | ForEach-Object {
    [ordered]@{ name = [IO.Path]::GetFileName($_); size = (Get-Item -LiteralPath $_).Length; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[ordered]@{ version = $version; architecture = 'win-x64'; signed = [bool]$certificate; testPackage = [bool]$TestPackage; files = @($files) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'release-manifest.json') -Encoding utf8NoBOM
Write-Output "Release created in $output. Authenticode signed: $([bool]$certificate)"
