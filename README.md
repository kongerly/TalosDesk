# TalosDesk

TalosDesk 是面向 Windows 的本地项目运行工作台：保存常用命令，一键运行，并集中查看状态和输出。

当前已发布版本为 `v0.1.0` 公开预览版。支持 Windows 11 x64，提供包含 .NET 10 运行时的便携包；该版本已完成本机发布包启动检查和三轮本地工具服务运行验收，可从 [GitHub Releases](https://github.com/kongerly/TalosDesk/releases/tag/v0.1.0) 下载。当前开发分支在此基础上新增了项目内命令分组。

## 主要功能

- 添加本地源码项目或独立工具目录。
- 保存任务和持续运行服务的名称、用途、命令及工作目录；命令编辑器提供 PowerShell 语法着色、多行缩进、成对符号和安全的路径、可执行程序及环境变量补全。
- 运行、停止、重启、复制、删除和排序命令；可双击命令卡片编辑，并直接拖动调整顺序。
- 勾选多条命令批量启动，并在不同项目之间切换而不中止任务。
- 在项目内创建可复用的命令分组，并选择同时执行或按顺序执行。
- 分别显示标准输出、诊断输出（stderr）、开始时间、状态和退出结果；输出区可像终端一样拖选任意字符或跨行范围，使用 `Ctrl+C` 或右键复制，使用 `Ctrl+A` 选择全部。
- 按项目、命令和运行批次保存 stdout/stderr，可在输出页查看历史批次、调整保留上限并清理已结束批次。
- 保存本机工作区，并通过 JSON 文件预览、导入和导出配置。
- 关闭应用时，若有任务仍在运行，可选择停止任务后退出或返回应用。

典型流程：

```text
选择项目 → 选择单条命令或命令分组 → 运行 → 查看输出和结果 → 按需停止或重启
```

工具目录不必包含源码。例如，可以把 `llama-server.exe` 所在目录添加为项目，再添加服务命令：

```powershell
.\llama-server.exe -m "D:\Models\your-model.gguf"
```

TalosDesk 只停止由当前应用实例启动并确认归属的进程，不会按进程名称结束其他终端中运行的程序。

## 系统要求

- Windows 11 x64。其他 Windows 版本可能可以运行，但 v0.1 不作兼容承诺。
- [PowerShell 7](https://learn.microsoft.com/powershell/) 已安装，并且 `pwsh.exe` 位于应用启动时继承的 `PATH` 中。
- 被运行项目需要的 Go、Python、uv 等工具需自行安装。

便携包已经包含 .NET 10 和 Windows Desktop 运行时，不要求安装 .NET SDK。解压全部文件后运行 `TalosDesk.App.exe`。v0.1 未进行代码签名，Windows 可能显示信誉或安全提示。

## 配置与隐私

工作区默认保存在：

```text
%LOCALAPPDATA%\TalosDesk\workspace.json
```

日志默认保存在工作区文件旁的 `<工作区文件名>.logs` 目录；默认工作区对应 `%LOCALAPPDATA%\TalosDesk\workspace.json.logs`。输出页可选择其他保存文件夹，TalosDesk 会在所选文件夹内建立当前工作区专属的 `TalosDesk-<工作区标识>.logs` 子目录，并迁移已有历史日志和保留设置。更改位置时须先结束所有命令；也可恢复默认位置。实际路径始终显示在输出页。位置选择记录在工作区文件旁的 `<工作区文件名>.log-location.json`，不会写入工作区 JSON 或导出文件。指定 `--workspace <文件>` 时，位置记录也只属于该隔离工作区。

日志目录按项目、命令和批次 ID 分层，每批次的 `stdout.log`、`stderr.log` 和 `run.json` 均为明文。**程序输出可能包含令牌或其他敏感信息。**输出页可打开实际目录、清理所有已结束批次；关闭应用后也可手工删除实际日志目录。若迁移后提示旧目录未能删除，还需手工清理提示的旧路径。清空显示只影响当前界面，不删除磁盘日志。

每个工作区默认最多保存 500 MB、保留 30 天，可在输出页修改；设置保存在日志目录的 `settings.json`，不随工作区 JSON 导入导出。达到上限时先删除最早的已结束批次；若只剩运行中批次，则停止保存该批次后续输出并提示截断，命令仍继续运行。目录不可写时也不阻止命令运行，但会显示日志未保存或不完整。历史输出在界面显示最近 10,000 行，完整内容可在日志文件中查看。删除命令不会立即删除历史文件，文件仍受期限和容量规则约束。

桌面快捷方式会显式传入当前工作区文件路径，避免从不同 Windows 启动上下文打开时发生路径重定向；若直接运行 EXE，可使用 `--workspace <文件>` 指定同一工作区。

工作区和导出文件都是明文 JSON，其中包含项目路径、完整命令和分组成员关系。当前 schema 版本为 2；应用仍可读取 schema 1 工作区，并在下次保存或导出时升级。不要把密码、令牌或 API 密钥直接写入命令；请使用环境变量或外部凭据工具。分享导出文件前，应先用文本编辑器检查内容。

导入配置会先显示预览并处理冲突，不会自动运行其中的命令。应用启动时也不会自动运行已保存命令。

同一个工作区路径只允许一个 TalosDesk 实例；再次启动会在没有命令运行或保存操作时从磁盘重新加载工作区，再唤醒已打开的窗口。窗口侧栏会显示“正式工作区 · N 个项目”及实际配置路径。自动化回归使用 `--workspace <文件>` 和 `--profile-label 隔离测试` 显式隔离，测试窗口不会读取或修改正式工作区；如果配置文件被外部程序改写，当前窗口会停止保存并提示重新打开，避免用旧内存内容覆盖新配置。

## 已知限制

- 只支持不需要中途输入的非交互命令。
- 命令编辑器的补全只读取运行目录、`PATH`、`PATHEXT` 和环境变量，不执行输入内容，也不提供 cmdlet 参数语义补全；它不是交互终端。
- “运行中”只表示进程仍存在，不表示服务已经就绪。
- stderr 是程序诊断通道，不等于执行失败；Uvicorn 等程序会把普通 INFO 日志写入 stderr，TalosDesk 以进程退出结果判断最终状态。
- 顺序分组只支持任务命令；上一条任务成功完成后才会运行下一条，失败或停止会终止后续步骤。
- 同时执行分组会跳过已经运行、正在重启或被顺序分组预留的命令；任一成员运行失败时，会停止由该分组启动的其他命令并显示失败提示。
- 实时输出仍只在内存中保留最近 10,000 行；磁盘日志按上述上限轮转。历史批次的 stdout 和 stderr 在界面中分别查看。
- PowerShell 7 必须能从 `PATH` 找到。
- v0.1 是 Windows 11 x64 便携预览包，不提供安装程序、后台托盘或自动扫描项目功能。

## 从源码构建

仓库通过 `global.json` 锁定 .NET SDK。可以使用系统中相同版本的 SDK，也可以安装仓库本地 SDK：

```powershell
pwsh -NoProfile -File .\scripts\Install-Sdk.ps1
pwsh -NoProfile -File .\scripts\Test-Environment.ps1
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 build TalosDesk.slnx --configuration Release
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test TalosDesk.slnx --configuration Release
```

生成完整候选包：

```powershell
pwsh -NoProfile -File .\scripts\Publish.ps1
```

脚本会执行还原、Release 构建和自动化测试，再生成 `artifacts/TalosDesk-v0.1.0-win-x64.zip` 及对应 SHA-256 文件。

## 文档与许可

- [设计说明](docs/design.md)
- [开发与发布路线图](docs/roadmap.md)：当前分支收口与隐私保护、更新分发、就绪检查，以及后续多 Shell 的范围和验收条件。
- [v0.1.0 发布说明](docs/release-v0.1.0.md)

TalosDesk 使用 [MIT License](LICENSE)。发布包包含的 .NET 运行时许可信息随包提供。
