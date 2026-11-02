#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [switch]$AllowDesktop
)

$ErrorActionPreference = 'Stop'
if (-not $AllowDesktop) { throw 'This check opens an application window. Supply -AllowDesktop only on an available test desktop.' }
if (-not $IsWindows) { throw 'A Windows test desktop is required.' }
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Evidence directory must not already exist.' }
$inspection = & (Join-Path $PSScriptRoot 'Test-Package.ps1') -ArchivePath $ArchivePath
$zipPath = (Resolve-Path -LiteralPath $ArchivePath).Path
$zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$root = Join-Path ([IO.Path]::GetTempPath()) ('TalosDesk-PackageStartup-' + [guid]::NewGuid().ToString('N'))
$process = $null
$report = [ordered]@{ Status='Failed'; SourceCommit=$env:GITHUB_SHA; ArchiveName=[IO.Path]::GetFileName($zipPath); Scope='Packaged EXE startup and idle exit only; not command execution or manual acceptance.'; ForcedCleanup=$false }
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
    $configuration | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $workspace -Encoding utf8
    $before = (Get-FileHash -LiteralPath $workspace).Hash
    $profile = 'Package startup ' + [guid]::NewGuid().ToString('N')
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $package 'TalosDesk.App.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $root
    foreach ($argument in @('--workspace', $workspace, '--profile-label', $profile)) { $start.ArgumentList.Add($argument) }
    $start.Environment['DOTNET_ROOT'] = Join-Path $root 'absent-dotnet'
    $start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.Environment['PATH'] = [Environment]::GetFolderPath('System') + ';' + $env:WINDIR
    $process = [Diagnostics.Process]::Start($start)
    $null = $process.Handle # 在退出前持有本次创建的确切进程，不按名称查询或清理。
    $report.ProcessId = $process.Id
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($process.HasExited) { throw "Packaged EXE exited before its window appeared: $($process.ExitCode)" }
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and $process.MainWindowTitle -eq "TalosDesk · $profile") { break }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed -lt [TimeSpan]::FromSeconds(30))
    if ($process.MainWindowHandle -eq [IntPtr]::Zero -or $process.MainWindowTitle -ne "TalosDesk · $profile") { throw 'Packaged window was not observed within 30 seconds.' }
    $report.WindowObserved = $true
    $report.SdkPathExcluded = $true
    # 仅观察空闲启动，不把此等待视作已验收命令执行或全部界面功能。
    Start-Sleep -Seconds 2
    if ($process.HasExited) { throw 'Packaged EXE exited during idle observation.' }
    if (Test-Path -LiteralPath $marker) { throw 'A saved command ran during startup.' }
    if (-not $process.CloseMainWindow()) { throw 'The packaged main window refused an idle close request.' }
    if (-not $process.WaitForExit(15000)) { throw 'Packaged EXE did not exit within 15 seconds.' }
    $report.ExitCode = $process.ExitCode
    if ($process.ExitCode -ne 0) { throw 'Packaged EXE did not exit normally.' }
    if (-not (Test-Path -LiteralPath ($workspace + '.window-settings.json'))) { throw 'Normal window-state persistence was not observed.' }
    if ((Get-FileHash -LiteralPath $workspace).Hash -ne $before) { throw 'Startup or idle exit rewrote the workspace.' }
    if ((Test-Path -LiteralPath $marker) -or @(Get-ChildItem -LiteralPath $root -Filter run.json -File -Recurse).Count) { throw 'Startup created a command run.' }
    $report.WorkspaceUnchanged = $true
    $report.NoCommandRunObserved = $true
    $report.WindowStateSaved = $true
    $report.Status = 'Passed'
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
    if (Test-Path -LiteralPath $evidence) { $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'startup.json') -Encoding utf8 }
    if ($report.Status -eq 'Passed') {
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $fullRoot = [IO.Path]::GetFullPath($root)
        if (-not $fullRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($fullRoot) -notlike 'TalosDesk-PackageStartup-*') { throw 'Refusing unexpected cleanup path.' }
        Remove-Item -LiteralPath $fullRoot -Recurse -Force
    }
}
[pscustomobject]$report
