# 隔离测试说明

所有桌面和集成测试都应把工作区放在独立临时目录，并传入明确身份：

```powershell
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("TalosDesk-Manual-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$workspace = Join-Path $testRoot "workspace.json"
.\src\TalosDesk.App\bin\Release\net10.0-windows\TalosDesk.App.exe --workspace $workspace --profile-label "隔离测试"
```

测试结束后先关闭应用并确认合成命令已经退出，再删除这个明确的临时目录。不要把 `%LOCALAPPDATA%\TalosDesk\workspace.json` 用于自动化。

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

2026-10-04 桌面回归拆分检查：仅导出本次暂存源码到独立目录验证，完整自动化测试 173 通过（Core 144、App 29），0 失败、0 跳过，其中六套桌面回归及四项宿主回归分别报告通过；Release 构建 0 警告、0 错误。该快照不包含另行开发的同时分组改动。结果保存在 artifacts/tests/desktop-split/commit/。忽略规则已验证根目录及嵌套路径匹配，普通 JSON 不被误忽略；原有两个更新旁文件仍保留。未启用真实托盘 API 检查，未执行真实鼠标、多屏或 Windows DPI 人工验收，未运行 scripts/Publish.ps1，未修改版本或制作发布候选包。共享 STA 卡死仍需结束该次测试运行。

## 工作区外部修改与保存摘要回归

`WorkspaceStoreTests` 在独立临时目录使用存储层内部回调，确定性地把外部修改安排在临时文件完整写入后、最终校验前，以及替换目标后、保存返回前。覆盖直接改写、原子替换、删除和首次创建冲突，检查返回摘要与实际提交字节一致，下一次保存拒绝陈旧摘要，冲突与取消不留下临时文件。另验证同实例两个携带相同预期摘要的保存只允许一次成功，以及文件被占用时两种保存入口均保留原文件。

`WorkspaceSaveRegression` 共用现有 STA 宿主，使用真实项目编辑按钮与隔离工作区，检查正常保存、两个提交边界的外部替换、旧编辑回滚、禁用后不重复保存或提示，以及重新加载外部版本后恢复保存。外部替换由同步回调模拟，不依赖概率性的线程调度；这些回归不证明已有文件最后校验与替换之间对任意外部进程完全原子，也不替代真实编辑器、网络盘或人工桌面验收。

`StaleWorkspaceChecksDoNotDisableSaving` 使用延迟摘要读取替身，先挂起一次激活检查，再成功保存或重载工作区，最后返回此前读取的旧摘要。检查迟到结果不会禁用保存；修改进行中收到不同摘要也不会干扰该次操作，空闲且基线未改变时的真实外部删除仍会禁用保存。读取替身只控制激活检查，实际保存继续调用带预期摘要的存储入口。该回归在增加基线核对前确定性失败，用于覆盖普通机器与 CI 的调度差异。

2026-10-05 外部修改源码修复检查：临时恢复旧桌面保存流程后，受控的保存后外部替换回归因摘要误归属而失败；恢复修复后，核心保存及 STA 桌面回归通过。使用仓库锁定 SDK 的完整自动化测试 217 通过（Core 185、App 32），0 失败、0 跳过；Release 构建 0 警告、0 错误，`git diff --check` 通过。旧流程失败记录位于 `artifacts/tests/workspace-save/red/`，Core/App 最终独立 TRX 位于 `artifacts/tests/workspace-save/final/`。未执行真实外部编辑器并发、网络盘、人工鼠标键盘或最终发布包验收，未运行 `scripts/Publish.ps1`，未修改版本或制作候选包；源码提交与推送按用户明确授权执行。已有目标的最后校验与替换之间仍保留设计说明中的极短竞争窗口。

