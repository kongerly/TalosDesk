#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ApplicationPath,

    [switch]$SkipLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$appPath = (Resolve-Path -LiteralPath $ApplicationPath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf) -or
    [IO.Path]::GetFileName($appPath) -ne 'TalosDesk.App.exe') {
    throw "请把 -ApplicationPath 指向 TalosDesk.App.exe：$ApplicationPath"
}

$demoRoot = Join-Path ([IO.Path]::GetTempPath()) ('TalosDesk-Demo-' + [guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $demoRoot 'sample-project'
New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
$workspacePath = Join-Path $demoRoot 'workspace.json'

$workspace = [ordered]@{
    SchemaVersion = 3
    Projects = @(
        [ordered]@{
            Id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
            Name = 'TalosDesk 演示项目'
            Directory = $projectRoot
            Commands = @(
                [ordered]@{
                    Id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1'
                    Name = '成功任务'
                    Purpose = '输出合成文本并正常退出'
                    Command = "Write-Output 'sample-success'"
                    WorkingDirectory = $projectRoot
                    Kind = 0
                    EnvironmentVariables = @()
                }
                [ordered]@{
                    Id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2'
                    Name = '失败任务'
                    Purpose = '演示诊断输出和非零退出'
                    Command = "Write-Error 'sample-failure'; exit 2"
                    WorkingDirectory = $projectRoot
                    Kind = 0
                    EnvironmentVariables = @()
                }
                [ordered]@{
                    Id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3'
                    Name = '合成长运行服务'
                    Purpose = '演示启动、输出和停止'
                    Command = "Write-Output 'sample-service-started'; while (`$true) { Start-Sleep -Seconds 1 }"
                    WorkingDirectory = $projectRoot
                    Kind = 1
                    EnvironmentVariables = @()
                }
                [ordered]@{
                    Id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4'
                    Name = '输出完成'
                    Purpose = '顺序分组第二步'
                    Command = "Write-Output 'sample-complete'"
                    WorkingDirectory = $projectRoot
                    Kind = 0
                    EnvironmentVariables = @()
                }
            )
            Groups = @(
                [ordered]@{
                    Id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa0'
                    Name = '顺序演示'
                    ExecutionMode = 1
                    CommandIds = @(
                        'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1'
                        'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4'
                    )
                }
            )
        }
    )
}

$workspace | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $workspacePath -Encoding utf8
Write-Host "演示工作区：$workspacePath"
Write-Host '脚本只创建隔离工作区，不会自动运行命令。'

if ($SkipLaunch) {
    return
}

$quotedWorkspacePath = '"' + $workspacePath.Replace('"', '""') + '"'
$arguments = @('--workspace', $quotedWorkspacePath, '--profile-label', '演示')
Start-Process -FilePath $appPath -ArgumentList $arguments | Out-Null
