# TalosDesk

TalosDesk 是面向 Windows 的本地项目运行工作台：保存常用命令，一键运行，并集中查看状态和输出。

[示例与演示](examples/README.md) · [从源码构建](#从源码构建) · [常见问题](docs/faq.md) · [版本记录](docs/changelog/README.md)

> **安全维护（2026-10-22）**：经本地核实，v0.1.0–v0.3.0 的历史自包含包（含 v0.3.0-preview.1）都嵌入了 .NET 10.0.12。这些包使用自带运行时，更新系统 .NET 不会替换它，因此请停止继续分发。10.0.13 的本地重建尚未发布，详情见[安全维护说明](docs/security-maintenance-2026-10-22.md)。

已发布的稳定版是 `v0.3.0`。当前源码在 `v0.3.1-rc.1 / Preview` 基础上包含未发布改动：服务有限自动重启、当前日志搜索与 Windows Cmd；保存使用 schema 6。应用版本号尚未重新分配，不代表新的正式候选或发布。后续方向见[路线图](docs/roadmap.md)。

## 主要功能

- 管理本地源码项目或独立工具目录，保存任务和服务命令，以及普通、敏感两类环境变量。
- 运行、停止、重启及批量启动命令；项目内的分组可以同时执行，也可以按成功顺序执行任务。
- 编辑器支持 PowerShell 着色、多行缩进，以及本地路径、程序和环境变量补全；双击编辑，拖动排序。
- 分别查看 stdout、stderr、退出结果与历史日志，支持范围选择、复制、保留上限和清理。
- 在运行输出页按 Ctrl+F 搜索当前显示或所选历史批次的所选通道，支持中文、关键词高亮、匹配计数和前后导航，范围限最近 10,000 行。
- 命令可选择默认 PowerShell 7 或 Windows Cmd；Cmd 首版支持 UTF-8 非交互单行命令，详见[Cmd 范围](docs/design.md#windows-cmd)。
- 服务可选开启回环 TCP 探测；项目概览分别统计进程运行数和探测通过数。
- 通过 JSON 预览、导入和导出配置；导入和应用启动都不会自动运行命令。
- 最小化到系统托盘，恢复窗口大小与位置；提供可关闭的更新检查和本地崩溃诊断。
- v0.3.1 候选：独立服务以非零码退出后，最多再重启 3 次，分别等待 2、4、8 秒；分组不启用，详见[设计说明](docs/design.md#有限自动重启)。

## 安装与启动

TalosDesk 面向 Windows 11 x64。默认 PowerShell 命令需要位于 `PATH` 中的 [PowerShell 7](https://learn.microsoft.com/powershell/)；当前源码新增的 Cmd 命令使用系统 `cmd.exe`。Go、Python、uv 等目标项目要用的工具需要自行安装。便携包自带 .NET 和 Windows Desktop 运行时，不需要安装 SDK；其他 Windows 版本尚无兼容承诺，发布包未签名。

[官方发布页](https://github.com/kongerly/TalosDesk/releases)保留历史附件，下载前请先阅读上方的安全维护提示。等新包发布后，下载完整的 ZIP 和同名 `.sha256` 文件，核对下面两行输出中的哈希是否一致：

```powershell
(Get-FileHash .\TalosDesk-v<版本>-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
Get-Content .\TalosDesk-v<版本>-win-x64.sha256
```

把 `<版本>` 换成实际版本号。将 ZIP 完整解压到一个新的应用目录，运行 `TalosDesk.App.exe`。SHA-256 只能验证文件完整性，不能替代发布者签名。

默认工作区位于 `%LOCALAPPDATA%\TalosDesk\workspace.json`。如果需要独立工作区，可以显式指定：

```powershell
.\TalosDesk\TalosDesk.App.exe --workspace "$env:TEMP\TalosDesk-Demo\workspace.json" --profile-label "演示"
```

## 快速上手

1. 添加项目或工具目录，保存名称、命令与工作目录。会结束的脚本选“任务”，持续运行的进程选“服务”。
2. 在“命令管理”中选择命令后运行，或勾选多条批量启动；结果在“运行输出”中查看。
3. 按需停止或重启。切换项目或页面不会中止命令；最小化后可从 Windows 通知区域或折叠菜单恢复窗口。

[示例工作区](examples/README.md)可以验证成功、失败和服务停止。导入时先确认预览，再把占位目录改成你本机存在的目录。导入只保存配置，不会运行命令或安装依赖。

源码构建后可以启动合成命令演示：

```powershell
pwsh -NoProfile -File .\examples\demo\Start-Demo.ps1 -ApplicationPath .\src\TalosDesk.App\bin\Release\net10.0-windows\TalosDesk.App.exe
```

脚本会创建独立的临时工作区，不会自动运行命令。关闭应用并确认命令退出后，可以删除脚本打印的临时目录。

## 数据与使用边界

工作区中的路径、命令、用途、分组和普通环境变量都是明文。敏感环境变量用 Windows DPAPI `CurrentUser` 保存。分享导出只保留变量名和待填写状态，不能代替完整的本机备份。日志也是明文，脱敏无法保证覆盖所有秘密。数据位置、保留与清理见[隐私说明](docs/privacy.md)。

- 只支持无需中途输入的非交互命令；补全不会执行输入内容，也不提供完整终端功能。
- “运行中”只表示进程存活；TCP 能连接不能证明业务健康，也不能证明监听者属于该命令。stderr 是诊断通道，最终结果按进程退出状态判断。
- 停止和退出只管理本次应用启动且能确认归属的进程。退出确认期间暂停自动重启，返回应用后继续使用原来的预算。
- 日志默认最多 500 MB、保留 30 天；实时和历史显示最近 10,000 行。“清空显示”不会删除磁盘日志，删除命令也不会立即删除历史文件。
- 更新检查默认关闭，只提示并打开官方发布页，不自动安装。本地诊断默认开启，可以关闭或清理，不会自动上传。
- 同一工作区只允许一个实例。配置被外部修改时会停止保存并提示重新打开；手工编辑 JSON 必须遵守[设计契约](docs/design.md)。

## 从源码构建

仓库通过 `global.json` 和 `eng/dotnet-sdk.json` 锁定 SDK 与运行时：

```powershell
pwsh -NoProfile -File .\scripts\Install-Sdk.ps1
pwsh -NoProfile -File .\scripts\Test-Environment.ps1
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 build TalosDesk.slnx --configuration Release
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test TalosDesk.slnx --configuration Release
```

生成本地便携包：

```powershell
pwsh -NoProfile -File .\scripts\Publish.ps1
```

脚本会依次执行还原、Release 构建、全量测试和自包含发布，按 `Directory.Build.props` 中的版本在 `artifacts/` 下生成 ZIP 与 SHA-256 文件。构建按警告即错误执行；本地打包不等于正式发布。验证方法见[测试说明](docs/testing.md)。

## 手工升级

1. 下载符合安全基线的新包并校验，然后停止命令、退出旧应用。
2. 备份本机工作区、日志和需要保留的旁文件；可分享导出不保留敏感值。
3. 解压到新目录，用原来的明确 `--workspace` 路径启动。回退旧版时要使用兼容的工作区副本：当前 schema 6 不能交给只支持 schema 4 的 v0.3.0，也不能交给此前 schema 5 的 rc.1 构建。

## 开发文档与许可

[设计说明](docs/design.md) · [路线图](docs/roadmap.md) · [贡献指南](CONTRIBUTING.md) · [测试说明](docs/testing.md) · [版本记录](docs/changelog/README.md)

TalosDesk 使用 [MIT License](LICENSE)。第三方与运行时许可随发布包提供，另见[第三方声明](THIRD-PARTY-NOTICES.md)。
