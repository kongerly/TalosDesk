# 隔离测试说明

最终包导入检查使用互斥 `-ExerciseImport` 模式，经 EXE 的原生文件选择器打开临时 schema 4 配置，核对只读预览包含项目、命令、分组和不自动运行提示。先取消并比较工作区原始字节，再重新打开并确认，检查原项目保留、导入项目/命令/分组与 TCP 配置保存、来源文件不变；正常退出不能再次改写已确认的工作区。合成命令标记、运行日志数量与专用回环 TCP 监听器检查导入期间无自动执行或探测连接。JSON 绑定 ZIP 哈希，明确区分确认前不变和确认后的正常保存；只上传摘要，不上传配置与日志。此检查仅在远端可用桌面显式传入 `-AllowDesktop`，不覆盖冲突替换/保留、敏感密文迁移、损坏配置、重新打开、托盘或完整人工验收。

最终包分组检查使用独立 `-ExerciseGroups` 模式，与命令模式互斥，仍需显式 `-AllowDesktop`。通过 EXE 的“运行分组”按钮检查三条路径：顺序成功组的后项核对前项标记且前项进程已退出；顺序失败组在退出码 9 后完成终止提示，后项无标记、无日志；同时组的持续任务被停止后，同组服务也退出，两个确切进程句柄均确认退出、日志为 Stopped、分组恢复可运行。最终恰有五份运行日志，未选中的哨兵保持空闲，应用正常退出且工作区不改写。远端 JSON 绑定 ZIP 哈希，仅上传摘要。该检查不涵盖同时组失败提示、跳过的独立会话、重新运行、导入、托盘或完整人工验收；已有源码层回归与最终包检查分别说明范围。

CI 另以 `-ExerciseCommands` 运行最终包命令检查：临时 schema 4 工作区包含成功任务、退出码 7 的失败任务、待停止服务和一个禁止运行的哨兵命令。通过该 EXE 窗口的 UI Automation 控件调用运行与停止，核对中文 stdout、独立 stderr、完成日志状态与退出码、停止后服务进程确已退出及界面恢复可运行状态。子进程 PATH 仅额外加入 PowerShell 7 所在目录；这不证明其他工具链或干净系统兼容。检查后正常关闭应用、保持工作区字节不变，JSON 证据绑定最终 ZIP 哈希。只在可用的测试桌面显式指定 `-AllowDesktop`；本机后台限制期间仅由远端 CI 执行。此自动化仍不覆盖强停子进程树、分组、导入、托盘和完整人工验收，不上传临时工作区或命令日志。

包启动检查使用不超过 40 字符的独立配置标签，与应用启动参数约束一致；窗口等待失败时，证据保留预期标题、最近观察到的窗口标题及句柄和失败原因，便于区分启动参数提示与主窗口。

CI 在静态包核验后调用 `scripts/Test-PackageStartup.ps1`，解压本次最终 ZIP，并用独立临时 schema 4 工作区启动其实际 EXE。子进程环境排除 SDK 的 PATH、指定不存在的 DOTNET_ROOT，检查独立窗口、空闲启动不运行合成命令、正常退出码、窗口状态保存及工作区字节不变；证据 JSON 绑定 ZIP 哈希。只管理脚本亲自启动并持有句柄的进程，强制清理不能计作正常退出通过。此检查会打开窗口，必须显式传入 `-AllowDesktop`；本机仅后台工作时不要运行。它不是干净 Windows 11 验收，也不覆盖命令执行、停止/强停、分组、导入、托盘或完整人工操作链。

日志位置失败阶段回归检查迁移提交后初始化失败仍保留新路径及位置记录，修复损坏设置后可从该位置恢复。WPF 的 `LogLocationFailureMessagesFollowCommittedState` 分别覆盖已有目标导致的迁移拒绝、提交后初始化失败，核对提示标题、路径、位置文件、可用标记及重试；已有目标中的合成文件必须保留。测试不使用原生文件选择器，只在远端 CI 执行桌面交互；最终 EXE 人工迁移与持续文件系统故障仍需单独验收。

日志初始化回归检查无效容量/保留天数不能覆盖最近有效的内存设置，失败时原设置文件保持不变；修复或删除文件后可在同一实例重试。设置文件缺失时，重试与新实例均采用默认 500 MB / 30 天，不沿用上一次的限制，也不因读取默认值而创建文件；已有有效历史仍可读取。测试仅使用临时目录中的合成设置和日志。

