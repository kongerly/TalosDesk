#requires -Version 7.0
# Run the repository-pinned SDK without changing machine-wide settings.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sdk = Get-Content -Raw (Join-Path $repoRoot 'eng/dotnet-sdk.json') | ConvertFrom-Json
$sdkRoot = Join-Path $repoRoot ".tools/dotnet/$($sdk.version)"
$dotnetExe = Join-Path $sdkRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) {
    throw 'TalosDesk SDK not found. Run scripts/Install-Sdk.ps1 first.'
}

$env:DOTNET_ROOT = $sdkRoot
$env:DOTNET_ROOT_X64 = $sdkRoot
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/nuget/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
if (($env:PATH -split ';') -notcontains $sdkRoot) { $env:PATH = "$sdkRoot;$env:PATH" }

Push-Location $repoRoot
try {
    & $dotnetExe @args
    $dotnetExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
exit $dotnetExitCode
