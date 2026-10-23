#requires -Version 7.2
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$publishSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/Publish.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('TalosDesk-PublishTests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$checks = 0
$artifactLocks = [Collections.Generic.List[IO.FileStream]]::new()

function Hold-TestFile([string]$Path) {
    $artifactLocks.Add([IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
}

function Compress-Archive {
    param([string]$Path, [string]$DestinationPath, [string]$CompressionLevel)
    if ($compressShouldFail) { throw 'Injected compression failure.' }
    Microsoft.PowerShell.Archive\Compress-Archive @PSBoundParameters
}

try {
    foreach ($failureStage in @('', 'restore', 'build', 'test', 'publish', 'notices', 'compress', 'commit', 'locked', 'reparse', 'recovery', 'fresh')) {
        $caseName = if ($failureStage -eq 'fresh') { 'fresh-success' } elseif ($failureStage) { "$failureStage-failure" } else { 'replacement-success' }
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
        $oldPublish = Join-Path $caseRoot 'artifacts/publish/TalosDesk-v0.0.0-fixture.1-win-x64'
        $oldArchive = Join-Path $caseRoot 'artifacts/TalosDesk-v0.0.0-fixture.1-win-x64.zip'
        $oldChecksum = [IO.Path]::ChangeExtension($oldArchive, '.sha256')
        if ($failureStage -ne 'fresh') {
            New-Item -ItemType Directory -Path $oldPublish -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $oldPublish 'old-output.txt') -Value 'Previous package output.'
            Set-Content -LiteralPath $oldArchive -Value 'Previous ZIP bytes.'
            Set-Content -LiteralPath $oldChecksum -Value 'Previous checksum bytes.'
            $previousArchiveHash = (Get-FileHash -LiteralPath $oldArchive).Hash
            $previousChecksumHash = (Get-FileHash -LiteralPath $oldChecksum).Hash
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
    $scenario = Get-Content -Raw -LiteralPath (Join-Path $fixtureRoot 'failure-stage.txt')
    if ($scenario -eq 'notices') { Remove-Item -LiteralPath (Join-Path $fixtureRoot '.tools/dotnet/fixture-sdk/LICENSE.txt') }
    if ($scenario -eq 'commit') { Hold-TestFile (Join-Path $fixtureRoot 'artifacts/TalosDesk-v0.0.0-fixture.1-win-x64.sha256') }
}
exit 0
'@
        Set-Content -LiteralPath (Join-Path $caseRoot 'scripts/With-Sdk.ps1') -Value $stub
        $errorMessage = $null
        $compressShouldFail = $failureStage -eq 'compress'
        if ($failureStage -eq 'locked') {
            $lockPath = Join-Path $caseRoot 'artifacts/.publish.lock'
            Set-Content -LiteralPath $lockPath -Value 'Existing lock owner.'
            Hold-TestFile $lockPath
        }
        if ($failureStage -eq 'reparse') {
            $publishRoot = Join-Path $caseRoot 'artifacts/publish'
            $externalPublish = Join-Path $caseRoot 'outside-artifacts'
            $casePrefix = [IO.Path]::GetFullPath($caseRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            foreach ($path in @($publishRoot, $externalPublish)) {
                if (-not [IO.Path]::GetFullPath($path).StartsWith($casePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test move path.' }
            }
            [IO.Directory]::Move($publishRoot, $externalPublish)
            New-Item -ItemType Junction -Path $publishRoot -Target $externalPublish | Out-Null
        }
        $originalPermissions = $null
        if ($failureStage -eq 'recovery') {
            # Deny adding a directory only at this fixture's publish parent.
            # Moving the old directory out succeeds; installing or restoring one fails.
            $permissionPath = Split-Path -Parent $oldPublish
            $permissions = Get-Acl -LiteralPath $permissionPath
            $originalPermissions = $permissions.Sddl
            $deny = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.AccessControl.FileSystemRights]::CreateDirectories, [Security.AccessControl.AccessControlType]::Deny)
            $permissions.AddAccessRule($deny)
            Set-Acl -LiteralPath $permissionPath -AclObject $permissions
        }
        try {
            try { & (Join-Path $caseRoot 'scripts/Publish.ps1') | Out-Null } catch { $errorMessage = $_.Exception.Message }
        } finally {
            foreach ($handle in $artifactLocks) { $handle.Dispose() }
            $artifactLocks.Clear()
            if ($originalPermissions) {
                $permissions = Get-Acl -LiteralPath $permissionPath
                $permissions.SetSecurityDescriptorSddlForm($originalPermissions)
                Set-Acl -LiteralPath $permissionPath -AclObject $permissions
            }
        }
        $calls = @(if (Test-Path -LiteralPath (Join-Path $caseRoot 'calls.jsonl')) { Get-Content -LiteralPath (Join-Path $caseRoot 'calls.jsonl') | ForEach-Object { ,(ConvertFrom-Json $_) } })
        $expectedStages = @('restore', 'build', 'test', 'publish')
        $expectedCount = if ($failureStage -in $expectedStages) { [Array]::IndexOf($expectedStages, $failureStage) + 1 } elseif ($failureStage -in @('locked', 'reparse')) { 0 } else { 4 }
        if ($calls.Count -ne $expectedCount) { throw "Unexpected number of SDK calls: $caseName" }
        for ($index = 0; $index -lt $expectedCount; $index++) {
            if ($calls[$index][0] -ne $expectedStages[$index]) { throw "Unexpected SDK call order: $caseName" }
        }
        $archivePath = Join-Path $caseRoot 'artifacts/TalosDesk-v0.0.0-fixture.1-win-x64.zip'
        if ($failureStage -and $failureStage -ne 'fresh') {
            $expectedError = switch ($failureStage) {
                'notices' { '*SDK license*missing*' }
                'compress' { '*Injected compression failure*' }
                'commit' { '*replacement failed; previous artifacts restored*' }
                'locked' { '*Cannot acquire the publish lock*' }
                'reparse' { '*through a reparse point*' }
                'recovery' { '*replacement and recovery failed*manual recovery*' }
                default { '*exit code 19*' }
            }
            if ($errorMessage -notlike $expectedError) { throw "Wrong build failure: ${caseName}: $errorMessage" }
            if ($failureStage -eq 'recovery') {
                $staging = @(Get-ChildItem -LiteralPath (Join-Path $caseRoot 'artifacts') -Directory -Force -Filter '.publish-staging-*')
                if ($staging.Count -ne 1 -or -not $errorMessage.Contains($staging[0].FullName)) { throw 'Recovery directory was not retained and reported.' }
                $backupPublish = Join-Path $staging[0].FullName 'previous/publish'
                if (-not (Test-Path -LiteralPath (Join-Path $backupPublish 'old-output.txt'))) { throw 'Old publish backup was lost.' }
                foreach ($path in @($backupPublish, $oldPublish, $staging[0].FullName)) {
                    if (-not [IO.Path]::GetFullPath($path).StartsWith(([IO.Path]::GetFullPath($caseRoot) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected recovery test path.' }
                }
                [IO.Directory]::Move($backupPublish, $oldPublish)
                Remove-Item -LiteralPath $staging[0].FullName -Recurse -Force
            }
            if (-not (Test-Path -LiteralPath $archivePath) -or (Get-FileHash -LiteralPath $archivePath).Hash -ne $previousArchiveHash -or (Get-FileHash -LiteralPath $oldChecksum).Hash -ne $previousChecksumHash -or -not (Test-Path -LiteralPath (Join-Path $oldPublish 'old-output.txt'))) { throw "Build failure destroyed previous artifacts: $caseName" }
        } else {
            if ($errorMessage) { throw $errorMessage }
            if (-not (Test-Path -LiteralPath $archivePath)) { throw 'Missing synthetic package.' }
            if (Test-Path -LiteralPath (Join-Path $oldPublish 'old-output.txt')) { throw 'Successful replacement retained stale publish files.' }
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
        if (@(Get-ChildItem -LiteralPath (Join-Path $caseRoot 'artifacts') -Directory -Force -Filter '.publish-staging-*').Count) { throw "Staging directory leaked: $caseName" }
        if ($failureStage -ne 'locked' -and (Test-Path -LiteralPath (Join-Path $caseRoot 'artifacts/.publish.lock'))) { throw 'Publish lock was not released.' }
        if ($failureStage -eq 'reparse') { [IO.Directory]::Delete($publishRoot) }
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
