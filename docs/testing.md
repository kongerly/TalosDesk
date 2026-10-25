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

CI 先用独立临时副本和合成 SDK 命令检查打包流水线的失败中止与测试证据保留，再统一调用 `scripts/Publish.ps1` 完成还原、Release 构建、全量测试和自包含打包，不额外重复全量测试。测试 TRX 写入 `artifacts/tests/`，即使后续步骤失败也尝试保存。随后对本次 ZIP 运行静态核验与核验脚本的隔离回归，将 JSON 清单和回归输出另存为 `package-inspection-evidence`。CI 不上传 ZIP 到 Releases，不分配正式版本，也不替代最终 EXE 人工验收。

打包流水线的轻量回归可以独立运行，不调用真实 SDK 或桌面宿主：

```powershell
pwsh -NoProfile -File .\tests\PublishPipeline.Tests.ps1
```

流水线回归先加载归档模块，再定义压缩失败注入函数，避免首次模块自动加载覆盖替身；压缩失败、旧产物保留和恢复失败的断言仍独立执行。

## 回归入口

下表列出主要的测试入口及其覆盖范围。

| 入口 | 主要覆盖 |
| --- | --- |
| `WorkspaceStoreTests`、`WorkspaceSecurityTests`、`RecoveryConfigurationTests` | schema 兼容、严格 JSON、全零 ID、外部修改、保存回滚及安全导出 |
| `WorkspaceCommitRetryTests` | 保存替换错误 5/32 的有界重试、同一临时文件及字节摘要、重试前外部修改/首次创建冲突校验、取消、持续错误的重试上限与其他错误立即失败、临时文件清理和调用方对象保留 |
| `CommandRunnerTests`、`CommandRunEnvironmentTests` | 归属进程、停止/重启、环境注入及启动失败 |
| `JobProcessEnumerationTests` | 真实 Job 的小缓冲扩容枚举、129 个轻量子进程超过默认容量后的完整名单、父/子 PID 包含关系、独立 Job 隔离及停止后空列表 |
| `CmdProcessTests`、`ShellConfigurationTests`、`CmdConfigurationEditorImportAndMixedGroup` | Cmd 引用与 UTF-8、中文/空格/特殊字符路径、批处理与 EXE、环境与脱敏、Ctrl+C/强停及独立进程隔离、schema 1–6、编辑/复制/预览/冲突/导出与混合分组 |
| `CommandRecoveryCoordinatorTests`、`CommandRecoveryProcessTests` | 预算、退避、取消、故障归属、观察在停止确认期间失败后的归属释放、真实 Job 与逐会话日志，以及 Core 公共入口的 Cmd 初次/重试会话 |
| `TcpProbeTests` | 可控连接与时钟、启动期限和失败阈值、停止后的迟到结果、超时通知内同步停止，以及真实本机 IPv4/IPv6 监听的断开恢复 |
| `CommandGroupExecutionTests` | 顺序预留、同时分组持续监视及确切会话清理 |
| `RunLogStoreTests`、`StreamingOutputRedactorTests`、`CommandRunEnvironmentTests` | 历史尾部读取、损坏元数据容量、清理失败后容量重算、收尾重建缺失元数据及精确配额边界、日志流关闭故障后的双通道收尾/结果保留/迁移与清理、截断；含空白已知值、长重叠链及模式前缀交叠的跨块遮蔽、独立完整文本覆盖校验，以及真实双通道事件/内存/磁盘输出 |
| `CommandOutputPumpTests` | 未结束流的双通道脱敏失败提示、正常半行缓冲、完整且不重复的异常提示；长行代理对的独立 UTF-8 编码和重开历史、LF/跨块 CRLF 不多记空行、保留真实空行及末尾单独回车 |
| `LogSearchTests`、`LogSearchFollowsDisplayedScopeWithoutBlocking` | 中文/字面/大小写、通道与最近 10,000 行、取消、WPF 查找路由/高亮/循环导航、实时追加与切换/清空失效、界面响应 |
| `MainWindowIntegrationTests` | 实际 WPF 控件路径、日志收尾失败提示及新运行清除旧警告、分组、导入预览、保存、探测、恢复、更新、窗口及托盘 |
| `DesktopTestHostTests`、`WpfTestHostTests` | STA 宿主、超时及清理失败边界 |
| 更新与诊断相关测试 | 固定网络响应、可控时间、渠道/缓存/限流、记录投影及独立崩溃宿主 |

