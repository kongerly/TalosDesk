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
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    public static void LeftDown(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
    }
    public static void LeftUp(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
    public static void LeftClick(int x, int y) {
        LeftDown(x, y);
        LeftUp(x, y);
    }
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

function Get-SavedCommandNames([string]$path) {
    return @((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).Projects[0].Commands.Name)
}

function Find-ById($parent, [string]$automationId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-TextValue($element) {
    return $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
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

function Find-GroupButton($list, [string]$groupName, [string]$buttonName) {
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($item in $items) {
        $nodes = $item.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $hasGroupName = @($nodes | Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and $_.Current.Name -eq $groupName
        }).Count -gt 0
        if ($hasGroupName) { return Find-ButtonByName $item $buttonName }
    }
    throw "Could not find $buttonName for group $groupName."
}

function Test-ContainsText($parent, [string]$text) {
    $nodes = $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    return @($nodes | Where-Object {
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and $_.Current.Name -eq $text
    }).Count -gt 0
}

function Find-ButtonByPrefix($parent, [string]$prefix) {
    $buttons = $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button))
    foreach ($button in $buttons) {
        if ($button.Current.Name.StartsWith($prefix, [StringComparison]::Ordinal)) { return $button }
    }
    throw "Could not find button beginning with $prefix."
}

function Activate-Window($window, [string]$description) {
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    foreach ($attempt in 1..10) {
        try { $window.SetFocus() } catch { }
        [HoverTestWindow]::SetForegroundWindow($handle) | Out-Null
        Start-Sleep -Milliseconds 150
        if ([HoverTestWindow]::GetForegroundWindow() -eq $handle) { return }
    }
    throw "Could not bring $description to foreground for hover verification."
}

function Select-ListItem($list, [string]$name) {
    $item = Find-ListItem $list $name
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

function Find-ListItem($list, [string]$name) {
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($item in $items) {
        $texts = $item.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($text in $texts) {
            if ($text.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and $text.Current.Name -eq $name) {
                return $item
            }
        }
    }
    throw "Could not find list item $name."
}

function Get-CommandListNames($list) {
    $names = @()
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($item in $items) {
        $texts = $item.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $nameNode = @($texts | Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and $_.Current.Name -match '^测试命令 \d\d$'
        } | Select-Object -First 1)
        if ($nameNode.Count -gt 0) { $names += $nameNode[0].Current.Name }
    }
    return $names
}

function Get-CommandCardPoint($item) {
    $bounds = $item.Current.BoundingRectangle
    return [System.Drawing.Point]::new(
        [int][Math]::Round($bounds.X + [Math]::Min(145, $bounds.Width * 0.45)),
        [int][Math]::Round($bounds.Y + $bounds.Height / 2))
}

function Invoke-MouseDoubleClick($item) {
    $point = Get-CommandCardPoint $item
    [HoverTestWindow]::LeftClick($point.X, $point.Y)
    Start-Sleep -Milliseconds 80
    [HoverTestWindow]::LeftClick($point.X, $point.Y)
}

