[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $repository 'artifacts\tools'
$compilerRoot = Join-Path $toolRoot 'InnoSetup643'
$setup = Join-Path $toolRoot 'innosetup-6.4.3.exe'
if (Test-Path -LiteralPath (Join-Path $compilerRoot 'ISCC.exe')) { Write-Output (Join-Path $compilerRoot 'ISCC.exe'); return }
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_4_3/innosetup-6.4.3.exe' -OutFile $setup
if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne 'F3C42116542C4CC57263C5BA6C4FEABFC49FE771F2F98A79D2F7628B8762723B') { throw 'Installer compiler checksum mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $setup
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') { throw 'Installer compiler publisher cannot be verified.' }
$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/CURRENTUSER','/NOICONS',('/DIR="' + $compilerRoot + '"'))
$installation = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($installation.ExitCode -ne 0) { throw "Installer compiler setup failed: $($installation.ExitCode)" }
Write-Output (Join-Path $compilerRoot 'ISCC.exe')
