#requires -Version 7.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HoverTestWindow {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
'@

$repoRoot = Split-Path -Parent $PSScriptRoot
$reviewRoot = Join-Path $repoRoot '.tools/command-scroll-review'
$sourceRoot = Join-Path $reviewRoot 'source'
$workspacePath = Join-Path $reviewRoot 'workspace.json'
$realWorkspace = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'TalosDesk/workspace.json'
$realHashBefore = if (Test-Path -LiteralPath $realWorkspace) { (Get-FileHash -LiteralPath $realWorkspace -Algorithm SHA256).Hash } else { $null }
$process = $null
$originalCursor = [System.Windows.Forms.Cursor]::Position

function Wait-For([scriptblock]$probe, [string]$description) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        $result = & $probe
        if ($result) { return $result }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out waiting for $description"
}

function Find-ById($parent, [string]$automationId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ButtonByName($parent, [string]$name) {
    $buttons = $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button))
    foreach ($button in $buttons) {
        if ($button.Current.Name -eq $name) { return $button }
    }
    throw "Could not find button $name."
}

function Activate-Window($window, [string]$description) {
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    [HoverTestWindow]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 150
    if ([HoverTestWindow]::GetForegroundWindow() -ne $handle) {
        throw "Could not bring $description to foreground for hover verification."
    }
}

function Select-ListItem($list, [string]$name) {
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($item in $items) {
        $texts = $item.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($text in $texts) {
            if ($text.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and $text.Current.Name -eq $name) {
                $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                return
            }
        }
    }
    throw "Could not select $name."
}

function Assert-HoverStable($button, $neighbor, [string]$description) {
    if ($button.Current.IsOffscreen -or $neighbor.Current.IsOffscreen) {
        throw "$description is not visible for hover verification."
    }
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new(0, 0)
    Start-Sleep -Milliseconds 150
    $buttonBefore = $button.Current.BoundingRectangle.ToString()
    $neighborBefore = $neighbor.Current.BoundingRectangle.ToString()
    $bounds = $button.Current.BoundingRectangle
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new(
        [int][Math]::Round($bounds.X + $bounds.Width / 2),
        [int][Math]::Round($bounds.Y + $bounds.Height / 2))
    Start-Sleep -Milliseconds 200
    $buttonDuring = $button.Current.BoundingRectangle.ToString()
    $neighborDuring = $neighbor.Current.BoundingRectangle.ToString()
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new(0, 0)
    Start-Sleep -Milliseconds 150
    $buttonAfter = $button.Current.BoundingRectangle.ToString()
    $neighborAfter = $neighbor.Current.BoundingRectangle.ToString()
    if ($buttonBefore -ne $buttonDuring -or $buttonBefore -ne $buttonAfter -or
        $neighborBefore -ne $neighborDuring -or $neighborBefore -ne $neighborAfter) {
        throw "$description moved during hover: button $buttonBefore -> $buttonDuring -> $buttonAfter; neighbor $neighborBefore -> $neighborDuring -> $neighborAfter."
    }
}

