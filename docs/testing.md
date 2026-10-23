# 隔离测试说明

本文供贡献者复现验证，不包含维护者的逐次验收报告。测试只使用独立临时目录、合成命令和占位凭据，不读取日常工作区，也不按进程名结束其他程序。

## 构建与自动化

开发环境的安装步骤见[贡献指南](../CONTRIBUTING.md)。验证时先检查相关的 Core 行为，再检查桌面流程：

```powershell
pwsh -NoProfile -File .\scripts\Test-Environment.ps1
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test tests/TalosDesk.Core.Tests/TalosDesk.Core.Tests.csproj --configuration Release
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test tests/TalosDesk.App.Tests/TalosDesk.App.Tests.csproj --configuration Release
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 build TalosDesk.slnx --configuration Release
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test TalosDesk.slnx --configuration Release --no-build --no-restore
```

`.editorconfig` 和 `Directory.Build.props` 启用了“警告即错误”，例外必须在配置中说明理由。[Windows CI](https://github.com/kongerly/TalosDesk/actions/workflows/windows-ci.yml)会保存对应提交的构建与测试结果。测试数量、耗时和包哈希只属于当次运行，不写进长期指南。

## 回归入口

下表列出主要的测试入口及其覆盖范围。

| 入口 | 主要覆盖 |
| --- | --- |
| `WorkspaceStoreTests`、`WorkspaceSecurityTests`、`RecoveryConfigurationTests` | schema 兼容、严格 JSON、全零 ID、外部修改、保存回滚及安全导出 |
| `CommandRunnerTests`、`CommandRunEnvironmentTests` | 归属进程、停止/重启、环境注入及启动失败 |
| `CmdProcessTests`、`ShellConfigurationTests`、`CmdConfigurationEditorImportAndMixedGroup` | Cmd 引用与 UTF-8、中文/空格/特殊字符路径、批处理与 EXE、环境与脱敏、Ctrl+C/强停及独立进程隔离、schema 1–6、编辑/复制/预览/冲突/导出与混合分组 |
| `CommandRecoveryCoordinatorTests`、`CommandRecoveryProcessTests` | 预算、退避、取消、故障归属、真实 Job 与逐会话日志，以及 Core 公共入口的 Cmd 初次/重试会话 |
| `CommandGroupExecutionTests` | 顺序预留、同时分组持续监视及确切会话清理 |
| `RunLogStoreTests`、`StreamingOutputRedactorTests` | 历史尾部读取、损坏元数据容量、迁移、截断和脱敏 |
| `LogSearchTests`、`LogSearchFollowsDisplayedScopeWithoutBlocking` | 中文/字面/大小写、通道与最近 10,000 行、取消、WPF 查找路由/高亮/循环导航、实时追加与切换/清空失效、界面响应 |
| `MainWindowIntegrationTests` | 实际 WPF 控件路径、分组、导入预览、保存、探测、恢复、更新、窗口及托盘 |
| `DesktopTestHostTests`、`WpfTestHostTests` | STA 宿主、超时及清理失败边界 |
| 更新与诊断相关测试 | 固定网络响应、可控时间、渠道/缓存/限流、记录投影及独立崩溃宿主 |

可以按方法或主题筛选，并输出 TRX：

```powershell
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test tests/TalosDesk.Core.Tests/TalosDesk.Core.Tests.csproj --configuration Release --filter "FullyQualifiedName~Recovery"
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test tests/TalosDesk.App.Tests/TalosDesk.App.Tests.csproj --configuration Release --filter "FullyQualifiedName~MainWindowIntegrationTests" --logger "trx;LogFileName=desktop.trx" --results-directory artifacts/tests/desktop
```

竞争条件使用可控时钟、预检/创建/完成屏障和确切的会话身份，避免靠随机等待来碰撞。关键断言包括：原文件未被改写、失败不会留下虚假批次、旧回调不覆盖新状态、旧分组不停止后来的会话，以及停止/退出后不恢复重试。

日志迁移回归另覆盖暂存重命名错误 5/32 的恢复、持续失败的重试上限、其他错误立即返回，以及重试时目标被其他操作创建的保护。通过注入确切的文件系统错误固定复现，不依赖降低测试工作线程数；原始文件系统争用可用独立目录中的嵌套复制、立即重命名循环辅助调查，不能把循环未失败当作不存在问题的证明。

## 桌面宿主与截图

桌面集成测试共用专用的 STA 线程和 Dispatcher，各套依次串行执行。正常失败并清理成功后，可以继续下一套；如果窗口残留、清理失败或超时，就禁用宿主。共享线程卡死后无法在进程内安全恢复，应结束该次测试运行。

```powershell
$env:TALOSDESK_LAYOUT_CAPTURE_DIR = Join-Path $PWD 'artifacts/tests/layout'
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test tests/TalosDesk.App.Tests/TalosDesk.App.Tests.csproj --configuration Release --filter "FullyQualifiedName~WindowLayoutAndSettingsSurviveResizeAndReopen"
```

`TALOSDESK_NATIVE_TRAY_TEST=1` 可以为托盘测试增加原生 API 检查，默认使用替身。WPF 布局缩放、合成屏幕、浏览器替身和原生托盘 API 都不能替代真实鼠标、多屏/DPI、浏览器点击与干净客户端验收。CI 虚拟桌面可能限制实际窗口尺寸，需要结合截图和实际尺寸判断。

## 最终包检查

候选版本必须执行 `scripts/Publish.ps1`，它会完成还原、Release 构建、全量测试和自包含发布。脚本会替换当前副本 `artifacts/` 中的同版本输出；如果要保留历史产物，应使用独立副本。包能启动并不表示完整操作链已经通过。

需要核对 ZIP 与随附 SHA-256、应用版本与渠道、包内运行时、许可及未签名状态。包内不得包含工作区、用户日志、凭据、PDB、TRX 或测试宿主。然后从最终 ZIP 完整解压的目录，以明确的临时工作区启动：

```powershell
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('TalosDesk-Manual-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$workspace = Join-Path $testRoot 'workspace.json'
# 将 EXE 路径替换为本次解压目录
.\TalosDesk\TalosDesk.App.exe --workspace $workspace --profile-label '隔离测试'
```

| 场景 | 必查结果 |
| --- | --- |
| 基础执行 | 成功、非零失败、并发、重启、Ctrl+C 及超时强停；只清理本次归属进程 |
| 分组与恢复 | 失败清理只作用于本次会话；有限恢复 2/4/8 秒预算不被 TCP 通过重置；等待中停止、重启、退出确认与返回不重复创建 |
| 配置 | 三个示例先预览，确认后不运行、不安装、不探测；缺失敏感值阻止启动；冲突替换和保存失败保留规则正确 |
| 日志与异常 | 历史批次隔离、迁移、损坏元数据、目录不可写；日志失败不改变命令结果；强制结束后归属清理且重开不自动运行 |
| TCP | 延迟监听、超时、失联、恢复、停止和快速重启；端口预占不能被解释为进程归属 |
| 更新与诊断 | 真实联网成功或可理解失败；点击才打开合法发布页；诊断开关、清理、损坏文件、不可写路径及合成秘密检查 |
| 桌面与兼容 | 同工作区二次启动、托盘恢复、退出取消、停止失败、窄窗口、多屏及真实 DPI；干净客户端不以开发机代替 |

UI/AppDomain 异常注入使用独立测试宿主，不能破坏测试运行器。强制结束只针对本轮确认了 PID、可执行路径和归属的实例。记录具体故障方式时，要区分目录初始化失败、磁盘写满、强制结束和断电；这些结果不能互相替代。

跨账户 DPAPI 验证需要两个隔离的 Windows 账户：A 保存合成敏感值并运行，B 只接收该临时工作区，确认原密文保留但无法启动；重新录入后可以运行，导出仍只有待填写状态。模拟解密失败不能替代跨账户实测。

结束后先关闭应用并确认合成命令退出，再删除确切的临时目录。交付说明要分别列出实际检查、未执行项和已知限制；截图、TRX、哈希清单、PID 与操作流水只保存在本地忽略目录，归档约定见[贡献指南](../CONTRIBUTING.md#文档与本地资料)。