2026-10-05 激活检查 CI 修复：提交 `ea1a75b` 的 [Windows CI](https://github.com/kongerly/TalosDesk/actions/runs/37307344163) 为 Core 185 通过、App 31 通过及 1 失败，失败时提交前的外部修改回调尚未执行。可控延迟回归在旧判断中确定性复现本机保存成功后的摘要误判；增加读取前后基线与修改状态核对后，两项保存相关桌面回归通过。使用与 CI 相同的 Release 构建及 `--no-build --no-restore` 全量测试入口，本机 218 通过（Core 185、App 33），0 失败、0 跳过；构建 0 警告、0 错误，`git diff --check` 通过。失败复现及最终 TRX 位于 `artifacts/tests/workspace-save-ci/`。远端结果按具体提交独立核对；本次不改版本、不制作候选包，未运行 `scripts/Publish.ps1` 或真实外部编辑器、网络盘与人工桌面验收。源码提交与推送按用户已有授权执行。

## 历史日志尾部读取回归

`RunLogStoreTests` 对比标准逐行读取结果，覆盖空文件、UTF-8 BOM、中文与 emoji、LF/CR/CRLF、空行、末行无换行、超长行和跨 16 KiB 块的 CRLF。256 MiB 合成日志测试检查最后 10 行和实际读取字节数，保证读取只覆盖尾部；计时只记录本机结果，不用固定毫秒数作为通过门槛。可控的慢读取测试在持有已打开句柄时验证其他批次追加、完成、清理和迁移仍能完成，另检查文件长度快照、取消、缺失文件与参数校验。

`LogHistoryRegression` 在共用 STA 宿主和隔离工作区中挂起历史读取，确认实际读取运行在后台、Dispatcher 能响应、界面显示加载提示，再按相反顺序返回结果或错误，检查通道、批次、命令、项目切换和关闭后的旧回调被忽略。另检查空通道、完整性警告、当前读取失败，以及慢读取期间通过实际按钮运行合成任务后实时输出仍保留。既有更新回归改为等待历史加载完成再检查 stderr。

这些自动化检查不替代慢盘或网络盘实测、人工鼠标键盘操作及最终发布包验收。包含少量超长行的日志仍需读取这些行的全部字节，WPF 显示文本时的排版也仍在界面线程执行。

2026-10-05 历史读取源码修复检查：日志 Core 测试 32 项和新增 STA 桌面回归通过；最终完整自动化测试 204 通过（Core 173、App 31），0 失败、0 跳过，Release 构建 0 警告、0 错误，`git diff --check` 通过。最终全量测试中，256 MiB 合成日志最后 10 行读取 16,514 字节，本机单次耗时 1.022 ms；该数值不代表慢盘的性能保证。通过仓库锁定 SDK 执行 `test TalosDesk.slnx --no-restore --configuration Release --logger trx` 和 `build TalosDesk.slnx --no-restore --configuration Release`，独立 TRX 结果位于 `artifacts/tests/log-tail/final/`。未执行慢盘/网络盘、人工鼠标键盘或最终发布包验收，未运行 `scripts/Publish.ps1`，未修改版本或制作候选包；源码提交与推送按用户明确授权执行。

## 损坏日志容量与清理回归

`RunLogStoreTests` 使用独立临时工作区覆盖 `run.json` 损坏、缺失、JSON `null` 和空对象。1 MiB 上限下先写入 700,000 字节的历史输出，破坏元数据并重新打开，再写入新的批次；检查损坏历史先被轮转、实际批次文件总量不超限，再次大量输出时当前批次被截断且状态落盘。另验证写入失败后的容量重算不会漏掉损坏的活动批次。

清理回归检查损坏历史目录被删除，元数据同样异常的活动目录仍保留并继续写入；期限回归检查元数据不可用时采用目录修改时间，启动与修改设置均会清理过期历史，活动目录不受期限影响。

`MessageDialogRegression` 在现有 STA 桌面宿主中检查实际“清理历史日志”按钮：提示明确包含损坏或缺失的元数据批次，默认聚焦取消，取消保留文件，确认删除损坏历史并保留活动批次，历史列表刷新且显示清理完成状态。活动批次使用合成写入器，无需真实用户命令或工作区。

2026-10-05 损坏日志源码修复检查：新增 11 个 Core 用例在修复前全部失败，修复后日志测试 22 项全部通过，STA 桌面清理回归通过。完整自动化测试 193 通过（Core 163、App 30），0 失败、0 跳过；Release 构建 0 警告、0 错误，`git diff --check` 通过。结果保存在 `artifacts/tests/corrupt-run-log/`。未执行人工鼠标键盘或最终发布包验收，未运行 `scripts/Publish.ps1`，未修改版本或制作候选包；源码提交与推送按用户明确授权执行。损坏元数据的批次仍不出现在历史列表中，期限及轮转顺序使用目录修改时间，不尝试恢复元数据。

## 同时分组持续监视回归

`CommandGroupExecutionTests` 使用可控的合成完成任务，覆盖纯服务分组中已失败与稍后失败的成员、混合分组任务成功后的服务失败、任务成功通知只发送一次、服务正常退出或被停止后继续监视其他成员，以及空分组、纯任务和任务被停止的原有结果。

`ParallelGroupRegression` 接入 STA 桌面测试宿主，在独立临时工作区运行合成服务，使用标记文件控制失败时刻。纯服务及混合分组分别验证退出码、同次启动服务被停止和失败提示；混合分组先确认“任务已成功”仍保留运行中的服务，再触发失败。另检查手动启动并被跳过的服务仍存活，手动重新启动或再次运行分组产生的新会话不被旧执行清理，再次执行后的分组状态不被旧回调覆盖。清理只调用这些隔离会话的既有停止流程。

2026-10-04 同时分组源码修复检查：三个缺陷回归在修复前全部失败，修复后分组 Core 测试 15 项通过，隔离桌面四种场景通过。当前工作区完整自动化测试 182 通过（Core 152、App 30），0 失败、0 跳过；Release 构建 0 警告、0 错误。为避开并行工作的共享构建文件占用，构建使用 `--artifacts-path artifacts/parallel-group-build`；测试记录保存在 `artifacts/tests/parallel-group-regression/`。未进行人工鼠标键盘或最终发布包验收，未运行 `scripts/Publish.ps1`，未修改版本或制作候选包；该检查阶段保留已有未提交改动，后续源码提交与推送按用户明确授权执行。

## 全零配置 ID 回归

`WorkspaceStoreTests` 使用独立临时文件覆盖 schema 1–4 的全零项目 ID 和命令 ID，检查 `LoadAsync`、`LoadSnapshotAsync` 及导入读取入口 `ReadFileAsync` 都以 `InvalidDataException` 拒绝，并逐字节确认原文件未改写。保存和导出回归分别检查拒绝全零 ID 后仍保留已有目标文件、不留下临时文件；既有往返测试继续检查有效 ID 保持不变。

`MessageDialogRegression` 在现有 STA 桌面宿主内加载两类异常文件，确认显示“无法加载工作区”、禁用保存与运行、主窗口仍可操作且原文件未改写。合成服务运行期间直接调用实际 `TryStartCommand` 方法传入全零项目或命令 ID，检查返回错误、没有新会话、日志批次或命令标记文件，已有服务仍在运行并能按原流程停止。导入异常文件由共用读取入口覆盖，本项不包含系统文件选择器的人工操作。

2026-10-04 全零 ID 源码修复检查：新增的 10 个 Core 用例在修复前全部失败，修复后配置测试 22 项通过；STA 桌面集成回归通过。随后完整自动化测试 164 通过（Core 144、App 20），0 失败、0 跳过；Release 构建 0 警告、0 错误。结果保存在 `artifacts/tests/empty-id-regression/`。未进行人工导入文件选择器验收，未运行 `scripts/Publish.ps1`，未修改版本或制作发布候选包。已有异常配置仍须手工修复 ID，不自动重分配；源码提交与推送按用户明确授权执行。

## 窗口与布局回归

`MessageDialogRegression` 接入现有 STA 桌面集成宿主，使用临时工作区和合成消息检查四种提示类别、模态所有者、所有者居中、无主窗口提示、初始焦点、Enter/Esc、关闭取消与按钮结果。编辑校验关闭后返回原输入框；删除取消与确认分别检查配置保留和保存；项目、命令及分组三种导入冲突分别检查替换、保留、取消及命令 ID 映射，验证不创建运行会话或执行合成命令。使用真实合成服务和默认提示窗口检查运行中关闭、取消退出、模拟停止失败后窗口与命令仍保留、重试停止并退出；进程只由隔离窗口启动并按已有归属机制停止。既有托盘回归继续覆盖托盘出口与停止期间重复关闭。

提示布局检查包含长中文、长路径、多行消息、选中复制、正文滚动，以及 380 × 320 基础布局和 1、1.25、1.5、2 倍 WPF 布局缩放下的三按钮可达性。设置 `TALOSDESK_LAYOUT_CAPTURE_DIR` 后，提示截图与既有页面截图一同输出。WPF 布局缩放属于合成检查，不能替代真实 Windows DPI 切换和多屏验收。

2026-10-04 提示窗口源码检查：完整自动化测试 154 通过（Core 134、App 20），0 失败、0 跳过；Release 构建 0 警告、0 错误。新提示回归接入现有桌面集成测试，已检查四类提示截图、长消息、窄窗口及上述 WPF 布局缩放；实际运行中取消退出、模拟停止失败提示和重试停止并退出均通过。复制测试检查复制事件的数据并取消系统剪贴板写入，不覆盖用户剪贴板。扫描确认 62 处应用提示均使用统一入口，无原生 `MessageBox.Show` 调用。未进行真实多屏、Windows 显示缩放切换或人工鼠标键盘验收；未运行 `scripts/Publish.ps1`，未修改版本或打包。源码提交与推送另按用户明确授权执行。

`TrayIconControllerTests` 在 STA 窗口中使用托盘替身检查普通窗口边界、最大化恢复、反复切换、注册失败保留任务栏、通知区域重建及取消退出后的资源保留。`MainWindowIntegrationTests` 的托盘场景使用临时工作区与持续写入 stdout/stderr 的合成服务，验证隐藏时输出与磁盘日志继续增加、重复启动信号唤醒最大化窗口、取消退出、模拟停止失败、确认停止及停止期间的重复退出。失败由退出交互替身注入，不结束未知进程。

真实桌面检查需使用 `--workspace` 和 `--profile-label` 启动隔离窗口，检查最小化后任务栏隐藏、通知区域或折叠菜单中的图标、左键恢复、右键菜单及“×”退出。不得为验收而重启用户的资源管理器；`TaskbarCreated` 失败路径由替身覆盖，真实资源管理器重启验收单独报告。

在有可用 Windows 通知区域的本机，将测试进程环境变量 `TALOSDESK_NATIVE_TRAY_TEST` 设为 `1` 后运行 `MainWindowIntegrationTests`，可额外检查真实托盘 API 注册、原生图标资源加载、最小化隐藏、恢复及关闭后释放。这项本机检查不代替真实鼠标点击与菜单验收，CI 默认不依赖系统托盘可用。

2026-10-03 本机托盘源码检查：设置 `TALOSDESK_NATIVE_TRAY_TEST=1` 后，全量自动化测试 144 通过（Core 124、App 20），0 失败、0 跳过；Release 构建 0 警告、0 错误。使用独立临时工作区启动源码构建的应用，并确认该测试进程正常关闭。Computer Use 返回 `Computer Use was not approved to use app`，因此未完成真实鼠标点击、右键菜单与任务栏可见性的人工验收；资源管理器重启也未实测。本批仅提交源码，不制作发布候选包，也不运行 `scripts/Publish.ps1`。

2026-10-03 桌面操作重试：Computer Use 已能操作独立临时工作区的源码窗口。实际点击最小化后，该窗口不再是可交互窗口，原进程仍在；再次启动同一工作区后，原窗口恢复可交互；点击右上角“×”后进程退出。工具未提供 Windows 通知区域作为可操作目标，因此托盘图标左键、右键菜单及任务栏图标显示仍未完成真实鼠标验收。重试未改动应用代码，也未运行发布打包。

`WindowSettingsStoreTests` 使用临时目录和合成屏幕数据验证隔离读写、损坏与失败回退、负坐标、DPI 换算及屏幕移除后的边界修正。桌面回归使用独立工作区，验证正常关闭、取消关闭、最大化、最小化及重新打开后的尺寸和位置，并检查 800 × 520、1050 × 650、1199/1200 宽度断点和 1400 × 860 下各页面及编辑弹窗的按钮可达性；运行、停止、重启及分组路径继续由既有集成测试覆盖。

设置测试进程环境变量 `TALOSDESK_LAYOUT_CAPTURE_DIR` 可将合成数据界面截图保存到指定测试产物目录。自动化的合成屏幕测试不代替真实多屏和 100%、125%、150%、200% Windows 显示缩放实测；交付时分别说明实际执行与未执行的项目。

CI 的虚拟桌面可能限制顶层窗口尺寸。布局断言依据实际显示宽度，并记录请求尺寸、实际尺寸和工作区；额外使用窗口尺寸上限，在本机也覆盖“请求宽布局但实际只能显示窄布局”的情况。位置往返测试先将窗口完整放入可用工作区，避免把正常的屏幕边界修正误判为恢复失败。较小 runner 上的通过不代表所有请求尺寸均已原样显示。

布局回归等待隔离工作区的首次读取和启动后异步重载完成，再开始访问分组控件；“检查更新”按钮可用不能代替启动重载完成。滚动请求后的消息处理可能重建按钮，检查会依据名称、内容和数据上下文重新定位当前控件，最多尝试三次，再执行原有尺寸和裁切断言。合成场景主动在滚动请求后替换按钮，验证旧可视树不会再参与坐标转换；另移除按钮并确认测试失败，避免因跳过已脱离窗口的控件而漏检。

2026-10-04 CI 布局稳定性修复：`28529aa` 的 Windows CI 在分组页布局检查中因控件脱离可视树而失败，Core 152 项和其余 App 29 项通过。新增的按钮重建场景在修复前稳定复现相同坐标转换异常，修复后布局回归通过；按 CI 的 Release 构建及全量测试命令重新检查，182 通过（Core 152、App 30），0 失败、0 跳过，构建 0 警告、0 错误。记录保存在 `artifacts/tests/ci-layout-regression/`，远端结果以对应 GitHub Actions 运行记录为准。本次只修改测试与说明文档，未执行真实鼠标、多屏、Windows DPI 或最终发布包验收，未运行 `scripts/Publish.ps1`。

2026-10-02 本机检查：完整自动化测试 144 通过（Core 124、App 20），0 失败、0 跳过；Release 构建 0 警告、0 错误。已检查隔离窗口截图、窗口自动恢复和各尺寸下的按钮可达性；未进行真实多屏与全部 Windows 缩放档位实测，未执行发布打包脚本。

2026-10-03 桌面版更新收尾：`scripts/Publish.ps1` 已完整通过还原、Release 构建、144 项测试和自包含打包，ZIP 的 SHA-256 与校验文件一致。已备份并更新本机快捷方式对应的便携版，保留原工作区参数；使用独立临时工作区验证最终便携版启动、调整窗口、正常关闭与再次打开后的边界恢复。该检查不替代最终便携版的完整命令运行、停止及异常路径验收；真实多屏与全部显示缩放档位仍未逐一实测。本次不创建远端标签或公开发布。
