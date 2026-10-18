param([string]$Version = '', [string]$NotesFile = '', [string]$TokenFile = '', [switch]$Publish)

$ErrorActionPreference = 'Stop'
$repository = 'boooooommmmmm/NetSpeedTest'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    function Assert-CleanReleaseSource {
        $status = @(git status --porcelain --untracked-files=all)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect Git source state.' }
        foreach ($line in $status) {
            if ($line.Substring(3).Replace('\', '/') -ne 'release/latest.json') { throw ('Commit all source changes before releasing: ' + $line) }
        }
        $head = (git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'A committed source revision is required.' }
        return $head
    }
    $sourceCommit = Assert-CleanReleaseSource
    [xml]$project = Get-Content -LiteralPath 'src/NetPulse/NetPulse.csproj' -Raw
    $projectVersion = [string]$project.Project.PropertyGroup.Version
    if (-not $Version) { $Version = $projectVersion }
    if ($Version -ne $projectVersion -or $Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Release version must match the project version.' }
    if (-not $NotesFile) { $NotesFile = "docs/releases/v$Version.md" }
    $notes = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile).Path)
    & (Join-Path $projectRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Build or tests failed.' }
    if ((Assert-CleanReleaseSource) -ne $sourceCommit) { throw 'Source revision changed during the build.' }
    $exePath = (Resolve-Path -LiteralPath 'dist/NetPulse.exe').Path
    if ([version](Get-Item -LiteralPath $exePath).VersionInfo.FileVersion -ne [version]$Version) { throw 'Published EXE version mismatch.' }
    $packageRoot = Join-Path $projectRoot "artifacts/release/v$Version"
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    $hash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $assetUrl = "https://github.com/$repository/releases/download/v$Version/NetPulse.exe"
    $releaseUrl = "https://github.com/$repository/releases/tag/v$Version"
    $manifest = [ordered]@{ Version=$Version; DownloadUrl=$assetUrl; ReleaseUrl=$releaseUrl; Size=(Get-Item -LiteralPath $exePath).Length; Sha256=$hash; Notes=$notes; PublishedAt=[DateTimeOffset]::UtcNow.ToString('O'); SourceCommit=$sourceCommit }
    $manifestPath = Join-Path $packageRoot 'latest.json'
    $checksumPath = Join-Path $packageRoot 'SHA256SUMS.txt'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($checksumPath, "$hash *NetPulse.exe`n", [Text.UTF8Encoding]::new($false))
    if (-not $Publish) { Write-Output "Prepared $exePath and $packageRoot. Use -Publish to publish the verified release."; return }

    git fetch --quiet origin main
    if ($LASTEXITCODE -ne 0 -or (git rev-parse origin/main).Trim() -ne $sourceCommit) { throw 'Push the exact source commit to origin/main before publishing.' }
    . (Join-Path $PSScriptRoot 'GitHub.ps1')
    $headers = Get-NetPulseGitHubHeaders -TokenFile $TokenFile
    $api = "https://api.github.com/repos/$repository"
    $release = $null
    try { $release = Invoke-RestMethod -Uri "$api/releases/tags/v$Version" -Headers $headers }
    catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
    if ($release -and -not $release.draft) { throw "v$Version is already public. Published releases are not overwritten." }
    if (-not $release) {
        $payload = @{ tag_name="v$Version"; target_commitish=$sourceCommit; name="NetPulse v$Version"; body=$notes; draft=$true; prerelease=$false } | ConvertTo-Json
        $release = Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload))
    }
    $uploadBase = $release.upload_url -replace '\{.*$', ''
    foreach ($asset in @(@{Name='NetPulse.exe';Path=$exePath;Type='application/octet-stream'}, @{Name='latest.json';Path=$manifestPath;Type='application/json'}, @{Name='SHA256SUMS.txt';Path=$checksumPath;Type='text/plain'})) {
        $expectedHash = (Get-FileHash -LiteralPath $asset.Path -Algorithm SHA256).Hash.ToLowerInvariant()
        $existing = @($release.assets | Where-Object { $_.name -eq $asset.Name })
        if ($existing.Count -gt 0) { $uploaded = $existing[0] }
        else {
            Write-Output ("Uploading " + $asset.Name)
            $uploaded = Invoke-RestMethod -Method Post -Uri ($uploadBase + '?name=' + [Uri]::EscapeDataString($asset.Name)) -Headers $headers -ContentType $asset.Type -InFile $asset.Path -TimeoutSec 300
        }
        if ($uploaded.size -ne (Get-Item -LiteralPath $asset.Path).Length -or $uploaded.digest -ne "sha256:$expectedHash") { throw ('GitHub asset size/digest mismatch: ' + $asset.Name) }
    }
    $publishBody = @{ tag_name="v$Version"; target_commitish=$sourceCommit; draft=$false; prerelease=$false; make_latest='true' } | ConvertTo-Json
    $published = Invoke-RestMethod -Method Patch -Uri "$api/releases/$($release.id)" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($publishBody))
    if ($published.html_url -ne $releaseUrl -or $published.draft -or $published.prerelease) { throw 'Published release identity mismatch.' }
    $publicRoot = Join-Path $packageRoot 'public-download'
    New-Item -ItemType Directory -Path $publicRoot -Force | Out-Null
    foreach ($name in @('NetPulse.exe','latest.json','SHA256SUMS.txt')) {
        $publicPath = Join-Path $publicRoot $name
        $url = "https://github.com/$repository/releases/download/v$Version/$name"
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            try { Invoke-WebRequest -Uri $url -OutFile $publicPath -Headers @{'User-Agent'='NetPulse-release-verification'} -TimeoutSec 300; break }
            catch { if ($attempt -eq 5) { throw }; Start-Sleep -Seconds 2 }
        }
        $original = if ($name -eq 'NetPulse.exe') { $exePath } else { Join-Path $packageRoot $name }
        if ((Get-FileHash -LiteralPath $publicPath).Hash -ne (Get-FileHash -LiteralPath $original).Hash) { throw ('Anonymous public download verification failed: ' + $name) }
    }
    git fetch --quiet origin tag "v$Version"
    if ($LASTEXITCODE -ne 0 -or (git rev-list -n 1 "v$Version").Trim() -ne $sourceCommit) { throw 'Release tag does not point to the build commit.' }
    if ((Assert-CleanReleaseSource) -ne $sourceCommit) { throw 'Source revision changed while publishing.' }
    New-Item -ItemType Directory -Path (Join-Path $projectRoot 'release') -Force | Out-Null
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $projectRoot 'release/latest.json') -Force
    [ordered]@{ ReleaseUrl=$releaseUrl; SourceCommit=$sourceCommit; Version=$Version; Size=$manifest.Size; Sha256=$hash; PublicAssetsVerified=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'publication.json') -Encoding utf8
    Write-Output "Published and anonymously verified: $releaseUrl"
    Write-Output 'Commit and push release/latest.json, then verify the raw GitHub manifest endpoint.'
}
finally { Pop-Location }