可以按方法或主题筛选，并输出 TRX：

```powershell
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test tests/TalosDesk.Core.Tests/TalosDesk.Core.Tests.csproj --configuration Release --filter "FullyQualifiedName~Recovery"
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test tests/TalosDesk.App.Tests/TalosDesk.App.Tests.csproj --configuration Release --filter "FullyQualifiedName~MainWindowIntegrationTests" --logger "trx;LogFileName=desktop.trx" --results-directory artifacts/tests/desktop
```

竞争条件使用可控时钟、预检/创建/完成屏障和确切的会话身份，避免靠随机等待来碰撞。关键断言包括：原文件未被改写、失败不会留下虚假批次、旧回调不覆盖新状态、旧分组不停止后来的会话，以及停止/退出后不恢复重试。

`RecoveryRealProcessWaitingStopPreservesFailureAndAllowsFreshRound` 使用真实 PowerShell/Cmd 会话与可控退避时钟，分别在 2/4/8 秒等待期间通过命令页或输出页停止按钮取消。退出前持有合成命令的确切进程句柄，确认真实非零退出与归属释放；取消后推进全部旧截止时间，验证不新增日志批次、不改写原失败元数据、停止入口禁用及手动运行建立零次重试的新轮次。该自动化仍不能替代最终 ZIP 的人工点击验收；只允许后台准备时可编译此测试，将 WPF 执行交给远端 CI。

观察与停止竞争回归在测试持有的真实 Job 已退出、停止锁尚未释放时同步注入观察异常，分别覆盖第一次等待确认退出和强停后的确认路径。断言原异常仍报告、输出收尾后释放占用、同一命令可重新启动且独立哨兵仍存活；不靠重复停止消除故障，也不把注入的确认路径当作 Ctrl+C 信号投递验收。未确认退出时保留占用的独立回归仍须通过。

Job 枚举回归分别覆盖初始容量 1 的少量父子进程，以及默认容量 128 下的 129 个轻量 Cmd 子进程，验证真实扩容后包含全部预期 PID、只停止目标 Job、独立哨兵仍存活及空组读取。容量边界用例让无窗口子进程等待各自的标准输入管道，不运行用户命令；就绪等待上限为 30 秒，失败也由测试持有的 Job 清理。运行前应为这些进程预留内存；单次边界回归不代表持续负载或资源、性能验收。

Cmd 进程回归的就绪等待默认仍为 15 秒。失败信息区分独立哨兵、Ctrl+C 子进程和忽略 Ctrl+C 的子进程阶段，记录耗时、完成状态或退出结果、归属释放与停止标记，以及最近 8 条已脱敏输出（每条含省略标记最多 256 字符）。强停用例通过 Cmd 启动随测试构建的 `TalosDesk.CtrlCTestHost`，由宿主报告启动与原生忽略 Ctrl+C 处理器注册成功，不在就绪窗口动态编译 C#；注册失败会立即报错。进程提前结束但未报告就绪时立即失败；已结束且已有就绪标记时仍按标记判定。等待失败本身不停止会话，清理由测试持有的确切会话完成；强停、子进程退出与独立哨兵存活的断言仍须通过。

失败快照在触发断言和清理会话前写入 TRX，供清理本身也出错时核对。合成控制台宿主还在测试自己的临时目录写入独立进度；就绪失败时读取并记录该进度，以区分宿主未推进与输出标记未送达，仍以原输出标记判定就绪。遇到就绪超时，先从对应 TRX 和 CI 日志核对阶段、是否提前退出、最后输出及归属状态。保留失败结果，再决定是否重跑同一提交；单次重跑成功不能证明是环境抖动，也不能作为放宽时限或跳过停止检查的依据。这些诊断只用于隔离合成命令，不应套用于收集用户工作区或真实命令输出。

提示窗口回归保留 10 秒响应期限，使用单调计时，并在失败时记录标题、正文标题、已处理和待处理提示数量及阶段，区分额外提示和预期提示响应超时。仅靠“未预期提示”这一合并错误，不能确定失败时是否真的出现了额外窗口；标题、内容、按钮、焦点及处理顺序的断言仍须通过。

