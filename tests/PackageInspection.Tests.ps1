#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArchivePath)

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/Test-Package.ps1'
$sourcePath = (Resolve-Path -LiteralPath $ArchivePath).Path
$originalHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
$identity = & $scriptPath -ArchivePath $sourcePath
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('TalosDesk-PackageTests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$checks = 0

function Write-Checksum([string]$Path) {
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ([IO.Path]::ChangeExtension($Path, '.sha256')) -Value "$hash  $([IO.Path]::GetFileName($Path))" -Encoding ascii
}

function Set-TextEntry($Archive, [string]$Name, [string]$Text) {
    $existing = $Archive.GetEntry($Name)
    if ($existing) { $existing.Delete() }
    $writer = [IO.StreamWriter]::new($Archive.CreateEntry($Name).Open())
    try { $writer.Write($Text) } finally { $writer.Dispose() }
}

function Assert-Rejected([string]$Name, [string]$Message, [scriptblock]$Mutation, [hashtable]$Overrides = @{}) {
    $candidate = Join-Path $testRoot "$Name.zip"
    Copy-Item -LiteralPath $baseline -Destination $candidate
    if ($Mutation) {
        $archive = [IO.Compression.ZipFile]::Open($candidate, [IO.Compression.ZipArchiveMode]::Update)
        try { & $Mutation $archive } finally { $archive.Dispose() }
    }
    Write-Checksum $candidate
    $arguments = @{ ArchivePath=$candidate; ExpectedVersion=$identity.Version; ExpectedChannel=$identity.Channel; ExpectedRuntimeVersion=$identity.RuntimeVersion }
    foreach ($key in $Overrides.Keys) { $arguments[$key] = $Overrides[$key] }
    $rejected = $false
    try { & $scriptPath @arguments | Out-Null } catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw "Wrong rejection for ${Name}: $($_.Exception.Message)" }
        $rejected = $true
    }
    if (-not $rejected) { throw "Expected rejection: $Name" }
    $script:checks++
    Write-Output "PASS: $Name"
}

