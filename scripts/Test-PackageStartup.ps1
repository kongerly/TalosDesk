#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [switch]$AllowDesktop,
    [switch]$ExerciseCommands,
    [switch]$ExerciseGroups
)

$ErrorActionPreference = 'Stop'
if (-not $AllowDesktop) { throw 'This check opens an application window. Supply -AllowDesktop only on an available test desktop.' }
if (-not $IsWindows) { throw 'A Windows test desktop is required.' }
if ($ExerciseCommands -and $ExerciseGroups) { throw 'Choose either command or group checks for each isolated workspace.' }
$requiresPowerShell = $ExerciseCommands -or $ExerciseGroups
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Evidence directory must not already exist.' }
$inspection = & (Join-Path $PSScriptRoot 'Test-Package.ps1') -ArchivePath $ArchivePath
$zipPath = (Resolve-Path -LiteralPath $ArchivePath).Path
$zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$root = Join-Path ([IO.Path]::GetTempPath()) ('TalosDesk-PackageStartup-' + [guid]::NewGuid().ToString('N'))
$process = $null
$report = [ordered]@{ Status='Failed'; SourceCommit=$env:GITHUB_SHA; ArchiveName=[IO.Path]::GetFileName($zipPath); Scope='Packaged EXE startup and idle exit only; not command execution or manual acceptance.'; ForcedCleanup=$false }
if ($ExerciseCommands) { $report.Scope = 'Packaged EXE task success, failure and service stop; not complete manual acceptance.' }
if ($ExerciseGroups) { $report.Scope = 'Packaged EXE sequential group success, failure boundary and parallel member stop; not complete manual acceptance.' }
try {
    $report.Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($zipStream)).ToLowerInvariant()
    if ($report.Sha256 -ne $inspection.Sha256) { throw 'Archive changed after static inspection.' }
    $zipStream.Position = 0
    New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
    New-Item -ItemType Directory -Path $evidence -ErrorAction Stop | Out-Null
    $package = Join-Path $root 'app'
    $archive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try { [IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $package) }
    finally { $archive.Dispose() }
    $workspace = Join-Path $root 'workspace.json'
    $marker = Join-Path $root 'unexpected-command.txt'
    # schema 4 可由两条交付线读取；命令若被意外运行只写合成标记并退出。
    $configuration = @{ SchemaVersion=4; Projects=@(@{ Id=[guid]::NewGuid(); Name='Isolated package startup'; Directory=$root; Groups=@(); Commands=@(@{
        Id=[guid]::NewGuid(); Name='Must remain idle'; Purpose='Synthetic startup check'; WorkingDirectory=$root; Kind=0;
        Command="Set-Content -LiteralPath './unexpected-command.txt' -Value 'synthetic'"; EnvironmentVariables=@()
    }) }) }
    if ($requiresPowerShell) { $pwshPath = (Get-Command pwsh -CommandType Application -ErrorAction Stop).Source }
    if ($ExerciseCommands) {
        foreach ($sample in @(
            @{ Name='Package success'; Kind=0; Command="Write-Output 'PACKAGE_SUCCESS_中文'; [Console]::Error.WriteLine('PACKAGE_STDERR'); exit 0" },
            @{ Name='Package failure'; Kind=0; Command="Write-Output 'PACKAGE_FAILURE'; exit 7" },
            @{ Name='Package service'; Kind=1; Command="[IO.File]::WriteAllText((Join-Path (Get-Location) 'service.pid'), [string]`$PID); Write-Output 'PACKAGE_SERVICE_READY'; while (`$true) { Start-Sleep -Milliseconds 100 }" }
        )) {
            $configuration.Projects[0].Commands += @{ Id=[guid]::NewGuid(); Name=$sample.Name; Kind=$sample.Kind; Command=$sample.Command; Purpose='Synthetic package command check'; WorkingDirectory=$root; EnvironmentVariables=@() }
        }
    }
    if ($ExerciseGroups) {
        foreach ($sample in @(
            @{ Name='Sequence first'; Kind=0; Command="Set-Content './sequence-first.pid' ([string]`$PID); Set-Content './sequence.txt' 'first'; Write-Output 'SEQUENCE_FIRST'; exit 0" },
            @{ Name='Sequence second'; Kind=0; Command="if ((Get-Content './sequence.txt' -Raw).Trim() -ne 'first') { exit 11 }; if (Get-Process -Id ([int](Get-Content './sequence-first.pid' -Raw)) -ErrorAction SilentlyContinue) { exit 12 }; Set-Content './sequence.txt' 'second'; Write-Output 'SEQUENCE_SECOND'; exit 0" },
            @{ Name='Sequence failure'; Kind=0; Command="Write-Output 'SEQUENCE_FAILURE'; exit 9" },
            @{ Name='Sequence skipped'; Kind=0; Command="Set-Content './unexpected-tail.txt' 'unexpected'; exit 0" },
            @{ Name='Parallel first'; Kind=0; Command="[IO.File]::WriteAllText((Join-Path (Get-Location) 'parallel-first.pid'), [string]`$PID); Write-Output 'PARALLEL_FIRST_READY'; while (`$true) { Start-Sleep -Milliseconds 100 }" },
            @{ Name='Parallel second'; Kind=1; Command="[IO.File]::WriteAllText((Join-Path (Get-Location) 'parallel-second.pid'), [string]`$PID); Write-Output 'PARALLEL_SECOND_READY'; while (`$true) { Start-Sleep -Milliseconds 100 }" }
        )) {
            $configuration.Projects[0].Commands += @{ Id=[guid]::NewGuid(); Name=$sample.Name; Kind=$sample.Kind; Command=$sample.Command; Purpose='Synthetic package group check'; WorkingDirectory=$root; EnvironmentVariables=@() }
        }
        foreach ($sample in @(
            @{ Name='Package sequential success'; Mode=1; Members=@('Sequence first','Sequence second') },
            @{ Name='Package sequential failure'; Mode=1; Members=@('Sequence failure','Sequence skipped') },
            @{ Name='Package parallel stop'; Mode=0; Members=@('Parallel first','Parallel second') }
        )) {
            $memberIds = @($sample.Members | ForEach-Object { $memberName=$_; ($configuration.Projects[0].Commands | Where-Object Name -eq $memberName).Id })
            $configuration.Projects[0].Groups += @{ Id=[guid]::NewGuid(); Name=$sample.Name; ExecutionMode=$sample.Mode; CommandIds=$memberIds }
        }
    }
    $configuration | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $workspace -Encoding utf8
    $before = (Get-FileHash -LiteralPath $workspace).Hash
    # 启动参数最多允许 40 字符；保留完整随机标识并使用短前缀。
    $profile = 'CI ' + [guid]::NewGuid().ToString('N')
    $report.ExpectedWindowTitle = "TalosDesk · $profile"
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $package 'TalosDesk.App.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $root
    foreach ($argument in @('--workspace', $workspace, '--profile-label', $profile)) { $start.ArgumentList.Add($argument) }
    $start.Environment['DOTNET_ROOT'] = Join-Path $root 'absent-dotnet'
    $start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.Environment['PATH'] = [Environment]::GetFolderPath('System') + ';' + $env:WINDIR
    if ($requiresPowerShell) { $start.Environment['PATH'] += ';' + (Split-Path -Parent $pwshPath) }
    $process = [Diagnostics.Process]::Start($start)
    $null = $process.Handle # 在退出前持有本次创建的确切进程，不按名称查询或清理。
    $report.ProcessId = $process.Id
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($process.HasExited) { throw "Packaged EXE exited before its window appeared: $($process.ExitCode)" }
        $process.Refresh()
        $report.ObservedWindowHandle = $process.MainWindowHandle.ToInt64()
        $report.ObservedWindowTitle = $process.MainWindowTitle
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and $process.MainWindowTitle -eq $report.ExpectedWindowTitle) { break }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed -lt [TimeSpan]::FromSeconds(30))
    if ($process.MainWindowHandle -eq [IntPtr]::Zero -or $process.MainWindowTitle -ne $report.ExpectedWindowTitle) { throw 'Packaged window was not observed within 30 seconds.' }
    $report.WindowObserved = $true
    $report.SdkPathExcluded = $true
    # 仅观察空闲启动，不把此等待视作已验收命令执行或全部界面功能。
    Start-Sleep -Seconds 2
    if ($process.HasExited) { throw 'Packaged EXE exited during idle observation.' }
    if (Test-Path -LiteralPath $marker) { throw 'A saved command ran during startup.' }
    if (@(Get-ChildItem -LiteralPath $root -Filter run.json -File -Recurse).Count) { throw 'Startup created a command run.' }
    $report.NoStartupCommandRunObserved = $true
    if (-not $requiresPowerShell) { $report.NoCommandRunObserved = $true }
    if ($ExerciseCommands) {
        $report.CommandChecks = & (Join-Path $PSScriptRoot '../tests/PackageCommandRegression.ps1') -ApplicationProcess $process -RootDirectory $root -Commands $configuration.Projects[0].Commands -PowerShellPath $pwshPath -AllowDesktop
    }
    if ($ExerciseGroups) {
        $report.GroupChecks = & (Join-Path $PSScriptRoot '../tests/PackageCommandRegression.ps1') -ApplicationProcess $process -RootDirectory $root -Commands $configuration.Projects[0].Commands -PowerShellPath $pwshPath -Groups $configuration.Projects[0].Groups -AllowDesktop
    }
    if (-not $process.CloseMainWindow()) { throw 'The packaged main window refused an idle close request.' }
    if (-not $process.WaitForExit(15000)) { throw 'Packaged EXE did not exit within 15 seconds.' }
    $report.ExitCode = $process.ExitCode
    if ($process.ExitCode -ne 0) { throw 'Packaged EXE did not exit normally.' }
    if (-not (Test-Path -LiteralPath ($workspace + '.window-settings.json'))) { throw 'Normal window-state persistence was not observed.' }
    if ((Get-FileHash -LiteralPath $workspace).Hash -ne $before) { throw 'Startup or idle exit rewrote the workspace.' }
    if (Test-Path -LiteralPath $marker) { throw 'The unselected command ran.' }
    $report.UnselectedCommandRemainedIdle = $true
    $expectedRuns = if ($ExerciseGroups) { 5 } elseif ($ExerciseCommands) { 3 } else { 0 }
    $report.CommandRunCount = @(Get-ChildItem -LiteralPath $root -Filter run.json -File -Recurse).Count
    if ($report.CommandRunCount -ne $expectedRuns) { throw 'Unexpected command run count.' }
    $report.WorkspaceUnchanged = $true
    $report.WindowStateSaved = $true
    $report.Status = 'Passed'
} catch {
    $report.Failure = $_.Exception.Message
    throw
} finally {
    if ($process) {
        if (-not $process.HasExited) {
            $report.ForcedCleanup=$true
            $process.Kill()
            if (-not $process.WaitForExit(5000)) { throw 'Owned package process cleanup did not complete.' }
        }
        $process.Dispose()
    }
    $zipStream.Dispose()
    $reportName = if ($ExerciseGroups) { 'groups.json' } elseif ($ExerciseCommands) { 'commands.json' } else { 'startup.json' }
    if (Test-Path -LiteralPath $evidence) { $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence $reportName) -Encoding utf8 }
    if ($report.Status -eq 'Passed') {
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $fullRoot = [IO.Path]::GetFullPath($root)
        if (-not $fullRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($fullRoot) -notlike 'TalosDesk-PackageStartup-*') { throw 'Refusing unexpected cleanup path.' }
        Remove-Item -LiteralPath $fullRoot -Recurse -Force
    }
}
[pscustomobject]$report