日志迁移回归另覆盖暂存重命名错误 5/32 的恢复、持续失败的重试上限、其他错误立即返回，以及重试时目标被其他操作创建的保护。通过注入确切的文件系统错误固定复现，不依赖降低测试工作线程数；原始文件系统争用可用独立目录中的嵌套复制、立即重命名循环辅助调查，不能把循环未失败当作不存在问题的证明。

工作区保存回归通过注入提交错误校验重试和外部版本保护；不能为了短暂拒绝而跳过摘要检查、覆盖首次创建冲突或放宽测试断言。调查自然失败时可在独立目录比较同步/异步临时文件写入、关闭后替换及应用保存，记录确切操作和错误码。最小程序可复现并短暂重试成功，只能证明该类错误可能恢复，不能据此归因于特定软件或宣称所有权限问题已修复。

桌面保存竞争回归在提交边界用独立的存储实例真实替换合成工作区，保留外部配置的固定身份和字节，复用保存入口的有界替换重试。若合成替换失败，阶段诊断记录错误类型和错误码；替换成功、冲突提示、外部字节保留、调用方回滚、原摘要及重载恢复的断言均须通过，不能将故障安排失败当作产品已检测到外部修改。

## 桌面宿主与截图

桌面集成测试共用专用的 STA 线程和 Dispatcher，各套依次串行执行。正常失败并清理成功后，可以继续下一套；如果窗口残留、清理失败或超时，就禁用宿主。共享线程卡死后无法在进程内安全恢复，应结束该次测试运行。

```powershell
$env:TALOSDESK_LAYOUT_CAPTURE_DIR = Join-Path $PWD 'artifacts/tests/layout'
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test tests/TalosDesk.App.Tests/TalosDesk.App.Tests.csproj --configuration Release --filter "FullyQualifiedName~WindowLayoutAndSettingsSurviveResizeAndReopen"
```

`TALOSDESK_NATIVE_TRAY_TEST=1` 可以为托盘测试增加原生 API 检查，默认使用替身。WPF 布局缩放、合成屏幕、浏览器替身和原生托盘 API 都不能替代真实鼠标、多屏/DPI、浏览器点击与干净客户端验收。CI 虚拟桌面可能限制实际窗口尺寸，需要结合截图和实际尺寸判断。

## 最终包检查

候选版本必须执行 `scripts/Publish.ps1`，它会完成还原、Release 构建、全量测试和自包含发布。脚本在 `artifacts/` 中的独立暂存目录生成新发布目录、ZIP 与摘要，生成步骤全部成功后才替换同版本输出；SDK、测试、许可复制或压缩失败时保留旧产物。独占锁拒绝同一副本同时打包，也拒绝经由重解析点修改产物路径。

替换前把旧产物移入暂存备份，替换失败会尝试恢复；恢复受阻时保留暂存目录并报告确切路径，不覆盖障碍文件。应先确认没有打包进程，再比较该目录 `previous/` 中的旧发布目录、ZIP 和摘要，并完整保留现场后手工恢复。这不是跨文件原子事务，外部修改、强制结束或断电可能留下中间状态；替换期间不要使用或复制正在更新的产物。成功后的旧备份会清理，如果需要保留多次历史包仍应使用独立副本。TRX 不随失败清理。包能启动并不表示完整操作链已经通过。

需要核对 ZIP 与随附 SHA-256、应用版本与渠道、包内运行时、许可及未签名状态。包内不得包含工作区、用户日志、凭据、PDB、TRX 或测试宿主。静态信息通过不代表运行与停止验收通过。

先做不启动应用的静态检查，可使用仓库脚本，需要 PowerShell 7.2 或以上（将占位版本换为实际文件名）：

```powershell
pwsh -NoProfile -File .\scripts\Test-Package.ps1 -ArchivePath .\artifacts\TalosDesk-v<版本>-win-x64.zip -EvidenceDirectory .\artifacts\package-evidence-<唯一编号>
```

