function Get-NetPulseGitHubHeaders {
    param([string]$TokenFile = '')
    $releaseToken = $env:GH_TOKEN
    if ([string]::IsNullOrWhiteSpace($releaseToken)) { $releaseToken = $env:GITHUB_TOKEN }
    if ([string]::IsNullOrWhiteSpace($releaseToken) -and $TokenFile) {
        $releaseToken = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $TokenFile).Path).Trim()
    }
    if ([string]::IsNullOrWhiteSpace($releaseToken)) {
        $env:GIT_TERMINAL_PROMPT = '0'
        $credentialLines = "protocol=https`nhost=github.com`n`n" | git -c credential.interactive=never credential fill 2>$null
        if ($LASTEXITCODE -eq 0) {
            foreach ($line in $credentialLines) { if ($line.StartsWith('password=')) { $releaseToken = $line.Substring(9) } }
        }
    }
    if ([string]::IsNullOrWhiteSpace($releaseToken)) { throw 'No GitHub credential. Configure Git credentials, GH_TOKEN, or an external -TokenFile with repository Contents: write permission.' }
    return @{ Authorization = "Bearer $releaseToken"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'NetPulse-release'; 'X-GitHub-Api-Version' = '2022-11-28' }
}
