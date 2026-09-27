#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'TalosDesk development requires Windows x64.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$sdk = Get-Content -Raw (Join-Path $repoRoot 'eng/dotnet-sdk.json') | ConvertFrom-Json
$globalConfig = Get-Content -Raw (Join-Path $repoRoot 'global.json') | ConvertFrom-Json
if ($sdk.version -ne $globalConfig.sdk.version -or $sdk.version -notmatch '^10\.0\.\d+$') {
    throw 'The SDK metadata and global.json must agree on a stable .NET 10 SDK.'
}

$downloadUri = [uri]$sdk.url
if ($downloadUri.Scheme -ne 'https' -or $downloadUri.Host -ne 'builds.dotnet.microsoft.com' -or
    $sdk.sha512 -notmatch '^[a-fA-F0-9]{128}$') {
    throw 'Expected a Microsoft HTTPS download URL and a SHA-512 checksum.'
}

$sdkRoot = Join-Path $repoRoot ".tools/dotnet/$($sdk.version)"
$dotnetExe = Join-Path $sdkRoot 'dotnet.exe'
$marker = Join-Path $sdkRoot '.talosdesk-sdk.sha512'
if (Test-Path -LiteralPath $sdkRoot) {
    if ((Test-Path -LiteralPath $dotnetExe) -and (Test-Path -LiteralPath $marker) -and
        (Get-Content -Raw $marker).Trim() -eq $sdk.sha512) {
        Write-Host "SDK $($sdk.version) is already installed. Run scripts/Test-Environment.ps1 to verify it."
        exit 0
    }
    throw "An incomplete or unrecognized SDK directory exists: $sdkRoot. Inspect it before retrying; it will not be overwritten."
}

$downloadRoot = Join-Path $repoRoot '.tools/downloads'
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
$archivePath = Join-Path $downloadRoot "dotnet-sdk-$($sdk.version)-win-x64.zip"
if (-not (Test-Path -LiteralPath $archivePath)) {
    $partialPath = "$archivePath.partial"
    Write-Host "Downloading .NET SDK $($sdk.version) from Microsoft..."
    & curl.exe --fail --location --retry 3 --connect-timeout 30 --max-time 1200 --output $partialPath $downloadUri.AbsoluteUri
    if ($LASTEXITCODE -ne 0) { throw "SDK download failed with curl exit code $LASTEXITCODE. The partial download was retained." }
    if ((Get-FileHash -LiteralPath $partialPath -Algorithm SHA512).Hash -ne $sdk.sha512) {
        throw 'SDK archive checksum mismatch. The partial download was retained; nothing was installed.'
    }
    Move-Item -LiteralPath $partialPath -Destination $archivePath
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash -ne $sdk.sha512) {
    throw "Cached SDK archive checksum mismatch. Inspect it before retrying: $archivePath"
}

Write-Host 'Checksum verified. Extracting the SDK...'
New-Item -ItemType Directory -Path $sdkRoot | Out-Null
try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $sdkRoot
    if (-not (Test-Path -LiteralPath $dotnetExe)) { throw 'SDK extraction did not produce dotnet.exe.' }
    Set-Content -LiteralPath $marker -Value $sdk.sha512 -Encoding utf8
}
catch {
    throw "SDK extraction failed. The directory was retained for inspection: $sdkRoot. $($_.Exception.Message)"
}
Write-Host "SDK installed at $sdkRoot"