脚本默认从当前仓库读取期望版本、渠道与运行时；核对其他源码副本的包时应显式提供 `-ExpectedVersion`、`-ExpectedChannel` 和 `-ExpectedRuntimeVersion`。默认使用 ZIP 同名 `.sha256`，也可用 `-ChecksumPath` 指定。它校验摘要及文件名、Windows 不区分大小写的重复条目/越界路径、必需文件、私有/测试文件名、README、依赖及运行时声明，并直接读取 PE 版本与 App 程序集渠道。生成的 summary.json 和逐条 inventory.json 绑定同一个只读 ZIP 句柄；证据目录必须不存在，防止覆盖旧记录。少量 PE 临时副本读取后即清理，不会加载或执行。

构建标识带 `+` 元数据时，应用自身的依赖键保留完整版本，Core 项目引用的依赖键省略构建元数据；报告分别记录 `Version` 和 `CoreDependencyVersion`。README 及应用/Core 的 PE 版本仍校验完整标识，错误的 Core 依赖版本仍拒绝。

该脚本不证明依赖完整、内容无秘密、签名可信、schema 兼容或运行/停止正确，也不代替完整自动化和人工验收。schema 要对应源码与配置回归确认。此前维护包 README 带本地重建前言，不能通过正式标题校验；维护审阅分支已更新包内说明并重新构建核验，旧包仍不能以修改校验文件或改名来绕过。

脚本自身的回归使用指定合格包的 PE 字节，在独立临时目录生成结构夹具及损坏变体，不启动其中任何程序：

```powershell
pwsh -NoProfile -File .\tests\PackageInspection.Tests.ps1 -ArchivePath .\artifacts\TalosDesk-v<版本>-win-x64.zip
```

它检查正向证据生成，以及缺文件、路径/大小写冲突、私有文件、错误摘要/文件名、版本/渠道/运行时/依赖声明错误和已有证据目录保护。此独立 PowerShell 回归需要先有可通过静态检查的 ZIP，不包含在 `dotnet test` 中，由 CI 在打包后单独执行。

静态检查后继续按下面步骤进行最终 EXE 操作验收；仅需要后台准备时，将其明确保留为未执行。

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

## 集中人工验收安排

需要前台操作或额外客户端时，先整理包身份与自动化证据，再约定一次集中验收。后台文档准备不启动 WPF、托盘或浏览器，不切换窗口，也不启动虚拟机。既有自动化结果可用于对应源码/包；修改版本、渠道或重新打包后，仍须为新候选运行完整测试、Release 和 Publish，并重新核对最终 ZIP。

1. 为每个待验收包记录源码提交/维护补丁差异、ZIP 大小和 SHA-256、包内版本/渠道、schema、运行时、操作记录位置。schema 4 维护与 schema 6 新功能使用不同的解压目录及临时工作区；不按同名 rc.1 合并结果。
2. 先完成该包的基础执行、停止/强停、分组、日志迁移、异常结束归属清理及重开不自动运行。schema 4 不安排其不支持的自动重启或 Cmd；schema 6 另查搜索、Cmd 双通道与中文/空格路径、PowerShell 和恢复预算。只对本次确认 PID、路径与归属的合成进程操作。
3. 等待中停止要在非零退出后先确认显示“待重试”，在退避期限内直接点击“停止”，记录启动计数并等待越过原期限，确认没有新会话。若点击前已进入运行态，该次只能记为运行中停止，重新安排；不要修改生产退避参数来迁就手速。退出确认另查跨期限暂停、返回只恢复一次并保留预算、再次确认退出不新建会话。提前持有该宿主的可查询句柄，才能把退出码作为证据；仅看到窗口消失不能记为 0。
4. 在专用干净 Windows 11 x64 客户端上查无 SDK 启动、PowerShell 前置条件、升级保存及[兼容备份回退](upgrade-schema6.md)；跨账户 DPAPI 用两个隔离账户与合成值。多屏、真实 DPI、托盘鼠标与 Explorer 通知区域重建安排在专用测试会话，避免中断日常桌面。
5. 系统级挂起连接、不可写日志目录与实际写满分别在隔离测试环境进行，不占满日常磁盘、不变更日常网络。记录具体故障方式、超时/取消及配置/日志保留结果，不以模拟异常替代真实故障证据。
6. 逐项标记通过、失败或未执行，绑定确切包哈希。重新打包前的 EXE 结果、源码测试、包内程序集/WPF 回归和最后 ZIP 实机操作分列；包能启动不能补齐操作链。结束后确认归属进程释放，保留经过检查的本机证据，不上传真实工作区或日志。
