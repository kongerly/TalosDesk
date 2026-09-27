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
$withSdk = Join-Path $PSScriptRoot 'With-Sdk.ps1'

function Assert-ArtifactPath([string]$Path) {
    $artifactFullPath = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $targetFullPath = [IO.Path]::GetFullPath($Path)
    if (-not $targetFullPath.StartsWith($artifactFullPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the artifacts directory: $targetFullPath"
    }
}

function Invoke-PinnedDotNet([string[]]$Arguments) {
    & $withSdk @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code ${LASTEXITCODE}: dotnet $($Arguments -join ' ')"
    }
}

foreach ($target in @($publishDirectory, $archivePath, $checksumPath)) {
    Assert-ArtifactPath $target
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

Invoke-PinnedDotNet @('restore', 'TalosDesk.slnx')
Invoke-PinnedDotNet @('build', 'TalosDesk.slnx', '--configuration', 'Release', '--no-restore')
Invoke-PinnedDotNet @('test', 'TalosDesk.slnx', '--configuration', 'Release', '--no-build', '--no-restore')
Invoke-PinnedDotNet @(
    'publish', 'src/TalosDesk.App/TalosDesk.App.csproj',
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--property:PublishSingleFile=false',
    "--property:Version=$releaseVersion",
    '--output', $publishDirectory
)

Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/README.txt') -Destination (Join-Path $publishDirectory 'README.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $publishDirectory 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $publishDirectory 'THIRD-PARTY-NOTICES.md')

$sdkRoot = Join-Path $repoRoot ".tools/dotnet/$($runtime.version)"
$dotnetLicense = Join-Path $sdkRoot 'LICENSE.txt'
$dotnetNotices = Join-Path $sdkRoot 'ThirdPartyNotices.txt'
if (-not (Test-Path -LiteralPath $dotnetLicense) -or -not (Test-Path -LiteralPath $dotnetNotices)) {
    throw 'The pinned .NET SDK license or third-party notices file is missing.'
}
Copy-Item -LiteralPath $dotnetLicense -Destination (Join-Path $publishDirectory 'DOTNET-LICENSE.txt')
Copy-Item -LiteralPath $dotnetNotices -Destination (Join-Path $publishDirectory 'DOTNET-THIRD-PARTY-NOTICES.txt')

Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
$checksum = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath -Value "$checksum  $releaseName.zip" -Encoding ascii

Write-Host "Release package: $archivePath"
Write-Host "SHA-256: $checksum"
