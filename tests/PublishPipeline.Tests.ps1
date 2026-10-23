#requires -Version 7.2
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$publishSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/Publish.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('TalosDesk-PublishTests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$checks = 0

try {
    foreach ($failureStage in @('', 'restore', 'build', 'test', 'publish')) {
        $caseName = if ($failureStage) { "$failureStage-failure" } else { 'success' }
        $caseRoot = Join-Path $testRoot "$caseName with spaces"
        foreach ($directory in @('scripts', 'eng', 'packaging', '.tools/dotnet/fixture-sdk')) {
            New-Item -ItemType Directory -Path (Join-Path $caseRoot $directory) -Force | Out-Null
        }
        Copy-Item -LiteralPath $publishSource -Destination (Join-Path $caseRoot 'scripts/Publish.ps1')
        Set-Content -LiteralPath (Join-Path $caseRoot 'Directory.Build.props') -Value '<Project><PropertyGroup><Version>0.0.0-fixture.1</Version></PropertyGroup></Project>'
        Set-Content -LiteralPath (Join-Path $caseRoot 'eng/dotnet-sdk.json') -Value '{"version":"fixture-sdk"}'
        Set-Content -LiteralPath (Join-Path $caseRoot 'failure-stage.txt') -Value $failureStage -NoNewline
        foreach ($file in @('packaging/README.txt', 'LICENSE', 'THIRD-PARTY-NOTICES.md', '.tools/dotnet/fixture-sdk/LICENSE.txt', '.tools/dotnet/fixture-sdk/ThirdPartyNotices.txt')) {
            Set-Content -LiteralPath (Join-Path $caseRoot $file) -Value 'Synthetic fixture; no application bytes.'
        }
        # This SDK substitute records calls and creates a synthetic publish output.
        # It never invokes dotnet, a build, a test host or an application.
        $stub = @'
$fixtureRoot = Split-Path -Parent $PSScriptRoot
ConvertTo-Json -InputObject @($args) -Compress | Add-Content -LiteralPath (Join-Path $fixtureRoot 'calls.jsonl')
if ($args[0] -eq 'test') {
    $resultsIndex = [Array]::IndexOf($args, '--results-directory')
    if ($resultsIndex -ge 0) {
        $resultDirectory = $args[$resultsIndex + 1]
        New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $resultDirectory 'synthetic.trx') -Value '<TestRun />'
    }
}
if ((Get-Content -Raw -LiteralPath (Join-Path $fixtureRoot 'failure-stage.txt')) -eq $args[0]) { exit 19 }
if ($args[0] -eq 'publish') {
    $outputIndex = [Array]::IndexOf($args, '--output')
    $outputDirectory = $args[$outputIndex + 1]
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $outputDirectory 'synthetic-output.txt') -Value 'Not executable.'
}
exit 0
'@
        Set-Content -LiteralPath (Join-Path $caseRoot 'scripts/With-Sdk.ps1') -Value $stub
        $errorMessage = $null
        try { & (Join-Path $caseRoot 'scripts/Publish.ps1') | Out-Null } catch { $errorMessage = $_.Exception.Message }
        $calls = @(Get-Content -LiteralPath (Join-Path $caseRoot 'calls.jsonl') | ForEach-Object { ,(ConvertFrom-Json $_) })
        $expectedStages = @('restore', 'build', 'test', 'publish')
        $expectedCount = if ($failureStage) { [Array]::IndexOf($expectedStages, $failureStage) + 1 } else { 4 }
        if ($calls.Count -ne $expectedCount) { throw "Unexpected number of SDK calls: $caseName" }
        for ($index = 0; $index -lt $expectedCount; $index++) {
            if ($calls[$index][0] -ne $expectedStages[$index]) { throw "Unexpected SDK call order: $caseName" }
        }
        $archivePath = Join-Path $caseRoot 'artifacts/TalosDesk-v0.0.0-fixture.1-win-x64.zip'
        if ($failureStage) {
            if ($errorMessage -notlike '*exit code 19*' -or (Test-Path -LiteralPath $archivePath)) { throw "Failure did not prevent a package: $caseName" }
        } else {
            if ($errorMessage) { throw $errorMessage }
            if (-not (Test-Path -LiteralPath $archivePath)) { throw 'Missing synthetic package.' }
            $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
            $checksum = Get-Content -Raw -LiteralPath ([IO.Path]::ChangeExtension($archivePath, '.sha256'))
            if ($checksum.Trim() -ne "$hash  $([IO.Path]::GetFileName($archivePath))") { throw 'Synthetic package checksum mismatch.' }
            $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
            try {
                foreach ($name in @('README.txt', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'DOTNET-LICENSE.txt', 'DOTNET-THIRD-PARTY-NOTICES.txt')) {
                    if (-not $archive.GetEntry($name)) { throw "Missing packaged notice: $name" }
                }
            } finally { $archive.Dispose() }
        }
        if ($expectedCount -ge 3) {
            $testCall = $calls[2]
            $loggerIndex = [Array]::IndexOf($testCall, '--logger')
            $resultsIndex = [Array]::IndexOf($testCall, '--results-directory')
            if ($loggerIndex -lt 0 -or $testCall[$loggerIndex + 1] -ne 'trx' -or $resultsIndex -lt 0 -or $testCall[$resultsIndex + 1] -ne (Join-Path $caseRoot 'artifacts/tests')) { throw 'Test evidence arguments missing or malformed.' }
            if (-not (Test-Path -LiteralPath (Join-Path $caseRoot 'artifacts/tests/synthetic.trx'))) { throw 'Missing retained test evidence.' }
        }
        $checks++
        Write-Output "PASS: $caseName"
    }
    Write-Output "Passed $checks publish pipeline checks with synthetic SDK commands; no real build or app launched."
    # Expected stub failures must not become the GitHub pwsh step's exit code.
    $global:LASTEXITCODE = 0
} finally {
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $testFullPath = [IO.Path]::GetFullPath($testRoot)
    if (-not $testFullPath.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($testFullPath) -notlike 'TalosDesk-PublishTests-*') { throw 'Refusing to remove an unexpected test path.' }
    Remove-Item -LiteralPath $testFullPath -Recurse -Force
}
