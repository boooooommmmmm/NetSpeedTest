param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    if (-not $SkipTests) {
        & dotnet run --project tests/NetPulse.Tests/NetPulse.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Core verification failed.' }
    }
    & dotnet publish src/NetPulse/NetPulse.csproj -c Release -o dist
    if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }
    $files = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'dist') -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'NetPulse.exe') {
        throw 'The dist directory should contain only NetPulse.exe. Check for old files.'
    }
    Write-Output ('Ready: {0} ({1:N1} MiB)' -f $files[0].FullName, ($files[0].Length / 1MB))
} finally {
    Pop-Location
}
