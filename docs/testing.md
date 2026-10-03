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

## 窗口与布局回归

`TrayIconControllerTests` 在 STA 窗口中使用托盘替身检查普通窗口边界、最大化恢复、反复切换、注册失败保留任务栏、通知区域重建及取消退出后的资源保留。`MainWindowIntegrationTests` 的托盘场景使用临时工作区与持续写入 stdout/stderr 的合成服务，验证隐藏时输出与磁盘日志继续增加、重复启动信号唤醒最大化窗口、取消退出、模拟停止失败、确认停止及停止期间的重复退出。失败由退出交互替身注入，不结束未知进程。

真实桌面检查需使用 `--workspace` 和 `--profile-label` 启动隔离窗口，检查最小化后任务栏隐藏、通知区域或折叠菜单中的图标、左键恢复、右键菜单及“×”退出。不得为验收而重启用户的资源管理器；`TaskbarCreated` 失败路径由替身覆盖，真实资源管理器重启验收单独报告。

在有可用 Windows 通知区域的本机，将测试进程环境变量 `TALOSDESK_NATIVE_TRAY_TEST` 设为 `1` 后运行 `MainWindowIntegrationTests`，可额外检查真实托盘 API 注册、原生图标资源加载、最小化隐藏、恢复及关闭后释放。这项本机检查不代替真实鼠标点击与菜单验收，CI 默认不依赖系统托盘可用。

2026-10-03 本机托盘源码检查：设置 `TALOSDESK_NATIVE_TRAY_TEST=1` 后，全量自动化测试 144 通过（Core 124、App 20），0 失败、0 跳过；Release 构建 0 警告、0 错误。使用独立临时工作区启动源码构建的应用，并确认该测试进程正常关闭。Computer Use 返回 `Computer Use was not approved to use app`，因此未完成真实鼠标点击、右键菜单与任务栏可见性的人工验收；资源管理器重启也未实测。本批仅提交源码，不制作发布候选包，也不运行 `scripts/Publish.ps1`。

`WindowSettingsStoreTests` 使用临时目录和合成屏幕数据验证隔离读写、损坏与失败回退、负坐标、DPI 换算及屏幕移除后的边界修正。桌面回归使用独立工作区，验证正常关闭、取消关闭、最大化、最小化及重新打开后的尺寸和位置，并检查 800 × 520、1050 × 650、1199/1200 宽度断点和 1400 × 860 下各页面及编辑弹窗的按钮可达性；运行、停止、重启及分组路径继续由既有集成测试覆盖。

设置测试进程环境变量 `TALOSDESK_LAYOUT_CAPTURE_DIR` 可将合成数据界面截图保存到指定测试产物目录。自动化的合成屏幕测试不代替真实多屏和 100%、125%、150%、200% Windows 显示缩放实测；交付时分别说明实际执行与未执行的项目。

CI 的虚拟桌面可能限制顶层窗口尺寸。布局断言依据实际显示宽度，并记录请求尺寸、实际尺寸和工作区；额外使用窗口尺寸上限，在本机也覆盖“请求宽布局但实际只能显示窄布局”的情况。位置往返测试先将窗口完整放入可用工作区，避免把正常的屏幕边界修正误判为恢复失败。较小 runner 上的通过不代表所有请求尺寸均已原样显示。

2026-10-02 本机检查：完整自动化测试 144 通过（Core 124、App 20），0 失败、0 跳过；Release 构建 0 警告、0 错误。已检查隔离窗口截图、窗口自动恢复和各尺寸下的按钮可达性；未进行真实多屏与全部 Windows 缩放档位实测，未执行发布打包脚本。

2026-10-03 桌面版更新收尾：`scripts/Publish.ps1` 已完整通过还原、Release 构建、144 项测试和自包含打包，ZIP 的 SHA-256 与校验文件一致。已备份并更新本机快捷方式对应的便携版，保留原工作区参数；使用独立临时工作区验证最终便携版启动、调整窗口、正常关闭与再次打开后的边界恢复。该检查不替代最终便携版的完整命令运行、停止及异常路径验收；真实多屏与全部显示缩放档位仍未逐一实测。本次不创建远端标签或公开发布。
