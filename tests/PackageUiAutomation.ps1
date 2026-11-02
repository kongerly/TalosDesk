#requires -Version 7.2
param(
    [Parameter(Mandatory)][Diagnostics.Process]$ApplicationProcess,
    [switch]$AllowDesktop
)

if (-not $AllowDesktop) { throw 'Package UI checks require an available test desktop and -AllowDesktop.' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$main = [System.Windows.Automation.AutomationElement]::FromHandle($ApplicationProcess.MainWindowHandle)
if ($main.Current.ProcessId -ne $ApplicationProcess.Id) { throw 'Unexpected application window owner.' }

function Wait-ForPackage([scriptblock]$Probe, [string]$Description) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($ApplicationProcess.HasExited) { throw 'Packaged application exited during UI checks.' }
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