try {
    # A synthetic structural fixture uses PE bytes from the supplied known package.
    # It deliberately omits runtime dependencies: this verifier is not a launch test.
    $baseline = Join-Path $testRoot 'baseline.zip'
    $source = [IO.Compression.ZipFile]::OpenRead($sourcePath)
    try {
        $fixture = [IO.Compression.ZipFile]::Open($baseline, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in @('TalosDesk.App.exe','TalosDesk.App.dll','TalosDesk.Core.dll','System.Security.Cryptography.ProtectedData.dll','coreclr.dll','hostfxr.dll','PresentationFramework.dll','README.txt','LICENSE','THIRD-PARTY-NOTICES.md','DOTNET-LICENSE.txt','DOTNET-THIRD-PARTY-NOTICES.txt','TalosDesk.App.deps.json','TalosDesk.App.runtimeconfig.json')) {
                $inputStream = $source.GetEntry($name).Open()
                $outputStream = $fixture.CreateEntry($name, [IO.Compression.CompressionLevel]::NoCompression).Open()
                try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
            }
        } finally { $fixture.Dispose() }
    } finally { $source.Dispose() }
    Write-Checksum $baseline
    $evidence = Join-Path $testRoot 'evidence'
    $summary = & $scriptPath -ArchivePath $baseline -ExpectedVersion $identity.Version -ExpectedChannel $identity.Channel -ExpectedRuntimeVersion $identity.RuntimeVersion -EvidenceDirectory $evidence
    $inventory = @(Get-Content -Raw -LiteralPath (Join-Path $evidence 'inventory.json') | ConvertFrom-Json)
    if ($summary.EntryCount -ne 14 -or $inventory.Count -ne 14 -or @($inventory | Where-Object { $_.Sha256 -notmatch '^[a-f0-9]{64}$' }).Count) { throw 'Invalid evidence inventory.' }
    $checks++
    Write-Output 'PASS: valid metadata and per-entry evidence'
    Assert-Rejected 'missing-exe' 'Missing package file' { param($zip) $zip.GetEntry('TalosDesk.App.exe').Delete() }
    Assert-Rejected 'private-file' 'private or test file' { param($zip) Set-TextEntry $zip 'workspace.json' '{}' }
    Assert-Rejected 'env-file' 'private or test file' { param($zip) Set-TextEntry $zip '.env' 'SYNTHETIC_PLACEHOLDER=value' }
    Assert-Rejected 'case-collision' 'Duplicate ZIP entry' { param($zip) Set-TextEntry $zip 'README.TXT' 'duplicate' }
    Assert-Rejected 'parent-path' 'Unsafe ZIP entry' { param($zip) Set-TextEntry $zip '../escape.txt' 'placeholder' }
    Assert-Rejected 'backslash-path' 'Unsafe ZIP entry' { param($zip) Set-TextEntry $zip 'folder\entry.txt' 'placeholder' }
    Assert-Rejected 'extra-json' 'Unexpected package JSON' { param($zip) Set-TextEntry $zip 'extra.json' '{}' }
    Assert-Rejected 'runtime-baseline' 'runtime baseline mismatch' {
        param($zip)
        $config = @{ runtimeOptions=@{ includedFrameworks=@(@{name='Microsoft.NETCore.App';version='0.0.0'}, @{name='Microsoft.WindowsDesktop.App';version='0.0.0'}) } } | ConvertTo-Json -Depth 5
        Set-TextEntry $zip 'TalosDesk.App.runtimeconfig.json' $config
    }
    Assert-Rejected 'runtime-target' 'runtime target must be win-x64' { param($zip) Set-TextEntry $zip 'TalosDesk.App.deps.json' '{"runtimeTarget":{"name":"linux-x64"},"libraries":{}}' }
    Assert-Rejected 'deps-identity' 'Dependency identity mismatch' { param($zip) Set-TextEntry $zip 'TalosDesk.App.deps.json' '{"runtimeTarget":{"name":"test/win-x64"},"libraries":{}}' }
    Assert-Rejected 'core-deps-version' 'Dependency identity mismatch' {
        param($zip)
        $reader = [IO.StreamReader]::new($zip.GetEntry('TalosDesk.App.deps.json').Open())
        try { $deps = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        foreach ($name in @($deps.libraries.PSObject.Properties.Name | Where-Object { $_.StartsWith('TalosDesk.Core/', [StringComparison]::Ordinal) })) {
            $deps.libraries.PSObject.Properties.Remove($name)
        }
        $deps.libraries | Add-Member -NotePropertyName 'TalosDesk.Core/0.0.0' -NotePropertyValue @{type='project'}
        Set-TextEntry $zip 'TalosDesk.App.deps.json' ($deps | ConvertTo-Json -Depth 30)
    }
    Assert-Rejected 'readme-version' 'README version mismatch' { param($zip) Set-TextEntry $zip 'README.txt' 'TalosDesk v0.0.0 placeholder' }
    $wrongChannel = if ($identity.Channel -eq 'Preview') { 'Stable' } else { 'Preview' }
    Assert-Rejected 'assembly-channel' 'release channel mismatch' $null @{ ExpectedChannel=$wrongChannel }
    Assert-Rejected 'bad-pe' 'PE product version mismatch' { param($zip) Set-TextEntry $zip 'TalosDesk.Core.dll' 'synthetic invalid PE' }
    Assert-Rejected 'runtime-pe' 'Missing PE file version' { param($zip) Set-TextEntry $zip 'coreclr.dll' 'synthetic invalid PE' }
    Assert-Rejected 'existing-evidence' 'must not already exist' $null @{ EvidenceDirectory=$evidence }

    $badChecksum = Join-Path $testRoot 'bad.sha256'
    Set-Content -LiteralPath $badChecksum -Value ('0' * 64 + '  baseline.zip') -Encoding ascii
    Assert-Rejected 'checksum-value' 'checksum or checksum filename mismatch' $null @{ ChecksumPath=$badChecksum }
    $baselineHash = (Get-FileHash -LiteralPath $baseline -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $badChecksum -Value "$baselineHash  another.zip" -Encoding ascii
    Assert-Rejected 'checksum-filename' 'checksum or checksum filename mismatch' $null @{ ChecksumPath=$badChecksum }
    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $originalHash) { throw 'Source ZIP changed.' }
    Write-Output "Passed $checks package inspection checks; source ZIP unchanged; no application launched."
} finally {
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $testFullPath = [IO.Path]::GetFullPath($testRoot)
    if (-not $testFullPath.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($testFullPath) -notlike 'TalosDesk-PackageTests-*') { throw 'Refusing to remove an unexpected test path.' }
    Remove-Item -LiteralPath $testFullPath -Recurse -Force
}