当前分支的维护范围、完整打包流水线与检查命令见[维护线审阅说明](maintenance-review.md)。CI 对本分支自己的源码执行 Core/App 全量测试、Release、Publish 和包静态核验，保存对应提交的证据；历史结果不移作当前证据。人工验收仍须针对最终包独立完成。

所有桌面和集成测试都应把工作区放在独立临时目录，并传入明确身份：

```powershell
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("TalosDesk-Manual-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$workspace = Join-Path $testRoot "workspace.json"
.\src\TalosDesk.App\bin\Release\net10.0-windows\TalosDesk.App.exe --workspace $workspace --profile-label "隔离测试"
```

测试结束后先关闭应用并确认合成命令已经退出，再删除这个明确的临时目录。不要把 `%LOCALAPPDATA%\TalosDesk\workspace.json` 用于自动化。

本文只写不随时间变化的内容：测试分层、回归覆盖范围、长期已知限制和未执行项。各次检查的通过数量与逐日实施记录已移入[变更记录归档](changelog/README.md)，发布包大小与校验值见[发布 tag](https://github.com/kongerly/TalosDesk/releases)；需要核对某个提交实际通过了多少用例时，请查该提交的 [CI 运行记录](https://github.com/kongerly/TalosDesk/actions/workflows/windows-ci.yml)。

## 日志设置失败阶段回归

`RunLogStoreTests` 分别锁住 `settings.json` 和过期批次的 `run.json`，核对写入失败保留旧设置，提交后清理失败保留新值并报告明确阶段及原始内部异常。WPF 的 `LogSettingsFailureMessagesFollowPersistedState` 另覆盖损坏位置记录导致的初始化失败，检查提示标题、正文、状态、实际磁盘设置、解除占用/修复后重试及命令未被自动运行。仅做后台验证时编译桌面回归，执行交给远端 CI；设置保存成功不代表历史清理或初始化也成功。

## 日志迁移目录别名回归

`RunLogStoreTests` 使用独立临时目录中的真实 Windows 目录联接，覆盖目标位于源内、源位于目标内，以及实际相同目录的两种路径拼写；拒绝时不得产生暂存目录或改写位置记录，同目录时保留原历史且重开可读。指向源内部的用例额外持有源文件的拒读句柄：若包含检查回归，复制会在递归目录前失败，避免测试本身反复复制。该夹具不修改全局链接权限、不运行界面，联接在临时目录递归清理前单独删除；不代表网络盘或并发链接替换已经验收。

## 日志丢弃失败回归

`FailedBeginRecountsRolledBackBatchBeforeOtherOutputExactlyFitsQuota` 先让历史与活动日志恰好占满配额，再锁住历史元数据，使新批次写入元数据后的清理真实失败。验证失败创建不留下新批次，回滚后原活动批次能写满实际剩余空间且不误报失败或截断，最后仍可迁移。容量断言来自临时目录的实际文件字节，不读取内部计数字段；旧历史已删除的输出不会因回滚恢复。

`RunLogStoreTests` 对 stdout/stderr 分别及同时注入真实文件流关闭故障，验证丢弃预建批次仍尝试关闭双通道、释放活动保护、删除批次并允许迁移，最后报告首个 IO/权限错误并标记写入失败，重复丢弃不再次关闭。另用真实只读共享句柄阻止删除，验证剩余字节仍参与容量限制、解除占用后可清理迁移；已消失的批次不重建且重新校准容量。关闭故障注入不能证明未刷新的输出可恢复，持续文件系统错误仍会使清理或容量重算失败。

## 导出硬链接回归

`WorkspaceSecurityTests.HardLinkExportReplacesOnlyAliasAndPreservesProtectedWorkspace` 在独立 NTFS 临时目录创建真实硬链接，验证导出替换别名后原工作区及合成 DPAPI 密文不变、导出只含待填写状态，后续修改导出文件不影响原文件；导出目标上拒绝删除共享的真实句柄还须阻止替换、保留两份原字节且清理临时文件，解除占用后可以再次导出。该回归约束导出继续采用临时文件替换，不能改成原地截断写入；不模拟并发更换目录、网络盘或其他文件系统。

## 工程规范护栏：警告即错误

仓库根 `.editorconfig` 统一缩进、换行、UTF-8 和命名约定（私有字段 `_camelCase`，其余成员 `PascalCase`），并逐条登记分析器严重级别；`Directory.Build.props` 只声明一次 `TreatWarningsAsErrors` 与 `EnforceCodeStyleInBuild`。`.editorconfig` 在展开阶段最后生效，可以逐条覆盖严重级别，因此例外必须连同理由写在文件内，不做隐式忽略。

验证护栏本身是否生效时，用一个临时源文件引入可空性违规（例如对 `string?` 字段取 `.Length`），确认 `dotnet build` 以 `CS8602` 失败，再删除该文件。

2026-10-06 护栏检查结论：Release 全量重建 0 警告、0 错误，完整自动化测试 0 失败、0 跳过，`git diff --check` 通过；故意引入的可空性违规按预期使构建失败。存量修复共 14 处，其中 `CommandEditorWindow` 的探测参数改为固定区域写入并同步以固定区域解析，消除了原先“写入与解析区域不一致”的往返缺陷；数量与日期显示使用 `CurrentCulture`，保持各区域原有行为。`DesktopTestHost` 的清理失败改为收集后随正文失败一并上报，不再从 `finally` 抛出，宿主不可用仍只由清理失败触发。未启用 `AnalysisLevel=latest-recommended`，未运行 `scripts/Publish.ps1`，未修改版本或制作候选包；未执行人工鼠标键盘、多屏或真实 DPI 验收。当次通过数量与基线数量见[变更记录归档](changelog/implementation-log-v0.1.1-to-v0.2.0.md#工程规范护栏检查2026-10-06)。

## 自动化层次

1. 相关 Core 测试验证纯逻辑、存储失败和边界条件。
2. App 测试使用 STA Dispatcher、模拟网络和浏览器替身验证桌面流程。
3. 全量验证运行 Release 构建及整个解决方案测试。
4. 候选包运行 `scripts/Publish.ps1`，再从最终解压目录执行桌面验收。

诊断测试只使用合成秘密，并检查 `.crashes` 中的 JSON 与临时文件都不包含该值。异常测试不得破坏测试运行器；真实进程退出场景应放在独立宿主进程中。跨账户 DPAPI 必须在两个隔离 Windows 账户中执行，模拟解密失败不能替代该结果。

候选包的人工步骤见 [v0.2.0 验收清单](acceptance-v0.2.0.md)。

## 桌面回归独立执行

`MainWindowIntegrationTests` 现在提供十个独立测试结果，可按方法名筛选；各套内部已有断言继续保留：

| 测试方法 | 覆盖内容 |
| --- | --- |
| `MessageDialogsPreserveInteractionAndLayout` | 提示、导入冲突、异常配置与运行中退出 |
| `TrayControllerPreservesWindowAndIconLifecycle` | 托盘注册、恢复、通知区域重建及资源释放 |
| `TrayCommandsContinueRunningAndExitSafely` | 隐藏期间命令与日志、取消退出、停止失败和重试 |
| `WindowLayoutAndSettingsSurviveResizeAndReopen` | 页面与弹窗布局、窗口状态保存与恢复 |
| `TcpProbeStatesFollowDesktopCommandLifecycle` | 探测编辑、状态、输出与项目汇总 |
| `ParallelGroupsKeepWatchingServicesAndOnlyCleanUpTheirOwnExecution` | 纯服务及混合分组持续监视、失败清理和后续会话隔离 |
| `HistoricalOutputLoadsWithoutBlockingAndRejectsStaleResults` | 历史后台读取、慢读取期间界面响应、切换与关闭后的旧结果隔离 |
| `WorkspaceSavesPreserveExternalChangesAndTrackTheirOwnRevision` | 保存前后外部修改、保存摘要归属、编辑回滚与重新加载恢复 |
| `StaleWorkspaceChecksDoNotDisableSaving` | 激活摘要检查跨越保存或重载后的旧结果失效，真实外部变化仍被拒绝 |
| `UpdateStatesDoNotBlockDesktopCommandAndOutputFlows` | 更新失败、限流、挂起期间的运行、重启、停止、输出和分组 |

例如，单独执行布局回归或执行全部十套并输出独立 TRX 结果：

```powershell
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test tests/TalosDesk.App.Tests/TalosDesk.App.Tests.csproj --configuration Release --filter "FullyQualifiedName~MainWindowIntegrationTests.WindowLayoutAndSettingsSurviveResizeAndReopen"
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test tests/TalosDesk.App.Tests/TalosDesk.App.Tests.csproj --configuration Release --filter "FullyQualifiedName~MainWindowIntegrationTests" --logger "trx;LogFileName=desktop.trx" --results-directory artifacts/tests/desktop
```

十套保持串行，在类初始化时创建专用 STA 线程、一个 `App(launchWorkspace: false)` 和持续运行的 Dispatcher，类清理时统一关闭。每套各有 120 秒外层等待上限，内部阶段期限保持原有设置；失败报告包含用例、阶段、耗时和原异常堆栈。更新流程只退出局部消息循环，不关闭共享 Dispatcher；等待异步关闭与请求取消时继续处理 Dispatcher 消息。

普通断言失败且清理成功后，宿主仍可执行下一套。清理失败、窗口残留或外层超时会使宿主不可用，后续测试直接失败并说明原因，不继续投递。共享 STA 线程卡死无法在进程内安全恢复；这时关闭宿主也可能超时，需要结束该次测试运行，不按名称结束其他系统进程。正常清理先关闭窗口、停止归属命令并完成日志收尾，再删除独立临时目录。

`DesktopTestHostTests` 使用不创建 `Application` 的 Dispatcher 宿主，验证异常后继续、局部消息循环结束后继续、超时拒绝后续投递，以及清理失败保留两处异常并禁用宿主。

`TALOSDESK_LAYOUT_CAPTURE_DIR` 继续控制提示与布局截图目录，分别筛选提示或布局测试即可生成对应截图。`TALOSDESK_NATIVE_TRAY_TEST=1` 仅为托盘控制器测试追加真实托盘 API 检查；默认使用替身，不要求通知区域可用。这些检查不替代真实鼠标、多屏或 Windows DPI 验收。

工作区旁的 `*.update-settings.json`、`*.update-cache.json` 与其他本机配置一样由 `.gitignore` 忽略，任意目录内均适用；普通 JSON 示例仍可纳入版本控制。忽略规则不会删除或改写已有旁文件。

## 工作区外部修改与保存摘要回归

`WorkspaceStoreTests` 在独立临时目录使用存储层内部回调，确定性地把外部修改安排在临时文件完整写入后、最终校验前，以及替换目标后、保存返回前。覆盖直接改写、原子替换、删除和首次创建冲突，检查返回摘要与实际提交字节一致，下一次保存拒绝陈旧摘要，冲突与取消不留下临时文件。另验证同实例两个携带相同预期摘要的保存只允许一次成功，以及文件被占用时两种保存入口均保留原文件。

`WorkspaceSaveRegression` 共用现有 STA 宿主，使用真实项目编辑按钮与隔离工作区，检查正常保存、两个提交边界的外部替换、旧编辑回滚、禁用后不重复保存或提示，以及重新加载外部版本后恢复保存。外部替换由同步回调模拟，不依赖概率性的线程调度；这些回归不证明已有文件最后校验与替换之间对任意外部进程完全原子，也不替代真实编辑器、网络盘或人工桌面验收。

`StaleWorkspaceChecksDoNotDisableSaving` 使用延迟摘要读取替身，先挂起一次激活检查，再成功保存或重载工作区，最后返回此前读取的旧摘要。检查迟到结果不会禁用保存；修改进行中收到不同摘要也不会干扰该次操作，空闲且基线未改变时的真实外部删除仍会禁用保存。读取替身只控制激活检查，实际保存继续调用带预期摘要的存储入口。该回归在增加基线核对前确定性失败，用于覆盖普通机器与 CI 的调度差异。

## 历史日志尾部读取回归

`RunLogStoreTests` 对比标准逐行读取结果，覆盖空文件、UTF-8 BOM、中文与 emoji、LF/CR/CRLF、空行、末行无换行、超长行和跨 16 KiB 块的 CRLF。256 MiB 合成日志测试检查最后 10 行和实际读取字节数，保证读取只覆盖尾部；计时只记录本机结果，不用固定毫秒数作为通过门槛。可控的慢读取测试在持有已打开句柄时验证其他批次追加、完成、清理和迁移仍能完成，另检查文件长度快照、取消、缺失文件与参数校验。

`LogHistoryRegression` 在共用 STA 宿主和隔离工作区中挂起历史读取，确认实际读取运行在后台、Dispatcher 能响应、界面显示加载提示，再按相反顺序返回结果或错误，检查通道、批次、命令、项目切换和关闭后的旧回调被忽略。另检查空通道、完整性警告、当前读取失败，以及慢读取期间通过实际按钮运行合成任务后实时输出仍保留。既有更新回归改为等待历史加载完成再检查 stderr。

这些自动化检查不替代慢盘或网络盘实测、人工鼠标键盘操作及最终发布包验收。包含少量超长行的日志仍需读取这些行的全部字节，WPF 显示文本时的排版也仍在界面线程执行。

## 损坏日志容量与清理回归

`RunLogStoreTests` 使用独立临时工作区覆盖 `run.json` 损坏、缺失、JSON `null` 和空对象。1 MiB 上限下先写入 700,000 字节的历史输出，破坏元数据并重新打开，再写入新的批次；检查损坏历史先被轮转、实际批次文件总量不超限，再次大量输出时当前批次被截断且状态落盘。另验证写入失败后的容量重算不会漏掉损坏的活动批次。

清理回归检查损坏历史目录被删除，元数据同样异常的活动目录仍保留并继续写入；期限回归检查元数据不可用时采用目录修改时间，启动与修改设置均会清理过期历史，活动目录不受期限影响。

`MessageDialogRegression` 在现有 STA 桌面宿主中检查实际“清理历史日志”按钮：提示明确包含损坏或缺失的元数据批次，默认聚焦取消，取消保留文件，确认删除损坏历史并保留活动批次，历史列表刷新且显示清理完成状态。活动批次使用合成写入器，无需真实用户命令或工作区。

## 同时分组持续监视回归

`CommandGroupExecutionTests` 使用可控的合成完成任务，覆盖纯服务分组中已失败与稍后失败的成员、混合分组任务成功后的服务失败、任务成功通知只发送一次、服务正常退出或被停止后继续监视其他成员，以及空分组、纯任务和任务被停止的原有结果。

`ParallelGroupRegression` 接入 STA 桌面测试宿主，在独立临时工作区运行合成服务，使用标记文件控制失败时刻。纯服务及混合分组分别验证退出码、同次启动服务被停止和失败提示；混合分组先确认“任务已成功”仍保留运行中的服务，再触发失败。另检查手动启动并被跳过的服务仍存活，手动重新启动或再次运行分组产生的新会话不被旧执行清理，再次执行后的分组状态不被旧回调覆盖。清理只调用这些隔离会话的既有停止流程。

## 全零配置 ID 回归

`WorkspaceStoreTests` 使用独立临时文件覆盖 schema 1–4 的全零项目 ID 和命令 ID，检查 `LoadAsync`、`LoadSnapshotAsync` 及导入读取入口 `ReadFileAsync` 都以 `InvalidDataException` 拒绝，并逐字节确认原文件未改写。保存和导出回归分别检查拒绝全零 ID 后仍保留已有目标文件、不留下临时文件；既有往返测试继续检查有效 ID 保持不变。

`MessageDialogRegression` 在现有 STA 桌面宿主内加载两类异常文件，确认显示“无法加载工作区”、禁用保存与运行、主窗口仍可操作且原文件未改写。合成服务运行期间直接调用实际 `TryStartCommand` 方法传入全零项目或命令 ID，检查返回错误、没有新会话、日志批次或命令标记文件，已有服务仍在运行并能按原流程停止。导入异常文件由共用读取入口覆盖，本项不包含系统文件选择器的人工操作。

2026-10-04 全零 ID 检查结论：新增 Core 用例在修复前全部失败，修复后配置测试与 STA 桌面集成回归通过；Release 构建 0 警告、0 错误。已有异常配置仍须手工修复 ID，不自动重分配。未进行真实导入文件选择器验收，未运行 `scripts/Publish.ps1`，未修改版本或制作候选包。当次通过数量见[变更记录归档](changelog/implementation-log-v0.1.1-to-v0.2.0.md#全零-id-源码修复检查2026-10-04)。

## 窗口与布局回归

`MessageDialogRegression` 接入现有 STA 桌面集成宿主，使用临时工作区和合成消息检查四种提示类别、模态所有者、所有者居中、无主窗口提示、初始焦点、Enter/Esc、关闭取消与按钮结果。编辑校验关闭后返回原输入框；删除取消与确认分别检查配置保留和保存；项目、命令及分组三种导入冲突分别检查替换、保留、取消及命令 ID 映射，验证不创建运行会话或执行合成命令。使用真实合成服务和默认提示窗口检查运行中关闭、取消退出、模拟停止失败后窗口与命令仍保留、重试停止并退出；进程只由隔离窗口启动并按已有归属机制停止。既有托盘回归继续覆盖托盘出口与停止期间重复关闭。

提示布局检查包含长中文、长路径、多行消息、选中复制、正文滚动，以及 380 × 320 基础布局和 1、1.25、1.5、2 倍 WPF 布局缩放下的三按钮可达性。设置 `TALOSDESK_LAYOUT_CAPTURE_DIR` 后，提示截图与既有页面截图一同输出。WPF 布局缩放属于合成检查，不能替代真实 Windows DPI 切换和多屏验收。

`TrayIconControllerTests` 在 STA 窗口中使用托盘替身检查普通窗口边界、最大化恢复、反复切换、注册失败保留任务栏、通知区域重建及取消退出后的资源保留。`MainWindowIntegrationTests` 的托盘场景使用临时工作区与持续写入 stdout/stderr 的合成服务，验证隐藏时输出与磁盘日志继续增加、重复启动信号唤醒最大化窗口、取消退出、模拟停止失败、确认停止及停止期间的重复退出。失败由退出交互替身注入，不结束未知进程。

真实桌面检查需使用 `--workspace` 和 `--profile-label` 启动隔离窗口，检查最小化后任务栏隐藏、通知区域或折叠菜单中的图标、左键恢复、右键菜单及“×”退出。不得为验收而重启用户的资源管理器；`TaskbarCreated` 失败路径由替身覆盖，真实资源管理器重启验收单独报告。

在有可用 Windows 通知区域的本机，将测试进程环境变量 `TALOSDESK_NATIVE_TRAY_TEST` 设为 `1` 后运行 `MainWindowIntegrationTests`，可额外检查真实托盘 API 注册、原生图标资源加载、最小化隐藏、恢复及关闭后释放。这项本机检查不代替真实鼠标点击与菜单验收，CI 默认不依赖系统托盘可用。

`WindowSettingsStoreTests` 使用临时目录和合成屏幕数据验证隔离读写、损坏与失败回退、负坐标、DPI 换算及屏幕移除后的边界修正。桌面回归使用独立工作区，验证正常关闭、取消关闭、最大化、最小化及重新打开后的尺寸和位置，并检查 800 × 520、1050 × 650、1199/1200 宽度断点和 1400 × 860 下各页面及编辑弹窗的按钮可达性；运行、停止、重启及分组路径继续由既有集成测试覆盖。

设置测试进程环境变量 `TALOSDESK_LAYOUT_CAPTURE_DIR` 可将合成数据界面截图保存到指定测试产物目录。自动化的合成屏幕测试不代替真实多屏和 100%、125%、150%、200% Windows 显示缩放实测；交付时分别说明实际执行与未执行的项目。

CI 的虚拟桌面可能限制顶层窗口尺寸。布局断言依据实际显示宽度，并记录请求尺寸、实际尺寸和工作区；额外使用窗口尺寸上限，在本机也覆盖“请求宽布局但实际只能显示窄布局”的情况。位置往返测试先将窗口完整放入可用工作区，避免把正常的屏幕边界修正误判为恢复失败。较小 runner 上的通过不代表所有请求尺寸均已原样显示。

布局回归等待隔离工作区的首次读取和启动后异步重载完成，再开始访问分组控件；“检查更新”按钮可用不能代替启动重载完成。滚动请求后的消息处理可能重建按钮，检查会依据名称、内容和数据上下文重新定位当前控件，最多尝试三次，再执行原有尺寸和裁切断言。合成场景主动在滚动请求后替换按钮，验证旧可视树不会再参与坐标转换；另移除按钮并确认测试失败，避免因跳过已脱离窗口的控件而漏检。
