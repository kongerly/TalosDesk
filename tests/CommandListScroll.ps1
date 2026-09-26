#requires -Version 7.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repoRoot = Split-Path -Parent $PSScriptRoot
$reviewRoot = Join-Path $repoRoot '.tools/command-scroll-review'
$sourceRoot = Join-Path $reviewRoot 'source'
$workspacePath = Join-Path $reviewRoot 'workspace.json'
$realWorkspace = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'TalosDesk/workspace.json'
$realHashBefore = if (Test-Path -LiteralPath $realWorkspace) { (Get-FileHash -LiteralPath $realWorkspace -Algorithm SHA256).Hash } else { $null }
$process = $null

function Wait-For([scriptblock]$probe, [string]$description) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        $result = & $probe
        if ($result) { return $result }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out waiting for $description"
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
    New-Item -ItemType Directory -Force -Path $sampleDirectory | Out-Null
    $commands = @(1..12 | ForEach-Object {
        [ordered]@{
            Id = [guid]::NewGuid().ToString()
            Name = ('测试命令 {0:D2}' -f $_)
            Purpose = '滚动定位检查'
            Command = "Write-Output 'SCROLL_TEST'"
            WorkingDirectory = $sampleDirectory
            Kind = 0
        }
    })
    $workspace = [ordered]@{
        SchemaVersion = 1
        Projects = @([ordered]@{
            Id = [guid]::NewGuid().ToString()
            Name = '滚动测试项目'
            Directory = $sampleDirectory
            Commands = $commands
        })
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
    $listCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CommandList')
    $list = Wait-For { $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCondition) } 'command list'
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
    Write-Output ('PASS: three small scroll steps moved command list {0:N1}%; selected {1} near the middle.' -f $after, $middleName)
}
finally {
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