try {
    New-Item -ItemType Directory -Force -Path $reviewRoot, $sourceRoot | Out-Null
    foreach ($projectName in @('TalosDesk.Core', 'TalosDesk.App')) {
        $from = Join-Path $repoRoot "src/$projectName"
        $to = Join-Path $sourceRoot "src/$projectName"
        Get-ChildItem -LiteralPath $from -Recurse -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
            ForEach-Object {
                $relative = $_.FullName.Substring($from.Length).TrimStart('\')
                $destination = Join-Path $to $relative
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
            }
    }

    $sampleDirectory = Join-Path $reviewRoot 'sample-project'
    $secondDirectory = Join-Path $reviewRoot 'second-project'
    New-Item -ItemType Directory -Force -Path $sampleDirectory, $secondDirectory | Out-Null
    $commands = @(1..12 | ForEach-Object {
        [ordered]@{
            Id = [guid]::NewGuid().ToString()
            Name = ('测试命令 {0:D2}' -f $_)
            Purpose = '滚动定位检查'
            Command = ("Write-Output 'SCROLL_TEST_{0:D2}'" -f $_)
            WorkingDirectory = $sampleDirectory
            Kind = 0
        }
    })
    $workspace = [ordered]@{
        SchemaVersion = 1
        Projects = @(
            [ordered]@{
                Id = [guid]::NewGuid().ToString()
                Name = '滚动测试项目'
                Directory = $sampleDirectory
                Commands = $commands
            },
            [ordered]@{
                Id = [guid]::NewGuid().ToString()
                Name = '第二个测试项目'
                Directory = $secondDirectory
                Commands = @([ordered]@{
                    Id = [guid]::NewGuid().ToString()
                    Name = '第二项目命令'
                    Purpose = '切换项目检查'
                    Command = "Write-Output 'SECOND_PROJECT_OUTPUT'"
                    WorkingDirectory = $secondDirectory
                    Kind = 0
                })
            }
        )
    }
    $workspace | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $workspacePath -Encoding utf8

    $mainSource = Join-Path $sourceRoot 'src/TalosDesk.App/MainWindow.xaml.cs'
    $sourceText = Get-Content -LiteralPath $mainSource -Raw
    $old = 'private readonly WorkspaceStore _store = new();'
    if (-not $sourceText.Contains($old)) { throw 'WorkspaceStore declaration changed; update the isolated test setup.' }
    $replacement = 'private readonly WorkspaceStore _store = new(@"' + $workspacePath + '");'
    [System.IO.File]::WriteAllText($mainSource, $sourceText.Replace($old, $replacement))

    $appProject = Join-Path $sourceRoot 'src/TalosDesk.App/TalosDesk.App.csproj'
    & dotnet build $appProject -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Isolated WPF build failed.' }
    $exe = Join-Path $sourceRoot 'src/TalosDesk.App/bin/Release/net10.0-windows/TalosDesk.App.exe'
    $process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $main = Wait-For { $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $processCondition) } 'test window'
    $projectNameCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ProjectNameText')
    Wait-For {
        $projectName = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $projectNameCondition)
        $null -ne $projectName -and $projectName.Current.Name -eq '滚动测试项目'
    } 'synthetic workspace' | Out-Null
    if ((Find-ById $main 'ProjectNameText').Current.IsOffscreen) { throw 'Project overview is not the startup page.' }
    Activate-Window $main 'main window'
    Assert-HoverStable (Find-ById $main 'OverviewPageButton') (Find-ById $main 'CommandsPageButton') 'Overview navigation button at default size'
    Assert-HoverStable (Find-ById $main 'OverviewCommandsButton') (Find-ById $main 'OverviewOutputButton') 'Overview action button at default size'
    (Find-ById $main 'AddProjectButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $projectEditorCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'TalosDesk · 项目')
    $projectEditor = Wait-For {
        $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $projectEditorCondition)
    } 'project editor window'
    Activate-Window $projectEditor 'project editor'
    $projectCancel = Find-ButtonByName $projectEditor '取消'
    Assert-HoverStable $projectCancel (Find-ById $projectEditor 'SaveButton') 'Project editor button'
    $projectCancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { $null -eq $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $projectEditorCondition) } 'project editor to close' | Out-Null
    Write-Output 'PASS: default-size navigation and action buttons keep their bounds and neighboring controls stable on hover.'
    Write-Output 'PASS: project editor buttons keep their bounds stable on hover.'
    $navigationCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CommandsPageButton')
    $navigation = Wait-For { $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $navigationCondition) } 'command page navigation'
    $navigation.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $listCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CommandList')
    $list = Wait-For {
        $candidate = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCondition)
        if ($null -ne $candidate -and -not $candidate.Current.IsOffscreen) { $candidate }
    } 'visible command list'
    function Count-FullyVisibleCommands {
        $bounds = $list.Current.BoundingRectangle
        $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
        $visible = @($items | Where-Object {
            $itemBounds = $_.Current.BoundingRectangle
            -not $_.Current.IsOffscreen -and $itemBounds.Height -gt 0 -and
            $itemBounds.Top -ge $bounds.Top -and $itemBounds.Bottom -le $bounds.Bottom
        })
        return $visible.Count
    }
    $defaultVisible = Count-FullyVisibleCommands
    if ($defaultVisible -lt 6) { throw "Default window shows only $defaultVisible complete command cards; expected at least 6." }
    $transform = $main.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    $transform.Resize(1050, 650)
    Start-Sleep -Milliseconds 350
    Activate-Window $main 'main window at minimum size'
    Assert-HoverStable (Find-ById $main 'OverviewPageButton') (Find-ById $main 'CommandsPageButton') 'Overview navigation button at minimum size'
    (Find-ById $main 'AddCommandButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $commandEditorCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'TalosDesk · 命令')
    $commandEditor = Wait-For {
        $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $commandEditorCondition)
    } 'command editor window'
    Activate-Window $commandEditor 'command editor'
    $commandCancel = Find-ButtonByName $commandEditor '取消'
    Assert-HoverStable $commandCancel (Find-ButtonByName $commandEditor '保存命令') 'Command editor button'
    $commandCancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { $null -eq $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $commandEditorCondition) } 'command editor to close' | Out-Null
    Write-Output 'PASS: minimum-size navigation buttons keep their bounds and neighboring controls stable on hover.'
    Write-Output 'PASS: command editor buttons keep their bounds stable on hover.'
    $minimumVisible = Count-FullyVisibleCommands
    if ($minimumVisible -lt 4) { throw "Minimum window shows only $minimumVisible complete command cards; expected at least 4." }
    $scroll = $list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if (-not $scroll.Current.VerticallyScrollable) { throw 'The command list is not scrollable.' }
    $before = $scroll.Current.VerticalScrollPercent
    1..3 | ForEach-Object { $scroll.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::SmallIncrement) }
    Start-Sleep -Milliseconds 400
    $after = $scroll.Current.VerticalScrollPercent
    if ($before -ne 0) { throw "Expected initial scroll position 0%, got $before%." }
    if ($after -le 0 -or $after -ge 10) { throw "Three small scroll steps moved command list from $before% to $after%; expected a selectable step below 10%." }

    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 50)
    Start-Sleep -Milliseconds 300
    $middleItem = $null
    $middleName = $null
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($item in $items) {
        if ($item.Current.IsOffscreen) { continue }
        $texts = $item.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($node in $texts) {
            if ($node.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and $node.Current.Name -match '^测试命令 (\d\d)$') {
                $middleItem = $item
                $middleName = $node.Current.Name
                break
            }
        }
        if ($null -ne $middleItem) { break }
    }
    if ($null -eq $middleItem -or $middleName -notmatch '^测试命令 (0[4-9]|10)$') {
        throw "Could not locate a selectable command near the middle of the list: $middleName"
    }
    $middleItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $selectedNameCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SelectedCommandName')
    $selectedName = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $selectedNameCondition)
    if ($selectedName.Current.Name -ne $middleName) { throw "Selected command was $($selectedName.Current.Name), expected $middleName." }
    Write-Output ('PASS: {0} cards at default size, {1} at minimum; three small scroll steps moved {2:N1}%; selected {3} near the middle.' -f $defaultVisible, $minimumVisible, $after, $middleName)

    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0)
    Start-Sleep -Milliseconds 250
    foreach ($number in 1..2) {
        $checkName = ('勾选 测试命令 {0:D2}' -f $number)
        $checkCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $checkName)
        $check = Wait-For { $list.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $checkCondition) } $checkName
        $check.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    }
    (Find-ById $main 'BatchRunButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $outputSelector = Wait-For {
        $candidate = Find-ById $main 'OutputCommandList'
        if ($null -ne $candidate -and -not $candidate.Current.IsOffscreen) { $candidate }
    } 'output page after batch run'
    $logName = Find-ById $main 'LogCommandName'
    if ($logName.Current.Name -ne '测试命令 01') { throw "Batch run selected $($logName.Current.Name) instead of its first started command." }
    Wait-For {
        $log = Find-ById $main 'OutputList'
        $nodes = $log.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        @($nodes | Where-Object { $_.Current.Name -eq 'SCROLL_TEST_01' }).Count -gt 0
    } 'first batch command output' | Out-Null
    Select-ListItem $outputSelector '测试命令 02'
    if ($logName.Current.Name -ne '测试命令 02') { throw 'Output selector did not change the selected command.' }
    Wait-For {
        $log = Find-ById $main 'OutputList'
        $nodes = $log.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        @($nodes | Where-Object { $_.Current.Name -eq 'SCROLL_TEST_02' }).Count -gt 0
    } 'second batch command output' | Out-Null
    $navigation.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if ($selectedName.Current.Name -ne '测试命令 02') { throw 'Command management did not retain the output page selection.' }
    (Find-ById $main 'OutputPageButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Find-ById $main 'OverviewPageButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { (Find-ById $main 'OverviewFinishedCountText').Current.Name -eq '2' } 'session overview result count' | Out-Null
    (Find-ById $main 'OutputPageButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $projects = Find-ById $main 'ProjectList'
    Select-ListItem $projects '第二个测试项目'
    if ($outputSelector.Current.IsOffscreen) { throw 'Switching projects left the output page.' }
    if ($logName.Current.Name -ne '选择命令以查看输出') { throw 'Switching projects kept a command from the previous project selected.' }
    Select-ListItem $projects '滚动测试项目'
    Select-ListItem $outputSelector '测试命令 01'
    Wait-For {
        $log = Find-ById $main 'OutputList'
        $nodes = $log.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        @($nodes | Where-Object { $_.Current.Name -eq 'SCROLL_TEST_01' }).Count -gt 0
    } 'preserved output after project switch' | Out-Null

    $navigation.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Select-ListItem $list '测试命令 03'
    (Find-ById $main 'RunButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { -not $outputSelector.Current.IsOffscreen -and $logName.Current.Name -eq '测试命令 03' } 'output page after single run' | Out-Null
    Write-Output 'PASS: batch and single runs open the output page; selection, logs, and project switching remain consistent.'
}
finally {
    [System.Windows.Forms.Cursor]::Position = $originalCursor
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            if (-not $process.WaitForExit(2500)) { Stop-Process -Id $process.Id -Force }
        }
        $process.Dispose()
    }
    $realHashAfter = if (Test-Path -LiteralPath $realWorkspace) { (Get-FileHash -LiteralPath $realWorkspace -Algorithm SHA256).Hash } else { $null }
    if ($realHashAfter -ne $realHashBefore) { throw 'Real TalosDesk workspace changed during isolated scroll test.' }
}
