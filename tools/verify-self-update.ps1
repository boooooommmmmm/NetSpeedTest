param([switch]$SkipBuild, [string]$PreviousExe = '')

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run with PowerShell 7.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $projectRoot 'artifacts/self-update'
$oldDirectory = Join-Path $artifactRoot 'fixtures/old'
$project = Join-Path $projectRoot 'src/NetPulse/NetPulse.csproj'
[xml]$definition = Get-Content -LiteralPath $project -Raw
$newVersion = [string]$definition.Project.PropertyGroup.Version
$oldVersion = '1.4.99'
$oldExe = Join-Path $oldDirectory 'NetPulse.exe'
$modes = @('success', 'auto-idle', 'startup-failure', 'rollback-state-readonly', 'corrupt-package', 'readonly-target', 'competing-update', 'helper-start-failure')
if ($PreviousExe) {
    $oldExe = (Resolve-Path -LiteralPath $PreviousExe).Path
    $oldVersion = ([version](Get-Item -LiteralPath $oldExe).VersionInfo.FileVersion).ToString(3)
    # Published v1.5 clients support these three protocol compatibility scenarios.
    $modes = @('success', 'auto-idle', 'startup-failure')
}
if (-not $SkipBuild) {
    if (-not $PreviousExe) {
        & dotnet publish $project -c Release "-p:Version=$oldVersion" -o $oldDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Old-version fixture build failed.' }
    }
    & dotnet publish $project -c Release -o (Join-Path $projectRoot 'dist')
    if ($LASTEXITCODE -ne 0) { throw 'Current-version build failed.' }
}
$package = Join-Path $projectRoot 'dist/NetPulse.exe'
if (([version]$oldVersion) -ge ([version]$newVersion)) { throw 'The previous executable must be older than the new package.' }
if (([version](Get-Item -LiteralPath $package).VersionInfo.FileVersion).ToString(3) -ne $newVersion) { throw 'The package version does not match the project.' }
$oldHash = (Get-FileHash -LiteralPath $oldExe).Hash
$newHash = (Get-FileHash -LiteralPath $package).Hash
if ($oldHash -eq $newHash) { throw 'Fixtures must be different real executable versions.' }
$runRoot = Join-Path $artifactRoot ('run-' + [Guid]::NewGuid().ToString('N'))
$results = @()
foreach ($mode in $modes) {
    # Literal shell metacharacters, Unicode, spaces and a renamed executable exercise ArgumentList.
    $caseDirectory = Join-Path $runRoot ($mode + ' 中文 空格 & $()')
    $null = New-Item -ItemType Directory -Path $caseDirectory -Force
    $target = Join-Path $caseDirectory '我的 NetPulse & Test.exe'
    Copy-Item -LiteralPath $oldExe -Destination $target
    $start = [Diagnostics.ProcessStartInfo]::new($target)
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $caseDirectory
    foreach ($argument in @('--self-update-test', $caseDirectory, $package, $mode)) { $start.ArgumentList.Add($argument) }
    $parent = [Diagnostics.Process]::Start($start)
    try {
        $proofPath = Join-Path $caseDirectory 'proof.json'
        $errorPath = Join-Path $caseDirectory 'test-error.txt'
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(75)
        while (-not (Test-Path -LiteralPath $proofPath)) {
            if (Test-Path -LiteralPath $errorPath) { throw ([IO.File]::ReadAllText($errorPath)) }
            if ([DateTimeOffset]::UtcNow -gt $deadline) { throw "Timed out waiting for real updater: $caseDirectory" }
            Start-Sleep -Milliseconds 200
        }
        $proof = Get-Content -LiteralPath $proofPath -Raw | ConvertFrom-Json
        $expectedStatus = switch ($mode) { 'success' { 'Succeeded' } 'auto-idle' { 'Succeeded' } 'startup-failure' { 'RolledBack' } 'rollback-state-readonly' { 'RolledBack' } default { 'RejectedBeforeExit' } }
        $expectedVersion = if ($mode -in @('success', 'auto-idle')) { $newVersion } else { $oldVersion }
        if ($proof.Status -ne $expectedStatus -or $proof.Version -ne $expectedVersion -or $proof.Path -ne $target -or -not $proof.SettingsPreserved -or -not $proof.PreferencesPreserved) {
            throw ('Restart path/version/preferences mismatch: ' + ($proof | ConvertTo-Json -Compress))
        }
        if ($expectedStatus -eq 'RolledBack') {
            $cooldown = $proof.RollbackCooldown
            if ($PreviousExe) {
                $savedState = Get-Content -LiteralPath (Join-Path $caseDirectory 'appdata/updates.json') -Raw | ConvertFrom-Json
                $cooldown = $savedState.RetryAfter -and [DateTimeOffset]$savedState.RetryAfter -gt [DateTimeOffset]::UtcNow
            }
            if (-not $cooldown) { throw 'Rollback retry cooldown was not applied.' }
        }
        if ($mode -eq 'rollback-state-readonly' -and $proof.UpdateStatus -notmatch '未能保存') { throw 'Read-only preferences were not exercised.' }
        if ($expectedStatus -in @('Succeeded', 'RolledBack')) {
            if ($mode -ne 'auto-idle' -and -not $proof.RecordsBackedUp) { throw 'Session CSV backup was not retained.' }
            $resultPath = Join-Path $caseDirectory 'appdata/updates/last-result.json'
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
            do {
                $result = if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json } else { $null }
                if ($result.Status -eq $expectedStatus) { break }
                if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Updater final result was not persisted.' }
                Start-Sleep -Milliseconds 100
            } while ($true)
        }
        $expectedHash = if ($mode -in @('success', 'auto-idle')) { $newHash } else { $oldHash }
        if ((Get-FileHash -LiteralPath $target).Hash -ne $expectedHash) { throw 'Destination EXE bytes are incorrect.' }
        if (-not $parent.WaitForExit(10000)) { throw 'Old process did not exit.' }
        $results += [ordered]@{ Mode=$mode; Status=$proof.Status; Version=$proof.Version; SamePath=$true; HashVerified=$true; SettingsPreserved=$proof.SettingsPreserved; RecordsBackedUp=$proof.RecordsBackedUp; Directory=$caseDirectory }
        Write-Output ("PASS real self-update: $mode -> $expectedStatus ($expectedVersion)")
    } finally { $parent.Dispose() }
}
$reportPath = Join-Path $runRoot 'verification.json'
[ordered]@{ OldVersion=$oldVersion; NewVersion=$newVersion; PreviousExecutable=$oldExe; Cases=$results; Success=$true } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Output ('Verification: ' + $reportPath)
