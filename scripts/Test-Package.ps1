#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [string]$ChecksumPath,
    [string]$ExpectedVersion,
    [ValidateSet('Stable', 'Preview')][string]$ExpectedChannel,
    [string]$ExpectedRuntimeVersion,
    [string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$properties = [xml](Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'Directory.Build.props'))
$versionProperties = $properties.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
if (-not $ExpectedVersion) { $ExpectedVersion = ([string]$versionProperties.Version).Trim() }
if (-not $ExpectedChannel) { $ExpectedChannel = ([string]$versionProperties.ReleaseChannel).Trim() }
if (-not $ExpectedRuntimeVersion) {
    $ExpectedRuntimeVersion = (Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'eng/dotnet-sdk.json') | ConvertFrom-Json).runtimeVersion
}
if (-not $ExpectedVersion -or $ExpectedChannel -notin @('Stable', 'Preview') -or -not $ExpectedRuntimeVersion) { throw 'Invalid expected package identity.' }
$archiveFullPath = (Resolve-Path -LiteralPath $ArchivePath).Path
if (-not $ChecksumPath) { $ChecksumPath = [IO.Path]::ChangeExtension($archiveFullPath, '.sha256') }
if ($EvidenceDirectory) {
    $EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
    if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Evidence directory must not already exist.' }
}

function Read-EntryText($Entry) {
    $reader = [IO.StreamReader]::new($Entry.Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Read-AssemblyMetadata([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $values = @{}
            foreach ($handle in $metadata.GetAssemblyDefinition().GetCustomAttributes()) {
                $attribute = $metadata.GetCustomAttribute($handle)
                if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
                $member = $metadata.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]::op_Explicit($attribute.Constructor))
                if ($member.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
                $type = $metadata.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]::op_Explicit($member.Parent))
                if ($metadata.GetString($type.Name) -ne 'AssemblyMetadataAttribute') { continue }
                $blob = $metadata.GetBlobReader($attribute.Value)
                if ($blob.ReadUInt16() -ne 1) { throw 'Invalid assembly attribute.' }
                $key = $blob.ReadSerializedString()
                $value = $blob.ReadSerializedString()
                if ($values.ContainsKey($key)) { throw 'Duplicate assembly metadata key.' }
                $values[$key] = $value
            }
            return $values
        } finally { $pe.Dispose() }
    } finally { $stream.Dispose() }
}

