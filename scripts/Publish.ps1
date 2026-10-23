#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$properties = [xml](Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props'))
$versionPropertyGroup = $properties.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
$releaseVersion = ([string]$versionPropertyGroup.Version).Trim()
if ([string]::IsNullOrWhiteSpace($releaseVersion)) { throw 'Directory.Build.props does not define a release version.' }
$runtime = Get-Content -Raw (Join-Path $repoRoot 'eng/dotnet-sdk.json') | ConvertFrom-Json
$releaseName = "TalosDesk-v$releaseVersion-win-x64"
$publishDirectory = Join-Path $artifactsRoot "publish/$releaseName"
$archivePath = Join-Path $artifactsRoot "$releaseName.zip"
$checksumPath = Join-Path $artifactsRoot "$releaseName.sha256"
$testResultsDirectory = Join-Path $artifactsRoot 'tests'
$withSdk = Join-Path $PSScriptRoot 'With-Sdk.ps1'

function Assert-ArtifactPath([string]$Path) {
    $artifactFullPath = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $targetFullPath = [IO.Path]::GetFullPath($Path)
    if (-not $targetFullPath.StartsWith($artifactFullPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the artifacts directory: $targetFullPath"
    }
    $currentPath = $targetFullPath
    $artifactRootPath = $artifactFullPath.TrimEnd([IO.Path]::DirectorySeparatorChar)
    while ($currentPath.StartsWith($artifactFullPath, [StringComparison]::OrdinalIgnoreCase) -or [string]::Equals($currentPath, $artifactRootPath, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $currentPath) -and ((Get-Item -LiteralPath $currentPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to modify an artifact path through a reparse point: $currentPath"
        }
        $currentPath = Split-Path -Parent $currentPath
    }
}

function Invoke-PinnedDotNet([string[]]$Arguments) {
    & $withSdk @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code ${LASTEXITCODE}: dotnet $($Arguments -join ' ')"
    }
}

function Move-Artifact([string]$Source, [string]$Destination, [bool]$IsDirectory) {
    Assert-ArtifactPath $Source
    Assert-ArtifactPath $Destination
    if ($IsDirectory) { [IO.Directory]::Move($Source, $Destination) }
    else { [IO.File]::Move($Source, $Destination) }
}

$stagingRoot = Join-Path $artifactsRoot ('.publish-staging-' + [guid]::NewGuid().ToString('N'))
$stagedPublish = Join-Path $stagingRoot 'new-publish'
$stagedArchive = Join-Path $stagingRoot "$releaseName.zip"
$stagedChecksum = Join-Path $stagingRoot "$releaseName.sha256"
$backupRoot = Join-Path $stagingRoot 'previous'
$lockPath = Join-Path $artifactsRoot '.publish.lock'
$publishLock = $null
$preserveStaging = $false

try {
    foreach ($target in @($publishDirectory, $archivePath, $checksumPath, $stagingRoot, $lockPath)) { Assert-ArtifactPath $target }
    New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
    try {
        $publishLock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
    } catch { throw "Cannot acquire the publish lock; another publish may be running. $($_.Exception.Message)" }

    New-Item -ItemType Directory -Path $stagedPublish, $backupRoot -Force | Out-Null
    Invoke-PinnedDotNet @('restore', 'TalosDesk.slnx')
    Invoke-PinnedDotNet @('build', 'TalosDesk.slnx', '--configuration', 'Release', '--no-restore')
    Invoke-PinnedDotNet @('test', 'TalosDesk.slnx', '--configuration', 'Release', '--no-build', '--no-restore', '--logger', 'trx', '--results-directory', $testResultsDirectory)
    Invoke-PinnedDotNet @(
        'publish', 'src/TalosDesk.App/TalosDesk.App.csproj',
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--property:PublishSingleFile=false',
        "--property:Version=$releaseVersion",
        '--output', $stagedPublish
    )

    Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/README.txt') -Destination (Join-Path $stagedPublish 'README.txt')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $stagedPublish 'LICENSE')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $stagedPublish 'THIRD-PARTY-NOTICES.md')

    $sdkRoot = Join-Path $repoRoot ".tools/dotnet/$($runtime.version)"
    $dotnetLicense = Join-Path $sdkRoot 'LICENSE.txt'
    $dotnetNotices = Join-Path $sdkRoot 'ThirdPartyNotices.txt'
    if (-not (Test-Path -LiteralPath $dotnetLicense) -or -not (Test-Path -LiteralPath $dotnetNotices)) {
        throw 'The pinned .NET SDK license or third-party notices file is missing.'
    }
    Copy-Item -LiteralPath $dotnetLicense -Destination (Join-Path $stagedPublish 'DOTNET-LICENSE.txt')
    Copy-Item -LiteralPath $dotnetNotices -Destination (Join-Path $stagedPublish 'DOTNET-THIRD-PARTY-NOTICES.txt')
    Compress-Archive -Path (Join-Path $stagedPublish '*') -DestinationPath $stagedArchive -CompressionLevel Optimal
    $checksum = (Get-FileHash -Algorithm SHA256 -LiteralPath $stagedArchive).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $stagedChecksum -Value "$checksum  $releaseName.zip" -Encoding ascii

    # Commit only after every generation step succeeds. Moves never overwrite a destination.
    $entries = @(
        [pscustomobject]@{ Source=$stagedPublish; Destination=$publishDirectory; Backup=(Join-Path $backupRoot 'publish'); IsDirectory=$true; BackedUp=$false; Installed=$false },
        [pscustomobject]@{ Source=$stagedArchive; Destination=$archivePath; Backup=(Join-Path $backupRoot 'archive.zip'); IsDirectory=$false; BackedUp=$false; Installed=$false },
        [pscustomobject]@{ Source=$stagedChecksum; Destination=$checksumPath; Backup=(Join-Path $backupRoot 'checksum.sha256'); IsDirectory=$false; BackedUp=$false; Installed=$false }
    )
    New-Item -ItemType Directory -Path (Split-Path -Parent $publishDirectory) -Force | Out-Null
    try {
        foreach ($entry in $entries) {
            if (Test-Path -LiteralPath $entry.Destination) {
                Move-Artifact $entry.Destination $entry.Backup $entry.IsDirectory
                $entry.BackedUp = $true
            }
            Move-Artifact $entry.Source $entry.Destination $entry.IsDirectory
            $entry.Installed = $true
        }
    } catch {
        $commitFailure = $_
        $rollbackFailures = @()
        for ($index = $entries.Count - 1; $index -ge 0; $index--) {
            $entry = $entries[$index]
            try {
                if ($entry.Installed) { Move-Artifact $entry.Destination $entry.Source $entry.IsDirectory }
                if ($entry.BackedUp) { Move-Artifact $entry.Backup $entry.Destination $entry.IsDirectory }
            } catch { $rollbackFailures += $_.Exception.Message }
        }
        if ($rollbackFailures.Count) {
            $preserveStaging = $true
            throw "Artifact replacement and recovery failed. Preserve $stagingRoot for manual recovery. $($rollbackFailures -join ' ')"
        }
        throw "Artifact replacement failed; previous artifacts restored. $($commitFailure.Exception.Message)"
    }
    Write-Host "Release package: $archivePath"
    Write-Host "SHA-256: $checksum"
} finally {
    try {
        if (-not $preserveStaging -and (Test-Path -LiteralPath $stagingRoot)) {
            Assert-ArtifactPath $stagingRoot
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        }
    } catch { Write-Warning "Cannot clean publish staging directory; inspect $stagingRoot. $($_.Exception.Message)" }
    finally { if ($publishLock) { $publishLock.Dispose() } }
}