function Invoke-MouseDrag($sourceItem, [System.Drawing.Point]$destination, [int]$holdAtDestinationMilliseconds = 120) {
    $source = Get-CommandCardPoint $sourceItem
    [HoverTestWindow]::LeftDown($source.X, $source.Y)
    Start-Sleep -Milliseconds 40
    foreach ($step in 1..10) {
        $x = [int][Math]::Round($source.X + (($destination.X - $source.X) * $step / 10))
        $y = [int][Math]::Round($source.Y + (($destination.Y - $source.Y) * $step / 10))
        [HoverTestWindow]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 35
    }
    Start-Sleep -Milliseconds $holdAtDestinationMilliseconds
    foreach ($attempt in 1..4) {
        [HoverTestWindow]::LeftUp($destination.X, $destination.Y)
        Start-Sleep -Milliseconds 100
    }
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
        $commandText = if ($_ -eq 5) {
            "Write-Output 'GROUP_SERVICE_READY'; while (`$true) { Start-Sleep -Milliseconds 200 }"
        } elseif ($_ -eq 6) {
            "Write-Error 'GROUP_TEST_FAILURE'; exit 9"
        } elseif ($_ -eq 7) {
            "Write-Output 'STREAM_FIRST'; Start-Sleep -Seconds 2; Write-Output 'STREAM_SECOND'"
        } elseif ($_ -eq 8) {
            "1..10005 | ForEach-Object { Write-Output ('BOUNDARY_' + `$_) }"
        } elseif ($_ -eq 9) {
            "1..120 | ForEach-Object { Write-Output ('FOLLOW_' + `$_); Start-Sleep -Milliseconds 20 }; Start-Sleep -Seconds 2; Write-Output 'FOLLOW_TAIL'"
        } else {
            "Write-Output 'SCROLL_TEST_{0:D2}'" -f $_
        }
        [ordered]@{
            Id = [guid]::NewGuid().ToString()
            Name = ('测试命令 {0:D2}' -f $_)
            Purpose = '滚动定位检查'
            Command = $commandText
            WorkingDirectory = $sampleDirectory
            Kind = if ($_ -eq 5) { 1 } else { 0 }
        }
    })
    $workspace = [ordered]@{
        SchemaVersion = 2
        Projects = @(
            [ordered]@{
                Id = [guid]::NewGuid().ToString()
                Name = '滚动测试项目'
                Directory = $sampleDirectory
                Commands = $commands
                Groups = @(
                    [ordered]@{
                        Id = [guid]::NewGuid().ToString()
                        Name = '同时冒烟分组'
                        ExecutionMode = 0
                        CommandIds = @($commands[0].Id, $commands[1].Id)
                    },
                    [ordered]@{
                        Id = [guid]::NewGuid().ToString()
                        Name = '顺序冒烟分组'
                        ExecutionMode = 1
                        CommandIds = @($commands[2].Id, $commands[3].Id)
                    },
                    [ordered]@{
                        Id = [guid]::NewGuid().ToString()
                        Name = '失败监督分组'
                        ExecutionMode = 0
                        CommandIds = @($commands[4].Id, $commands[5].Id)
                    }
                )
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
                Groups = @()
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
    $orderBeforeGestures = Get-SavedCommandNames $workspacePath
    $quickClickItem = Find-ListItem $list '测试命令 02'
    $quickClickPoint = Get-CommandCardPoint $quickClickItem
    [HoverTestWindow]::LeftClick($quickClickPoint.X, $quickClickPoint.Y)
    Start-Sleep -Milliseconds 380
    if ($selectedName.Current.Name -ne '测试命令 02') { throw 'A quick card click did not select its command.' }
    if (((Get-SavedCommandNames $workspacePath) -join '|') -ne ($orderBeforeGestures -join '|')) {
        throw 'A quick card click unexpectedly reordered commands.'
    }

    Invoke-MouseDoubleClick (Find-ListItem $list '测试命令 03')
    $commandEditor = Wait-For {
        $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $commandEditorCondition)
    } 'command editor opened by double-click'
    if ((Find-ById $commandEditor 'HeadingText').Current.Name -ne '编辑命令') {
        throw 'Double-click opened the command editor in the wrong mode.'
    }
    (Find-ButtonByName $commandEditor '取消').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { $null -eq $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $commandEditorCondition) } 'double-click editor to close' | Out-Null

    $checkCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, '勾选 测试命令 03')
    $check = Find-ById $main 'CommandList'
    $check = $check.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $checkCondition)
    $checkBounds = $check.Current.BoundingRectangle
    $checkX = [int][Math]::Round($checkBounds.X + $checkBounds.Width / 2)
    $checkY = [int][Math]::Round($checkBounds.Y + $checkBounds.Height / 2)
    [HoverTestWindow]::LeftClick($checkX, $checkY)
    Start-Sleep -Milliseconds 80
    [HoverTestWindow]::LeftClick($checkX, $checkY)
    Start-Sleep -Milliseconds 250
    if ($null -ne $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $commandEditorCondition)) {
        throw 'Double-clicking a batch checkbox opened the command editor.'
    }
    Write-Output 'PASS: a quick click only selects, a card double-click opens edit mode, and the batch checkbox is excluded.'

    $groupsBeforeDrag = @((Get-Content -LiteralPath $workspacePath -Raw | ConvertFrom-Json).Projects[0].Groups | ForEach-Object {
        $_.CommandIds -join ','
    }) -join '|'
    $dragCheckCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, '勾选 测试命令 04')
    $dragCheck = $list.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $dragCheckCondition)
    $dragCheck.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $dragSource = Find-ListItem $list '测试命令 04'
    $listBounds = $list.Current.BoundingRectangle
    $dragDestination = [System.Drawing.Point]::new(
        [int][Math]::Round($listBounds.X + $listBounds.Width * 0.45),
        [int][Math]::Round($listBounds.Bottom - 12))
    Invoke-MouseDrag $dragSource $dragDestination 1800
    Wait-For {
        try {
            $names = Get-SavedCommandNames $workspacePath
            [Array]::IndexOf($names, '测试命令 04') -ge 6
        } catch { $false }
    } 'mouse drag order to persist beyond the initial viewport' | Out-Null
    $orderAfterDrag = Get-SavedCommandNames $workspacePath
    if ($selectedName.Current.Name -ne '测试命令 04') { throw 'Dragged command did not remain selected.' }
    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
    $dragCheck = Wait-For {
        $list.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $dragCheckCondition)
    } 'checked dragged command to be realized again'
    if ($dragCheck.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        throw 'Dragging a checked command lost its batch selection.'
    }
    $dragCheck.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $groupsAfterDrag = @((Get-Content -LiteralPath $workspacePath -Raw | ConvertFrom-Json).Projects[0].Groups | ForEach-Object {
        $_.CommandIds -join ','
    }) -join '|'
    if ($groupsAfterDrag -ne $groupsBeforeDrag) { throw 'Global command dragging changed a command group member order.' }
    Write-Output 'PASS: native mouse dragging reorders live, auto-scrolls beyond the viewport, persists once, and leaves group order unchanged.'

    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0)
    Start-Sleep -Milliseconds 250
    $cancelOrder = (Get-SavedCommandNames $workspacePath) -join '|'
    $cancelSource = Find-ListItem $list '测试命令 03'
    $cancelSourcePoint = Get-CommandCardPoint $cancelSource
    $cancelTarget = Find-ListItem $list '测试命令 01'
    $cancelTargetBounds = $cancelTarget.Current.BoundingRectangle
    [HoverTestWindow]::LeftDown($cancelSourcePoint.X, $cancelSourcePoint.Y)
    Start-Sleep -Milliseconds 40
    [HoverTestWindow]::SetCursorPos(
        [int][Math]::Round($cancelTargetBounds.X + $cancelTargetBounds.Width * 0.45),
        [int][Math]::Round($cancelTargetBounds.Top + 10)) | Out-Null
    Start-Sleep -Milliseconds 180
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 120
    [HoverTestWindow]::LeftUp(
        [int][Math]::Round($cancelTargetBounds.X + $cancelTargetBounds.Width * 0.45),
        [int][Math]::Round($cancelTargetBounds.Top + 10))
    Start-Sleep -Milliseconds 300
    if (((Get-SavedCommandNames $workspacePath) -join '|') -ne $cancelOrder) {
        throw 'Esc during command dragging changed the saved order.'
    }
    if (((Get-SavedCommandNames $workspacePath) -join '|') -ne ($orderAfterDrag -join '|')) {
        throw 'Esc during command dragging did not restore the order from before that drag.'
    }
    Write-Output 'PASS: pressing Esc cancels a live command reorder and restores the original order.'

    $saveFailureOrder = (Get-SavedCommandNames $workspacePath) -join '|'
    $workspaceLock = [System.IO.FileStream]::new(
        $workspacePath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::None)
    try {
        $failureSource = Find-ListItem $list '测试命令 02'
        $failureTarget = Find-ListItem $list '测试命令 03'
        $failureTargetBounds = $failureTarget.Current.BoundingRectangle
        $failureDestination = [System.Drawing.Point]::new(
            [int][Math]::Round($failureTargetBounds.X + $failureTargetBounds.Width * 0.45),
            [int][Math]::Round($failureTargetBounds.Bottom - 8))
        Invoke-MouseDrag $failureSource $failureDestination 180
        $saveFailureCondition = [System.Windows.Automation.AndCondition]::new(@(
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, '配置未保存'),
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Window)))
        $saveFailureDialog = Wait-For {
            $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $saveFailureCondition)
        } 'save failure prompt after command drag'
        $saveFailureButton = Wait-For {
            $buttons = $saveFailureDialog.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Button))
            if ($buttons.Count -gt 0) { $buttons[0] }
        } 'save failure confirmation button'
        $saveFailureButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-For {
            $names = Get-CommandListNames $list
            $names.Count -ge 3 -and $names[0] -eq '测试命令 01' -and
                $names[1] -eq '测试命令 02' -and $names[2] -eq '测试命令 03'
        } 'command order rollback after save failure' | Out-Null
    }
    finally {
        $workspaceLock.Dispose()
    }
    if (((Get-SavedCommandNames $workspacePath) -join '|') -ne $saveFailureOrder) {
        throw 'A failed drag save changed the workspace file.'
    }
    Write-Output 'PASS: a failed drag save reports the error and restores both the visible and persisted order.'

    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0)
    Start-Sleep -Milliseconds 250
    Select-ListItem $list '测试命令 05'
    (Find-ById $main 'RunButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For {
        $candidate = Find-ById $main 'OutputCommandList'
        $null -ne $candidate -and -not $candidate.Current.IsOffscreen
    } 'service output page for running edit guard' | Out-Null
    $navigation.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0)
    Start-Sleep -Milliseconds 200
    Invoke-MouseDoubleClick (Find-ListItem $list '测试命令 05')
    Start-Sleep -Milliseconds 300
    if ($null -ne $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $commandEditorCondition)) {
        throw 'Double-clicking a running command opened the editor.'
    }
    (Find-ById $main 'StopButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For {
        (Find-ById $main 'RunStateText').Current.Name.StartsWith('已停止', [StringComparison]::Ordinal) -or
            (Find-ById $main 'RunStateText').Current.Name.StartsWith('已强制停止', [StringComparison]::Ordinal)
    } 'service to stop after running edit guard' | Out-Null
    Write-Output 'PASS: double-click editing remains disabled while a command is running.'

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
        (Get-TextValue (Find-ById $main 'OutputTextBox')).Contains('标准输出  SCROLL_TEST_01')
    } 'first batch command output' | Out-Null
    Select-ListItem $outputSelector '测试命令 02'
    if ($logName.Current.Name -ne '测试命令 02') { throw 'Output selector did not change the selected command.' }
    Wait-For {
        (Get-TextValue (Find-ById $main 'OutputTextBox')).Contains('标准输出  SCROLL_TEST_02')
    } 'second batch command output' | Out-Null
    $navigation.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if ($selectedName.Current.Name -ne '测试命令 02') { throw 'Command management did not retain the output page selection.' }
    (Find-ById $main 'OutputPageButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Find-ById $main 'OverviewPageButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { (Find-ById $main 'OverviewFinishedCountText').Current.Name -eq '3' } 'session overview result count' | Out-Null
    (Find-ById $main 'OutputPageButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $projects = Find-ById $main 'ProjectList'
    Select-ListItem $projects '第二个测试项目'
    if ($outputSelector.Current.IsOffscreen) { throw 'Switching projects left the output page.' }
    if ($logName.Current.Name -ne '选择命令以查看输出') { throw 'Switching projects kept a command from the previous project selected.' }
    Select-ListItem $projects '滚动测试项目'
    Select-ListItem $outputSelector '测试命令 01'
    Wait-For {
        (Get-TextValue (Find-ById $main 'OutputTextBox')).Contains('标准输出  SCROLL_TEST_01')
    } 'preserved output after project switch' | Out-Null

    (Find-ById $main 'GroupsPageButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $groupList = Wait-For {
        $candidate = Find-ById $main 'GroupList'
        if ($null -ne $candidate -and -not $candidate.Current.IsOffscreen) { $candidate }
    } 'visible command group list'
    (Find-ById $main 'AddGroupButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $groupEditorCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'TalosDesk · 命令分组')
    $groupEditor = Wait-For {
        $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $groupEditorCondition)
    } 'command group editor'
    (Find-ById $groupEditor 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('临时分组')
    $groupChecks = $groupEditor.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::CheckBox))
    if ($groupChecks.Count -eq 0) { throw 'Command group editor has no selectable commands.' }
    $groupChecks[0].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    (Find-ButtonByName $groupEditor '保存分组').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { Test-ContainsText $groupList '临时分组' } 'created command group' | Out-Null

    (Find-GroupButton $groupList '临时分组' '编辑').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $groupEditor = Wait-For {
        $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $groupEditorCondition)
    } 'command group editor for edit'
    (Find-ById $groupEditor 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('临时分组已编辑')
    (Find-ButtonByName $groupEditor '保存分组').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { Test-ContainsText $groupList '临时分组已编辑' } 'edited command group' | Out-Null

    (Find-GroupButton $groupList '临时分组已编辑' '删除').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deleteDialog = Wait-For {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, '删除分组')
        $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    } 'delete command group confirmation'
    (Find-ButtonByPrefix $deleteDialog '是').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { -not (Test-ContainsText $groupList '临时分组已编辑') } 'deleted command group' | Out-Null
    Write-Output 'PASS: command groups can be created, edited, and deleted from the isolated workspace.'

    (Find-GroupButton $groupList '同时冒烟分组' '运行分组').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { -not $outputSelector.Current.IsOffscreen -and $logName.Current.Name -eq '测试命令 01' } 'output page after parallel group run' | Out-Null
    (Find-ById $main 'GroupsPageButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Find-GroupButton $groupList '顺序冒烟分组' '运行分组').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { -not $outputSelector.Current.IsOffscreen -and $logName.Current.Name -eq '测试命令 04' } 'last sequential group command' | Out-Null
    Wait-For {
        (Get-TextValue (Find-ById $main 'OutputTextBox')).Contains('标准输出  SCROLL_TEST_04')
    } 'sequential group output' | Out-Null
    Write-Output 'PASS: parallel and sequential command groups open the output page and run their configured members.'

    (Find-ById $main 'GroupsPageButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Find-GroupButton $groupList '失败监督分组' '运行分组').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $failureDialog = Wait-For {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, '分组执行失败')
        $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    } 'parallel group failure prompt'
    (Find-ButtonByPrefix $failureDialog '确定').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Select-ListItem $outputSelector '测试命令 06'
    Wait-For {
        (Get-TextValue (Find-ById $main 'OutputTextBox')).Contains('诊断输出  ')
    } 'stderr diagnostic label' | Out-Null
    if ((Find-ById $main 'CopySelectedOutputButton').Current.IsEnabled) { throw 'Copy-selected button is enabled before a selection exists.' }
    Select-ListItem $outputSelector '测试命令 05'
    Wait-For {
        $state = Find-ById $main 'OutputRunStateText'
        $state.Current.Name.StartsWith('已停止', [StringComparison]::Ordinal) -or $state.Current.Name.StartsWith('已强制停止', [StringComparison]::Ordinal)
    } 'service cleanup after parallel group failure' | Out-Null
    Write-Output 'PASS: a failed parallel-group test stops its service, shows a failure prompt, and labels stderr as diagnostic output.'

    $navigation.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 50)
    Start-Sleep -Milliseconds 250
    Select-ListItem $list '测试命令 07'
    (Find-ById $main 'RunButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { (Get-TextValue (Find-ById $main 'OutputTextBox')).Contains('STREAM_FIRST') } 'first streaming output' | Out-Null
    $terminal = Find-ById $main 'OutputTextBox'
    $textPattern = $terminal.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $partialRange = $textPattern.DocumentRange.Clone()
    $partialRange.MoveEndpointByRange([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End,
        $partialRange, [System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start)
    [void]$partialRange.MoveEndpointByUnit([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End,
        [System.Windows.Automation.Text.TextUnit]::Character, 5)
    $partialRange.Select()
    $selectedBeforeAppend = $textPattern.GetSelection()[0].GetText(-1)
    if ($selectedBeforeAppend.Length -ne 5) { throw "Partial terminal selection has length $($selectedBeforeAppend.Length), expected 5." }
    Wait-For { (Find-ById $main 'CopySelectedOutputButton').Current.IsEnabled } 'copy-selected button to enable' | Out-Null
    Wait-For { (Get-TextValue $terminal).Contains('STREAM_SECOND') } 'second streaming output' | Out-Null
    $selectedAfterAppend = $textPattern.GetSelection()[0].GetText(-1)
    if ($selectedAfterAppend -ne $selectedBeforeAppend) { throw 'Appending output changed the active terminal selection.' }

    $terminal.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    Wait-For {
        $selection = $textPattern.GetSelection()[0].GetText(-1)
        $selection.Contains('STREAM_FIRST') -and $selection.Contains('STREAM_SECOND')
    } 'Ctrl+A terminal selection' | Out-Null
    $collapsedRange = $textPattern.DocumentRange.Clone()
    $collapsedRange.MoveEndpointByRange([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End,
        $collapsedRange, [System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start)
    $collapsedRange.Select()
    Wait-For { -not (Find-ById $main 'CopySelectedOutputButton').Current.IsEnabled } 'copy-selected button to disable' | Out-Null
    Write-Output 'PASS: terminal output supports partial and cross-line selection, Ctrl+A, and preserves selection while new output arrives.'

    (Find-ById $main 'ClearOutputButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { (Get-TextValue $terminal).Length -eq 0 } 'cleared terminal output' | Out-Null
    $navigation.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 50)
    Start-Sleep -Milliseconds 250
    Select-ListItem $list '测试命令 08'
    (Find-ById $main 'RunButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { (Get-TextValue (Find-ById $main 'OutputTextBox')).Contains('BOUNDARY_10005') } 'bounded terminal output' | Out-Null
    $boundedText = Get-TextValue (Find-ById $main 'OutputTextBox')
    $boundedLines = $boundedText -split "`r?`n"
    if ($boundedLines.Count -ne 10000) { throw "Terminal retained $($boundedLines.Count) lines, expected 10000." }
    if (-not $boundedLines[0].EndsWith('BOUNDARY_6', [StringComparison]::Ordinal) -or
        -not $boundedLines[-1].EndsWith('BOUNDARY_10005', [StringComparison]::Ordinal)) {
        throw "Terminal buffer bounds are incorrect: '$($boundedLines[0])' ... '$($boundedLines[-1])'."
    }
    Write-Output 'PASS: clearing output resets the terminal and the rendered view remains synchronized with the 10,000-line buffer.'

    $navigation.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 55)
    Start-Sleep -Milliseconds 250
    Select-ListItem $list '测试命令 09'
    (Find-ById $main 'RunButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { (Get-TextValue (Find-ById $main 'OutputTextBox')).Contains('FOLLOW_40') } 'scroll-follow output' | Out-Null
    $followTerminal = Find-ById $main 'OutputTextBox'
    $terminalScroll = $followTerminal.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    Wait-For { $terminalScroll.Current.VerticallyScrollable } 'terminal vertical scrolling' | Out-Null
    $terminalScroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0)
    Wait-For { (Get-TextValue $followTerminal).Contains('FOLLOW_120') } 'output while terminal is scrolled up' | Out-Null
    if ($terminalScroll.Current.VerticalScrollPercent -gt 5) {
        throw "Terminal auto-scrolled while reading older output: $($terminalScroll.Current.VerticalScrollPercent)%."
    }
    $terminalScroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
    Wait-For { (Get-TextValue $followTerminal).Contains('FOLLOW_TAIL') } 'output after returning to terminal bottom' | Out-Null
    if ($terminalScroll.Current.VerticalScrollPercent -lt 95) {
        throw "Terminal did not resume following at the bottom: $($terminalScroll.Current.VerticalScrollPercent)%."
    }
    Write-Output 'PASS: scrolling away pauses terminal follow, and returning to the bottom resumes it.'

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
