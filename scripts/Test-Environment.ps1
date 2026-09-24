#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sdk = Get-Content -Raw (Join-Path $repoRoot 'eng/dotnet-sdk.json') | ConvertFrom-Json
$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    $checks.Add([pscustomobject]@{ name = $Name; status = $status; detail = $Detail })
    Write-Host "[$status] ${Name}: $Detail"
}

Add-Check 'Windows x64' ($IsWindows -and [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64') ([Environment]::OSVersion.VersionString)
Add-Check 'PowerShell 7' ($PSVersionTable.PSVersion.Major -ge 7) $PSVersionTable.PSVersion.ToString()
$gitRoot = & git -C $repoRoot rev-parse --show-toplevel 2>$null
Add-Check 'Git repository' ($LASTEXITCODE -eq 0 -and [IO.Path]::GetFullPath($gitRoot) -eq [IO.Path]::GetFullPath($repoRoot)) $repoRoot

$sdkRoot = Join-Path $repoRoot ".tools/dotnet/$($sdk.version)"
$marker = Join-Path $sdkRoot '.talosdesk-sdk.sha512'
$markerReady = (Test-Path -LiteralPath $marker) -and (Get-Content -Raw $marker).Trim() -eq $sdk.sha512
$dotnetExe = Join-Path $sdkRoot 'dotnet.exe'
if (Test-Path -LiteralPath $dotnetExe) {
    $actualVersion = (& $dotnetExe --version 2>&1 | Out-String).Trim()
    $versionReady = $LASTEXITCODE -eq 0 -and $actualVersion -eq $sdk.version
    Add-Check 'Pinned SDK' ($versionReady -and $markerReady) "expected $($sdk.version), found $actualVersion; install marker verified: $markerReady"
    $runtimes = & $dotnetExe --list-runtimes
    $desktopReady = $LASTEXITCODE -eq 0 -and ($runtimes -match "Microsoft\.WindowsDesktop\.App $([regex]::Escape($sdk.runtimeVersion)) ")
    Add-Check 'Windows Desktop runtime' $desktopReady $sdk.runtimeVersion
    $wpfTemplate = (& $dotnetExe new list wpf 2>&1 | Out-String)
    Add-Check 'WPF template' ($LASTEXITCODE -eq 0 -and $wpfTemplate -match '\bwpf\b') 'Listed templates only; no application project was generated.'
}
else {
    Add-Check 'Pinned SDK' $false "missing $dotnetExe"
    Add-Check 'Windows Desktop runtime' $false 'SDK unavailable'
    Add-Check 'WPF template' $false 'SDK unavailable'
}

$checks | Format-Table -AutoSize
if (@($checks | Where-Object status -eq 'FAIL').Count -gt 0) { exit 1 }
