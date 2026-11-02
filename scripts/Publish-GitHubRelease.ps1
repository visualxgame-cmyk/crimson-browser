[CmdletBinding()]
param(
    [string]$Repository = 'visualxstudio/vela-browser',
    [string]$Directory = '',
    [Parameter(Mandatory)][string]$NotesFile,
    [switch]$InitializeRepository,
    [switch]$AllowUnsigned,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$Directory) { $Directory = Join-Path $projectRoot 'artifacts\releases' }
if ($Repository -notmatch '^[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*$') { throw 'Invalid GitHub repository.' }
& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -Directory $Directory
$releaseRoot = (Resolve-Path -LiteralPath $Directory).Path
$manifestPath = Join-Path $releaseRoot 'release-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.testPackage) { throw 'Test packages cannot be published.' }
if (!$manifest.signed -and !$AllowUnsigned) { throw 'Unsigned releases require -AllowUnsigned.' }
if ($manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
$notes = Get-Content -LiteralPath $NotesFile -Raw
if ([string]::IsNullOrWhiteSpace($notes)) { throw 'Release notes are required.' }
$assetPaths = @($manifest.files | ForEach-Object { Join-Path $releaseRoot $_.name }) + @($manifestPath)
$tag = 'v' + $manifest.version
$owner, $repositoryName = $Repository.Split('/')
$priorPrompt = $env:GIT_TERMINAL_PROMPT; $priorInteractive = $env:GCM_INTERACTIVE
$headers = @{}; $credentialValues = @{}; $http = $null
try {
    # Git Credential Manager supplies an existing credential. Never put a token in files, command arguments or output.
    $env:GIT_TERMINAL_PROMPT = '0'; $env:GCM_INTERACTIVE = 'never'
    $credentialLines = "protocol=https`nhost=github.com`nusername=$owner`n`n" | git credential fill
    if ($LASTEXITCODE -ne 0) { throw 'No existing GitHub credential is available in Git Credential Manager.' }
    foreach ($line in $credentialLines) { $separator = $line.IndexOf('='); if ($separator -gt 0) { $credentialValues[$line.Substring(0, $separator)] = $line.Substring($separator + 1) } }
    if (!$credentialValues['password']) { throw 'GitHub authentication is unavailable.' }
    $headers = @{ Authorization = ('Bearer ' + $credentialValues['password']); 'User-Agent' = 'Vela-Release-Publisher'; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2026-03-10' }
    function Invoke-GitHub([string]$Method, [string]$Path, $Body = $null) {
        $arguments = @{ Method = $Method; Uri = ('https://api.github.com/' + $Path); Headers = $headers; TimeoutSec = 60 }
        if ($null -ne $Body) { $arguments.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 10)); $arguments.ContentType = 'application/json; charset=utf-8' }
        Invoke-RestMethod @arguments
    }
    function Get-GitHubOptional([string]$Path) {
        try { Invoke-GitHub 'GET' $Path } catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
    }
    $identity = Invoke-GitHub 'GET' 'user'
    if ($identity.login -ne $owner) { throw 'The authenticated account must own this distribution repository.' }
    $repositoryInfo = Get-GitHubOptional "repos/$Repository"
    if (!$repositoryInfo) {
        if (!$InitializeRepository) { throw 'Repository does not exist. Use -InitializeRepository to create the public distribution repository.' }
        $readmePath = Join-Path $projectRoot 'docs\DISTRIBUTION.md'
        $readme = [Convert]::ToBase64String([IO.File]::ReadAllBytes($readmePath))
        $repositoryInfo = Invoke-GitHub 'POST' 'user/repos' @{ name = $repositoryName; description = 'Vela Browser — Windows downloads and releases'; private = $false; auto_init = $false; has_issues = $false; has_projects = $false; has_wiki = $false }
        $null = Invoke-GitHub 'PUT' "repos/$Repository/contents/README.md" @{ message = 'Add Vela distribution information'; content = $readme }
        Write-Output "Distribution repository created: $($repositoryInfo.html_url)"
    }
    if ($repositoryInfo.private) { throw 'The update checker requires a public release repository.' }
    $release = Get-GitHubOptional "repos/$Repository/releases/tags/$tag"
    if (!$release) {
        # A draft has no published Git tag yet and is not returned by the tag endpoint.
        $drafts = @((Invoke-GitHub 'GET' "repos/$Repository/releases?per_page=100") | Where-Object { $_.tag_name -eq $tag -and $_.draft })
        if ($drafts.Count -gt 1) { throw 'Multiple drafts have this tag. Resolve them before publishing.' }
        if ($drafts.Count -eq 1) { $release = $drafts[0] }
    }
    if (!$release) {
        $release = Invoke-GitHub 'POST' "repos/$Repository/releases" @{ tag_name = $tag; name = "Vela $($manifest.version)"; body = $notes; draft = $true; prerelease = $false }
    }
    $handler = [Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect = $false
    $http = [Net.Http.HttpClient]::new($handler); $http.Timeout = [TimeSpan]::FromMinutes(10)
    $http.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $credentialValues['password'])
    $http.DefaultRequestHeaders.UserAgent.ParseAdd('Vela-Release-Publisher')
    $http.DefaultRequestHeaders.Add('X-GitHub-Api-Version', '2026-03-10')
    foreach ($path in $assetPaths) {
        $name = [IO.Path]::GetFileName($path); $size = (Get-Item -LiteralPath $path).Length
        $digest = 'sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $existing = @($release.assets | Where-Object name -EQ $name)
        if ($existing.Count -gt 0) {
            if ($existing.Count -ne 1 -or $existing[0].size -ne $size -or $existing[0].digest -ne $digest) { throw "Existing release asset differs: $name. Publish a new version; assets are not overwritten." }
            Write-Output "Already verified: $name"; continue
        }
        if (!$release.draft) { throw 'Cannot add missing files to an already published release.' }
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "https://uploads.github.com/repos/$Repository/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($name))")
        $response = $null
        try {
            $request.Content = [Net.Http.StreamContent]::new([IO.File]::OpenRead($path))
            $request.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
            $response = $http.SendAsync($request).GetAwaiter().GetResult()
            if (!$response.IsSuccessStatusCode) { throw "GitHub upload failed for $name (HTTP $([int]$response.StatusCode))." }
            $asset = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
            if ($asset.size -ne $size -or $asset.digest -ne $digest -or $asset.state -ne 'uploaded') { throw "GitHub integrity validation failed: $name" }
            Write-Output "Uploaded and verified: $name"
        } finally { if ($response) { $response.Dispose() }; $request.Dispose() }
    }
    if ($Publish -and $release.draft) {
        $release = Invoke-GitHub 'PATCH' "repos/$Repository/releases/$($release.id)" @{ draft = $false; make_latest = 'true' }
    }
    Write-Output "Release: $($release.html_url); draft=$($release.draft)"
} finally {
    if ($http) { $http.Dispose() }
    $headers.Clear(); $credentialValues.Clear(); $credentialLines = $null
    $env:GIT_TERMINAL_PROMPT = $priorPrompt; $env:GCM_INTERACTIVE = $priorInteractive
}