$archiveStream = [IO.File]::Open($archiveFullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$archive = $null
$inspectionRoot = $null
try {
    # Hash and inspect the same read-only handle; Windows denies replacement/writes until closed.
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($archiveStream)).ToLowerInvariant()
    if ((Get-Content -Raw -LiteralPath $ChecksumPath).Trim() -cne "$hash  $([IO.Path]::GetFileName($archiveFullPath))") { throw 'ZIP checksum or checksum filename mismatch.' }
    $archiveStream.Position = 0
    $archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Read, $true)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName
        if ($name -match '(^/|\\|:|(^|/)\.\.(/|$))') { throw 'Unsafe ZIP entry path.' }
        if (-not $names.Add($name)) { throw 'Duplicate ZIP entry (Windows case-insensitive comparison).' }
        if ($name -match '(?i)(workspace|\.logs/|\.log$|\.crashes/|\.pdb$|testhost|\.Tests[./]|CrashTestHost|\.trx$|(^|/)\.env($|\.)|\.pfx$|\.p12$|\.pem$|\.key$|\.secrets\.)') { throw 'Unexpected private or test file in ZIP.' }
    }
    $required = @('TalosDesk.App.exe', 'TalosDesk.App.dll', 'TalosDesk.Core.dll', 'System.Security.Cryptography.ProtectedData.dll', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll', 'README.txt', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'DOTNET-LICENSE.txt', 'DOTNET-THIRD-PARTY-NOTICES.txt', 'TalosDesk.App.deps.json', 'TalosDesk.App.runtimeconfig.json')
    foreach ($name in $required) {
        if ($null -eq $archive.GetEntry($name)) { throw "Missing package file: $name" }
    }
    if (@($archive.Entries | Where-Object { $_.FullName -like '*.json' }).Count -ne 2) { throw 'Unexpected package JSON files.' }
    $runtime = (Read-EntryText $archive.GetEntry('TalosDesk.App.runtimeconfig.json')) | ConvertFrom-Json
    $frameworks = @($runtime.runtimeOptions.includedFrameworks)
    if ($frameworks.Count -ne 2 -or @($frameworks | Where-Object { $_.version -ne $ExpectedRuntimeVersion }).Count -or @($frameworks.name | Sort-Object -Unique).Count -ne 2 -or $frameworks.name -notcontains 'Microsoft.NETCore.App' -or $frameworks.name -notcontains 'Microsoft.WindowsDesktop.App') { throw 'Included runtime baseline mismatch.' }
    $deps = (Read-EntryText $archive.GetEntry('TalosDesk.App.deps.json')) | ConvertFrom-Json
    if (-not ([string]$deps.runtimeTarget.name).EndsWith('/win-x64', [StringComparison]::Ordinal)) { throw 'Package runtime target must be win-x64.' }
    foreach ($library in @("TalosDesk.App/$ExpectedVersion", "TalosDesk.Core/$ExpectedVersion", "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/$ExpectedRuntimeVersion", "runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/$ExpectedRuntimeVersion")) {
        if ($null -eq $deps.libraries.PSObject.Properties[$library]) { throw "Dependency identity mismatch: $library" }
    }
    $readme = Read-EntryText $archive.GetEntry('README.txt')
    if (-not $readme.StartsWith("TalosDesk v$ExpectedVersion ", [StringComparison]::Ordinal) -and -not $readme.StartsWith("TalosDesk v$ExpectedVersion`r", [StringComparison]::Ordinal) -and -not $readme.StartsWith("TalosDesk v$ExpectedVersion`n", [StringComparison]::Ordinal)) { throw 'Package README version mismatch.' }

    # FileVersionInfo reads PE resources only. These files are never loaded or executed.
    $inspectionRoot = Join-Path ([IO.Path]::GetTempPath()) ('TalosDesk-PackageInspection-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $inspectionRoot | Out-Null
    $versions = foreach ($name in @('TalosDesk.App.exe', 'TalosDesk.App.dll', 'TalosDesk.Core.dll', 'coreclr.dll', 'PresentationFramework.dll', 'System.Security.Cryptography.ProtectedData.dll')) {
        $destination = Join-Path $inspectionRoot $name
        [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry($name), $destination)
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($destination)
        if ($name -like 'TalosDesk.*' -and $version.ProductVersion -cne $ExpectedVersion) { throw "PE product version mismatch: $name" }
        if (-not $version.FileVersion) { throw "Missing PE file version: $name" }
        if ($name -in @('PresentationFramework.dll', 'System.Security.Cryptography.ProtectedData.dll') -and $version.ProductVersion -notmatch ('^' + [regex]::Escape($ExpectedRuntimeVersion) + '($|[+-])')) { throw "Runtime PE product version mismatch: $name" }
        [pscustomobject]@{ Name=$name; ProductVersion=$version.ProductVersion; FileVersion=$version.FileVersion }
    }
    $metadata = Read-AssemblyMetadata (Join-Path $inspectionRoot 'TalosDesk.App.dll')
    if ($metadata['ReleaseChannel'] -cne $ExpectedChannel) { throw 'Assembly release channel mismatch.' }
    $inventory = foreach ($entry in $archive.Entries) {
        $stream = $entry.Open()
        try { $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
        [pscustomobject]@{ Name=$entry.FullName; Bytes=$entry.Length; Sha256=$entryHash }
    }
    $summary = [pscustomobject]@{
        CheckedAt=[DateTimeOffset]::UtcNow.ToString('o'); ArchiveName=[IO.Path]::GetFileName($archiveFullPath)
        Bytes=$archiveStream.Length; Sha256=$hash; EntryCount=$archive.Entries.Count
        Version=$ExpectedVersion; Channel=$ExpectedChannel; RuntimeVersion=$ExpectedRuntimeVersion; Versions=@($versions)
        Scope='Static ZIP/PE metadata and file-name checks only; no execution, dependency completeness, secret-content scan, signature trust or manual acceptance.'
    }
    if ($EvidenceDirectory) {
        New-Item -ItemType Directory -Path $EvidenceDirectory -ErrorAction Stop | Out-Null
        $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'summary.json') -Encoding utf8
        @($inventory) | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'inventory.json') -Encoding utf8
    }
    $summary
} finally {
    if ($archive) { $archive.Dispose() }
    $archiveStream.Dispose()
    if ($inspectionRoot) {
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $inspectionFullPath = [IO.Path]::GetFullPath($inspectionRoot)
        if (-not $inspectionFullPath.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($inspectionFullPath) -notlike 'TalosDesk-PackageInspection-*') { throw 'Refusing to remove an unexpected inspection path.' }
        Remove-Item -LiteralPath $inspectionFullPath -Recurse -Force
    }
}
