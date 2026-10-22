# TalosDesk

TalosDesk 是面向 Windows 的本地项目运行工作台：保存常用命令，一键运行，并集中查看状态和输出。

[示例与演示](examples/README.md) · [从源码构建](#从源码构建) · [常见问题](docs/faq.md) · [版本记录](docs/changelog/README.md)

> 安全维护（2026-10-22）：本地核实的 v0.1.0–v0.3.0 历史自包含包（含 v0.3.0-preview.1）嵌入 .NET 10.0.12，请停止继续分发这些旧包。更新系统 .NET 不会替换包内运行时。10.0.13 的本地重建尚未发布，详情见[安全维护说明](docs/security-maintenance-2026-10-22.md)。

已发布稳定版为 `v0.3.0`；当前源码为 `v0.3.1-rc.1 / Preview`，使用 schema 5，包含默认关闭的服务有限自动重启，尚未正式发布。变更方向见[路线图](docs/roadmap.md)。

## 主要功能

- 管理本地源码项目或独立工具目录，保存任务和服务命令及普通、敏感环境变量。
- 运行、停止、重启及批量启动命令；项目内分组支持同时执行或按成功顺序执行任务。
- 编辑器支持 PowerShell 着色、多行缩进和本地路径、程序、环境变量补全；双击编辑、拖动排序。
- 分别查看 stdout、stderr、退出结果与历史日志，支持范围选择、复制、保留上限和清理。
- 服务可选回环 TCP 探测，项目概览分别统计进程运行与探测通过数量。
- 通过 JSON 预览、导入和导出配置；导入和应用启动均不自动运行命令。
- 最小化到系统托盘，恢复窗口大小与位置；提供可关闭的更新检查和本地崩溃诊断。
- v0.3.1 候选：独立服务非零退出后最多追加 3 次重启，等待 2、4、8 秒；分组不启用，详见[设计说明](docs/design.md#有限自动重启)。

## 安装与启动

需要 Windows 11 x64，以及位于 `PATH` 中的 [PowerShell 7](https://learn.microsoft.com/powershell/)。Go、Python、uv 等目标项目工具需自行安装。便携包包含 .NET 和 Windows Desktop 运行时，不需要安装 SDK；其他 Windows 版本尚无兼容承诺，发布包未签名。

[官方发布页](https://github.com/kongerly/TalosDesk/releases)保留历史附件；选择下载前请阅读上方安全维护提示。可用新包发布后，下载完整 ZIP 和同名 `.sha256` 文件，核对以下两行输出中的哈希：

```powershell
(Get-FileHash .\TalosDesk-v<版本>-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
Get-Content .\TalosDesk-v<版本>-win-x64.sha256
```

将 `<版本>` 替换为实际版本。完整解压到新的应用目录，运行 `TalosDesk.App.exe`。SHA-256 验证文件完整性，不能替代发布者签名。

默认工作区位于 `%LOCALAPPDATA%\TalosDesk\workspace.json`。需要独立工作区时显式指定：

```powershell
.\TalosDesk\TalosDesk.App.exe --workspace "$env:TEMP\TalosDesk-Demo\workspace.json" --profile-label "演示"
```

## 快速上手

1. 添加项目或工具目录，保存名称、命令与工作目录；会结束的脚本选“任务”，持续运行的进程选“服务”。
2. 在“命令管理”选择命令后运行，或勾选多条批量启动；在“运行输出”查看结果。
3. 按需停止或重启。切换项目或页面不会中止命令；最小化后可从 Windows 通知区域或折叠菜单恢复窗口。

使用[示例工作区](examples/README.md)可以验证成功、失败和服务停止。导入时先确认预览，再把占位目录改为本机存在的目录；导入只保存配置，不运行命令或安装依赖。

源码构建后可启动合成命令演示：

```powershell
pwsh -NoProfile -File .\examples\demo\Start-Demo.ps1 -ApplicationPath .\src\TalosDesk.App\bin\Release\net10.0-windows\TalosDesk.App.exe
```

脚本创建独立临时工作区，不自动运行命令。关闭应用并确认命令退出后，可删除脚本打印的临时目录。

## 数据与使用边界

工作区中的路径、命令、用途、分组和普通环境变量是明文。敏感环境变量使用 Windows DPAPI `CurrentUser` 保存；分享导出只保留变量名和待填写状态，不能代替完整本机备份。日志也为明文，脱敏无法保证覆盖全部秘密。数据位置、保留与清理见[隐私说明](docs/privacy.md)。

- 仅支持无需中途输入的非交互命令；补全不执行输入内容，也不提供完整终端功能。
- “运行中”表示进程存活；TCP 可连接不能证明业务健康或监听者归属。stderr 是诊断通道，最终结果按进程退出状态判断。
- 停止和退出只管理本次应用启动且能确认归属的进程；退出确认期间暂停自动重启，返回应用后继续原预算。
- 日志默认最多 500 MB、保留 30 天；实时和历史显示最近 10,000 行。“清空显示”不删除磁盘日志，删除命令也不会立即删除历史文件。
- 更新检查默认关闭，只提示并打开官方发布页，不自动安装；本地诊断默认开启，可关闭或清理，不自动上传。
- 同一工作区只允许一个实例。配置被外部修改时停止保存并提示重新打开；手工编辑 JSON 须遵守[设计契约](docs/design.md)。

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

脚本执行还原、Release 构建、全量测试和自包含发布，按 `Directory.Build.props` 版本生成 `artifacts/` 下的 ZIP 与 SHA-256 文件。构建按警告即错误执行；本地打包不等于正式发布。验证方法见[测试说明](docs/testing.md)。

## 手工升级

1. 下载符合安全基线的新包并校验，停止命令、退出旧应用。
2. 备份本机工作区、日志及需要保留的旁文件；可分享导出不保留敏感值。
3. 解压到新目录，以原来的明确 `--workspace` 路径启动。回退旧版时使用兼容的工作区副本：当前 schema 5 不能交给只支持 schema 4 的 v0.3.0。

## 开发文档与许可

[设计说明](docs/design.md) · [路线图](docs/roadmap.md) · [贡献指南](CONTRIBUTING.md) · [测试说明](docs/testing.md) · [版本记录](docs/changelog/README.md)

TalosDesk 使用 [MIT License](LICENSE)。第三方与运行时许可随发布包提供，另见[第三方声明](THIRD-PARTY-NOTICES.md)。
