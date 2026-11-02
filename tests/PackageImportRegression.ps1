#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][Diagnostics.Process]$ApplicationProcess,
    [Parameter(Mandatory)][string]$RootDirectory,
    [Parameter(Mandatory)][string]$WorkspacePath,
    [switch]$AllowDesktop
)

$ErrorActionPreference = 'Stop'
if (-not $AllowDesktop) { throw 'Package import checks require an available test desktop and -AllowDesktop.' }
. (Join-Path $PSScriptRoot 'PackageUiAutomation.ps1') -ApplicationProcess $ApplicationProcess -AllowDesktop

function Find-OwnedImportWindow([string]$Title) {
    $condition=[System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$ApplicationProcess.Id),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Title))
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)
}

function Invoke-ImportDialogButton($Window, [string]$Name) {
    $button=$Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Name),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
    if (-not $button -or -not $button.Current.IsEnabled) { throw "Import dialog button unavailable: $Name." }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Open-ImportPreview([string]$Path) {
    Invoke-PackageButton 'ImportButton'
    $picker=Wait-ForPackage { Find-OwnedImportWindow '导入 TalosDesk 工作区' } 'owned import file picker'
    $fileName=$picker.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'1148'))
    if (-not $fileName) { throw 'Native import filename control 1148 was not found.' }
    $value=$null
    if (-not $fileName.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern,[ref]$value)) {
        $fileName=$fileName.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit))
        if (-not $fileName) { throw 'Native import filename editor was not found.' }
        $value=$fileName.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    }
    $value.SetValue($Path)
    if ($value.Current.Value -ne $Path) { throw 'Native import filename did not match the isolated source.' }
    $open=$picker.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'1'),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
    if (-not $open) { throw 'Native import open button was not found.' }
    $open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return Wait-ForPackage { Find-OwnedImportWindow '确认导入工作区' } 'import preview window'
}

function Assert-ImportIdle {
    if ((Test-Path -LiteralPath (Join-Path $RootDirectory 'unexpected-command.txt')) -or
        (Test-Path -LiteralPath (Join-Path $importDirectory 'imported-command-ran.txt')) -or
        @(Get-ChildItem -LiteralPath $RootDirectory -Filter run.json -File -Recurse).Count -or $listener.Pending()) {
        throw 'Import ran a command or connected to the synthetic probe.'
    }
}

$before=(Get-FileHash -LiteralPath $WorkspacePath).Hash
$original=Get-Content -LiteralPath $WorkspacePath -Raw | ConvertFrom-Json
$importDirectory=Join-Path $RootDirectory 'imported-project'
New-Item -ItemType Directory -Path $importDirectory -ErrorAction Stop | Out-Null
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start()
try {
    $command=@{Id=[guid]::NewGuid(); Name='Imported must remain idle'; Purpose='Synthetic import command'; Kind=1; WorkingDirectory=$importDirectory;
        Command="Set-Content './imported-command-ran.txt' 'unexpected'; exit 0"; EnvironmentVariables=@();
        TcpProbe=@{Address='127.0.0.1'; Port=$listener.LocalEndpoint.Port; IntervalSeconds=1; ConnectTimeoutSeconds=1; StartupTimeoutSeconds=3; FailureThreshold=1}}
    $project=@{Id=[guid]::NewGuid(); Name='Package imported project'; Directory=$importDirectory; Commands=@($command);
        Groups=@(@{Id=[guid]::NewGuid(); Name='Imported idle group'; ExecutionMode=0; CommandIds=@($command.Id)})}
    $source=Join-Path $RootDirectory 'import-source.json'
    @{SchemaVersion=4; Projects=@($project)} | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $source -Encoding utf8
    $sourceHash=(Get-FileHash -LiteralPath $source).Hash
    foreach ($confirm in @($false,$true)) {
        $preview=Open-ImportPreview $source
        $previewText=$preview.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'WorkspaceImportPreview'))
        if (-not $previewText) { throw 'Import preview text was not found.' }
        $value=$previewText.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        foreach ($text in @($project.Name,$command.Name,'Imported idle group','导入不会自动运行命令或连接探测端口。')) {
            if (-not $value.Current.Value.Contains($text,[StringComparison]::Ordinal)) { throw 'Import preview omitted expected configuration details.' }
        }
        if (-not $value.Current.IsReadOnly -or (Get-FileHash -LiteralPath $WorkspacePath).Hash -ne $before) { throw 'Workspace changed before import confirmation.' }
        Assert-ImportIdle
        $buttonName=if($confirm){'继续导入'}else{'取消'}
        Invoke-ImportDialogButton $preview $buttonName
        Wait-ForPackage { -not (Find-OwnedImportWindow '确认导入工作区') -and (Find-PackageControl 'ImportButton').Current.IsEnabled } 'import dialog completion' | Out-Null
        if (-not $confirm -and (Get-FileHash -LiteralPath $WorkspacePath).Hash -ne $before) { throw 'Cancelled import changed workspace bytes.' }
        if ($confirm) {
            Wait-ForPackage { (Find-PackageControl 'SaveStatusText').Current.Name -eq '工作区已导入' } 'saved import status' | Out-Null
        }
        Start-Sleep -Seconds 2
        Assert-ImportIdle
    }
    $saved=Get-Content -LiteralPath $WorkspacePath -Raw | ConvertFrom-Json
    if ($saved.Projects.Count -ne 2) { throw 'Import did not save exactly the original and imported projects.' }
    $preserved=@($saved.Projects | Where-Object Id -eq $original.Projects[0].Id)
    if ($preserved.Count -ne 1 -or $preserved[0].Name -ne $original.Projects[0].Name -or $preserved[0].Directory -ne $original.Projects[0].Directory -or
        $preserved[0].Commands.Count -ne 1 -or $preserved[0].Commands[0].Id -ne $original.Projects[0].Commands[0].Id -or
        $preserved[0].Commands[0].Command -ne $original.Projects[0].Commands[0].Command) { throw 'Import did not preserve the original synthetic project.' }
    $incoming=@($saved.Projects | Where-Object Id -eq $project.Id.ToString())
    if ($incoming.Count -ne 1 -or $incoming[0].Name -ne $project.Name -or $incoming[0].Directory -ne $importDirectory -or
        $incoming[0].Commands.Count -ne 1 -or $incoming[0].Commands[0].Id -ne $command.Id.ToString() -or
        $incoming[0].Commands[0].Command -ne $command.Command -or $incoming[0].Commands[0].TcpProbe.Port -ne $listener.LocalEndpoint.Port -or
        $incoming[0].Groups.Count -ne 1 -or $incoming[0].Groups[0].CommandIds.Count -ne 1 -or $incoming[0].Groups[0].CommandIds[0] -ne $command.Id.ToString()) { throw 'Saved import did not match the previewed project, command and group.' }
    if ((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash) { throw 'Import modified its source file.' }
    [ordered]@{NativeFilePickerUsed=$true; PreviewObserved=$true; PreviewCancellationPreservedWorkspace=$true; ConfirmationSavedExpectedWorkspace=$true;
        OriginalProjectPreserved=$true; ImportedSourceUnchanged=$true; NoAutomaticCommandRunObserved=$true; NoProbeConnectionObserved=$true; ProjectCount=$saved.Projects.Count; SchemaVersion=$saved.SchemaVersion}
} finally {
    $listener.Stop()
}
