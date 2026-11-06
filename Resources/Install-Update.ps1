param(
    [Parameter(Mandatory=$true)][int]$BrowserProcessId,
    [Parameter(Mandatory=$true)][string]$InstallerPath,
    [Parameter(Mandatory=$true)][string]$InstallDirectory
)

$ErrorActionPreference = 'Stop'
$updateLog = Join-Path (Split-Path -Parent $InstallerPath) 'update-install.log'
try {
    # Wait for the browser to save its session and release its binaries before setup starts.
    $browserProcess = Get-Process -Id $BrowserProcessId -ErrorAction SilentlyContinue
    if ($browserProcess) {
        $browserProcess | Wait-Process -Timeout 60 -ErrorAction Stop
    }
    $arguments = '/SILENT /SUPPRESSMSGBOXES /NORESTART /CRIMSONRESTART /DIR="' + $InstallDirectory + '" /LOG="' + $updateLog + '"'
    $setup = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -PassThru -Wait
    if ($setup.ExitCode -ne 0) { throw "Setup failed with exit code $($setup.ExitCode). See $updateLog" }
}
catch {
    $_ | Out-String | Add-Content -LiteralPath ($updateLog + '.error.txt')
    # If setup could not run, reopen the existing installation so the user isn't stranded.
    $browserPath = Join-Path $InstallDirectory 'CrimsonBrowser.exe'
    if (Test-Path -LiteralPath $browserPath) { Start-Process -FilePath $browserPath }
    exit 1
}
