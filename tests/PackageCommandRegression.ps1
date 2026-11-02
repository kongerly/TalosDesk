#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][Diagnostics.Process]$ApplicationProcess,
    [Parameter(Mandatory)][string]$RootDirectory,
    [Parameter(Mandatory)][object[]]$Commands,
    [Parameter(Mandatory)][string]$PowerShellPath,
    [switch]$AllowDesktop
)

$ErrorActionPreference = 'Stop'
if (-not $AllowDesktop) { throw 'Package command checks require an available test desktop and -AllowDesktop.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$main = [System.Windows.Automation.AutomationElement]::FromHandle($ApplicationProcess.MainWindowHandle)
if ($main.Current.ProcessId -ne $ApplicationProcess.Id) { throw 'Unexpected application window owner.' }

function Wait-ForPackage([scriptblock]$Probe, [string]$Description) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($ApplicationProcess.HasExited) { throw 'Packaged application exited during command checks.' }
        $result = & $Probe
        if ($result) { return $result }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt 20)
    throw "Timed out waiting for $Description."
}

function Find-PackageControl([string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-PackageButton([string]$Id) {
    $button = Wait-ForPackage { $candidate = Find-PackageControl $Id; if ($candidate -and $candidate.Current.IsEnabled -and -not $candidate.Current.IsOffscreen) { $candidate } } "enabled $Id"
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Read-PackageRun([guid]$CommandId) {
    foreach ($file in Get-ChildItem -LiteralPath $RootDirectory -Filter run.json -File -Recurse) {
        # 元数据以原子替换写入；读取时仍可能恰好与替换竞争，下一轮重新检查。
        try { $info = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json }
        catch [IO.IOException] { continue }
        if ($info.CommandId -eq $CommandId.ToString() -and $info.EndedAt) {
            return @{ Info=$info; Directory=$file.DirectoryName }
        }
    }
}

$checks = @()
foreach ($name in @('Package success', 'Package failure', 'Package service')) {
    Invoke-PackageButton 'CommandsPageButton'
    $command = @($Commands | Where-Object Name -eq $name)
    if ($command.Count -ne 1) { throw 'Expected one synthetic command.' }
    $item = Wait-ForPackage {
        $list = Find-PackageControl 'CommandList'
        if ($list) {
            foreach ($candidate in $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
                $text = $candidate.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name))
                if ($text) { return $candidate }
            }
        }
    } "$name list item"
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-PackageButton 'RunButton'
    $expectedOutput = switch ($name) { 'Package success' { 'PACKAGE_SUCCESS_中文' }; 'Package failure' { 'PACKAGE_FAILURE' }; default { 'PACKAGE_SERVICE_READY' } }
    Wait-ForPackage {
        $terminal = Find-PackageControl 'OutputTextBox'
        $terminal -and $terminal.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value.Contains($expectedOutput, [StringComparison]::Ordinal)
    } "$name rendered output" | Out-Null
    $service = $null
    try {
        if ($name -eq 'Package service') {
            $pidFile = Join-Path $RootDirectory 'service.pid'
            Wait-ForPackage { Test-Path -LiteralPath $pidFile } 'synthetic service PID' | Out-Null
            $serviceId = [int](Get-Content -LiteralPath $pidFile -Raw)
            $service = [Diagnostics.Process]::GetProcessById($serviceId)
            $null = $service.Handle
            if ($service.StartTime -lt $ApplicationProcess.StartTime -or $service.MainModule.FileName -ne $PowerShellPath) { throw 'Synthetic service identity did not match.' }
            Invoke-PackageButton 'OutputStopButton'
            if (-not $service.WaitForExit(15000)) { throw 'Stopped service process remained alive.' }
        }
        $run = Wait-ForPackage { Read-PackageRun $command[0].Id } "$name completed log"
        $expectedState = switch ($name) { 'Package success' { 'Succeeded' }; 'Package failure' { 'Failed' }; default { 'Stopped' } }
        if ($run.Info.State -ne $expectedState -or $run.Info.WriteFailed -or $run.Info.Truncated) { throw "Unexpected result for $name." }
        if ($name -ne 'Package service') {
            $expectedCode = if ($name -eq 'Package success') { 0 } else { 7 }
            if ($run.Info.ExitCode -ne $expectedCode) { throw "Unexpected exit code for $name." }
        }
        $stdout = Get-Content -LiteralPath (Join-Path $run.Directory 'stdout.log') -Raw
        if (-not $stdout.Contains($expectedOutput, [StringComparison]::Ordinal)) { throw "Missing stdout for $name." }
        if ($name -eq 'Package success') {
            $stderr = Get-Content -LiteralPath (Join-Path $run.Directory 'stderr.log') -Raw
            if (-not $stderr.Contains('PACKAGE_STDERR', [StringComparison]::Ordinal)) { throw 'Missing successful task stderr.' }
        }
        Wait-ForPackage { $runButton=Find-PackageControl 'OutputRunButton'; $stopButton=Find-PackageControl 'OutputStopButton'; $runButton -and $stopButton -and $runButton.Current.IsEnabled -and -not $stopButton.Current.IsEnabled } 'idle command controls' | Out-Null
        $checks += [ordered]@{ Name=$name; State=$run.Info.State; ExitCode=$run.Info.ExitCode; OutputVerified=$true; ProcessExitVerified=($name -eq 'Package service') }
    } finally {
        if ($service) { $service.Dispose() }
    }
}
$checks
